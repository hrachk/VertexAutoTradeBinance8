using Microsoft.JSInterop;

namespace VertexAutoTradeBinance8.Web.Services.Auth;

/// <summary>
/// Scoped per Blazor circuit. Holds the currently logged-in client.
/// On first load, tries to restore session from the vertex_auth cookie
/// via JS interop → SessionTokenService → ClientDbService.
/// </summary>
public sealed class AuthSessionService : IAsyncDisposable
{
    public ClientRecord?  CurrentClient   { get; private set; }
    public bool           IsAuthenticated => CurrentClient != null;

    /// <summary>True when user is in demo mode (virtual balance, no real orders).</summary>
    public bool           IsDemo          => _demoMode || CurrentClient is null
                                             || string.Equals(CurrentClient.TradingMode, "demo", StringComparison.OrdinalIgnoreCase)
                                             || !CurrentClient.IsLiveEnabled;

    /// <summary>True only when user explicitly chose live AND has API keys.</summary>
    public bool           IsLive          => !_demoMode
                                             && CurrentClient is { IsLiveEnabled: true }
                                             && string.Equals(CurrentClient.TradingMode, "live", StringComparison.OrdinalIgnoreCase);

    // Runtime toggle (also persisted on ClientRecord.TradingMode + cookie)
    private bool _demoMode = true;
    public  bool DemoMode  => _demoMode;

    public event Action? OnChange;

    private readonly ClientDbService      _db;
    private readonly SessionTokenService  _tokens;
    private readonly ILogger<AuthSessionService> _log;
    private readonly VertexAutoTradeBinance8.Web.Services.DemoAccountService _demo;
    private IJSRuntime? _js;
    private bool        _initialized;

    // Cookie name and JS interop key
    public const string CookieName  = "vertex_auth";
    public const string DemoCookie  = "vertex_demo";

    private readonly EmailService _email;

    public AuthSessionService(
        ClientDbService db,
        SessionTokenService tokens,
        EmailService email,
        VertexAutoTradeBinance8.Web.Services.DemoAccountService demo,
        ILogger<AuthSessionService> log)
    {
        _db     = db;
        _tokens = tokens;
        _email  = email;
        _demo   = demo;
        _log    = log;
    }


    private void BindDemoForClient(ClientRecord client)
    {
        try
        {
            _db.EnsureClientDataFolder(client.Id);
            _demo.BindClient(client.Id, client.DemoBalance > 0 ? client.DemoBalance : 10_000m);

            // Restore trading mode from client record (source of truth)
            var mode = (client.TradingMode ?? "demo").ToLowerInvariant();
            if (mode == "live" && client.IsLiveEnabled)
                _demoMode = false;
            else
            {
                _demoMode = true;
                if (mode != "demo")
                    client.TradingMode = "demo";
            }
            _demo.SetDemoMode(_demoMode);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[SESSION] BindDemoForClient failed for {id}", client.Id);
        }
    }

    // ── Initialize: restore from cookie ──────────────────────
    // Call from OnAfterRenderAsync(firstRender=true) in MainLayout.
    public async Task InitializeAsync(IJSRuntime js)
    {
        if (_initialized) return;
        _initialized = true;
        _js = js;

        try
        {
            // Read auth cookie via JS
            var token = await js.InvokeAsync<string?>("vertexAuth.getCookie", CookieName);
            if (!string.IsNullOrEmpty(token))
            {
                var clientId = await _tokens.ValidateAsync(token);
                if (clientId != null)
                {
                    var client = await _db.FindByIdAsync(clientId);
                    if (client != null && client.IsActive)
                    {
                        CurrentClient = client;
                        BindDemoForClient(client);
                        _log.LogInformation("[SESSION] Restored from cookie: {id}", clientId);
                        OnChange?.Invoke();
                    }
                }
            }

            // Restore demo mode preference
            var demoVal = await js.InvokeAsync<string?>("vertexAuth.getCookie", DemoCookie);
            _demoMode = demoVal != "live";
        }
        catch (Exception ex)
        {
            _log.LogDebug("[SESSION] Cookie restore failed: {msg}", ex.Message);
        }
    }

    // ── Login ─────────────────────────────────────────────────
    public async Task<(bool ok, string error)> LoginAsync(
        string email, string password, bool rememberMe = true)
    {
        var (ok, error, client) = await _db.LoginAsync(email, password);
        if (!ok || client == null) return (false, error);

        CurrentClient = client;
        BindDemoForClient(client);
        _log.LogInformation("[SESSION] Login: {id}, rememberMe={rm}", client.Id, rememberMe);

        // Create persistent token and set cookie
        var token   = await _tokens.CreateAsync(client.Id, rememberMe);
        var days    = rememberMe ? SessionTokenService.RememberMeDays : 0;
        if (_js != null)
            await SetCookieAsync(CookieName, token, days);

        OnChange?.Invoke();
        return (true, "");
    }

    // ── OAuth login ───────────────────────────────────────────
    /// <summary>
    /// Called after OAuthService validates the token.
    /// Finds or creates the account and sets the session cookie.
    /// </summary>
    public async Task<(bool ok, string error, bool isNew)>
        OAuthLoginAsync(OAuthUserInfo info)
    {
        try
        {
            var (client, isNew) = await _db.OAuthFindOrCreateAsync(info);
            if (!client.IsActive) return (false, "Аккаунт заблокирован.", false);

            CurrentClient = client;
            BindDemoForClient(client);

            var token = await _tokens.CreateAsync(client.Id, rememberMe: true);
            if (_js != null)
                await SetCookieAsync(CookieName, token, SessionTokenService.RememberMeDays);

            OnChange?.Invoke();
            return (true, "", isNew);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[SESSION] OAuthLogin failed for {p}", info.Provider);
            return (false, "Ошибка авторизации. Попробуйте снова.", false);
        }
    }

    // ── Register ──────────────────────────────────────────────
    public async Task<(bool ok, string error)>
        RegisterAsync(string email, string password, string displayName)
    {
        var (ok, error, client) = await _db.RegisterAsync(email, password, displayName);
        if (!ok || client == null) return (false, error);

        CurrentClient = client;
        BindDemoForClient(client);
        // Don't set session cookie yet — require email verification first
        // (cookie will be set in ConfirmEmailAsync after code is verified)

        // Generate and send verification code
        _ = Task.Run(async () =>
        {
            try
            {
                var code = await _db.GenerateVerifyCodeAsync(client.Id);
                if (!string.IsNullOrEmpty(code))
                    await _email.SendVerificationCodeAsync(client.Email, client.DisplayName, code);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[SESSION] Failed to send verification email to {email}", client.Email);
            }
        });

        OnChange?.Invoke();
        return (true, "");
    }

    // ── Email verification ────────────────────────────────────
    /// <summary>
    /// Verifies the 6-digit code entered by user.
    /// On success: sets IsEmailVerified=true, creates session cookie.
    /// </summary>
    public async Task<(bool ok, string error)> ConfirmEmailAsync(string code)
    {
        if (CurrentClient == null) return (false, "Сессия истекла. Войдите снова.");

        var (ok, error) = await _db.VerifyEmailAsync(CurrentClient.Id, code.Trim());
        if (!ok) return (false, error);

        // Refresh client record to get IsEmailVerified=true
        await RefreshAsync();

        // Now set the auth cookie — user is fully verified
        var token = await _tokens.CreateAsync(CurrentClient.Id, rememberMe: true);
        if (_js != null)
            await SetCookieAsync(CookieName, token, SessionTokenService.RememberMeDays);

        // Send welcome email in background
        _ = Task.Run(async () =>
        {
            try { await _email.SendWelcomeAsync(CurrentClient!.Email, CurrentClient!.DisplayName); }
            catch { }
        });

        OnChange?.Invoke();
        return (true, "");
    }

    /// <summary>
    /// Generates a new verification code and re-sends it to the user's email.
    /// Returns true if email was sent (or dev mode — always true).
    /// </summary>
    public async Task<bool> ResendVerificationCodeAsync()
    {
        if (CurrentClient == null) return false;
        try
        {
            var code = await _db.GenerateVerifyCodeAsync(CurrentClient.Id);
            if (string.IsNullOrEmpty(code)) return false;
            return await _email.SendVerificationCodeAsync(
                CurrentClient.Email, CurrentClient.DisplayName, code);
        }
        catch { return false; }
    }

    // ── Logout ────────────────────────────────────────────────────
    public async Task LogoutAsync()
    {
        _log.LogInformation("[SESSION] Logout: {id}", CurrentClient?.Id);

        // Revoke server-side token
        if (_js != null)
        {
            try
            {
                var token = await _js.InvokeAsync<string?>("vertexAuth.getCookie", CookieName);
                if (!string.IsNullOrEmpty(token))
                    await _tokens.RevokeAsync(token);
            }
            catch { }
            // Clear cookie
            await SetCookieAsync(CookieName, "", -1);
        }

        CurrentClient = null;
        _demoMode = true;
        try { _demo.UnbindClient(); } catch { }
        OnChange?.Invoke();
    }

    // ── Legacy sync Logout (for backward compat) ──────────────
    public void Logout()
    {
        CurrentClient = null;
        try { _demo.UnbindClient(); } catch { }
        _demoMode = true;
        if (_js != null)
            _ = SetCookieAsync(CookieName, "", -1);
        OnChange?.Invoke();
    }

    // ── Refresh ───────────────────────────────────────────────
    public async Task RefreshAsync()
    {
        if (CurrentClient == null) return;
        var fresh = await _db.FindByIdAsync(CurrentClient.Id);
        if (fresh != null)
        {
            CurrentClient = fresh;
            BindDemoForClient(fresh);
            OnChange?.Invoke();
        }
    }

    // ── Helpers ───────────────────────────────────────────────
    private async Task SetCookieAsync(string name, string value, int days)
    {
        if (_js == null) return;
        try
        {
            await _js.InvokeVoidAsync("vertexAuth.setCookie", name, value, days);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        // Nothing to dispose — JS runtime handles cleanup
    }

    /// <summary>
    /// Explicit demo ↔ live switch. Live requires saved Binance API keys.
    /// Persists to ClientRecord.TradingMode and demo mode cookie.
    /// </summary>
    public async Task<(bool ok, string error)> SwitchTradingModeAsync(string mode)
    {
        if (CurrentClient == null)
            return (false, "Необходимо войти в аккаунт.");

        mode = (mode ?? "demo").Trim().ToLowerInvariant();
        var (ok, error) = await _db.SetTradingModeAsync(CurrentClient.Id, mode);
        if (!ok) return (false, error);

        // Refresh client from disk
        var refreshed = await _db.FindByIdAsync(CurrentClient.Id);
        if (refreshed != null)
            CurrentClient = refreshed;

        _demoMode = mode != "live";
        _demo.SetDemoMode(_demoMode);

        if (_js != null)
        {
            try { await _js.InvokeVoidAsync("vertexAuth.setCookie", DemoCookie, _demoMode ? "1" : "0", 365); }
            catch { /* non-fatal */ }
        }

        OnChange?.Invoke();
        _log.LogInformation("[SESSION] TradingMode → {mode} for {id}", mode, CurrentClient.Id);
        return (true, "");
    }

    /// <summary>Legacy toggle used by MainLayout sticky switch.</summary>
    public async Task SetDemoModeAsync(bool enabled)
    {
        await SwitchTradingModeAsync(enabled ? "demo" : "live");
    }

}

