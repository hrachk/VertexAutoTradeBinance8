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

    /// <summary>True when virtual demo trading is active (no real Binance orders).</summary>
    public bool IsDemo => CurrentClient == null || _demoMode || !CurrentClient.IsLiveEnabled;

    /// <summary>True only with API keys + explicit LIVE mode.</summary>
    public bool IsLive => CurrentClient is { IsLiveEnabled: true } && !_demoMode;

    // Runtime flag; source of truth after bind = ClientRecord.TradingMode + keys
    private bool _demoMode = true;
    public  bool DemoMode  => IsDemo;

    public event Action? OnChange;

    private readonly ClientDbService      _db;
    private readonly SessionTokenService  _tokens;
    private readonly ILogger<AuthSessionService> _log;
    private readonly VertexAutoTradeBinance8.Web.Services.DemoAccountService _demo;
    private readonly VertexAutoTradeBinance8.Services.TradingCredentialStore _liveCreds;
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
        VertexAutoTradeBinance8.Services.TradingCredentialStore liveCreds,
        ILogger<AuthSessionService> log)
    {
        _db        = db;
        _tokens    = tokens;
        _email     = email;
        _demo      = demo;
        _liveCreds = liveCreds;
        _log       = log;
    }


    private void BindDemoForClient(ClientRecord client)
    {
        try
        {
            _db.EnsureClientDataFolder(client.Id);
            _demo.BindClient(client.Id, client.DemoBalance > 0 ? client.DemoBalance : 10_000m);

            // Source of truth: ClientRecord.TradingMode + IsLiveEnabled.
            // If user has API keys and TradingMode is live (or was never set but
            // keys exist and Plan is live) → LIVE. Otherwise DEMO.
            var mode = (client.TradingMode ?? "").Trim().ToLowerInvariant();
            bool wantLive = client.IsLiveEnabled && (
                mode == "live" ||
                (string.IsNullOrEmpty(mode) && string.Equals(client.Plan, "live", StringComparison.OrdinalIgnoreCase)));

            _demoMode = !wantLive;
            _demo.SetDemoMode(_demoMode);

            // Sync LIVE credentials into process-wide store (or clear for demo)
            _ = SyncLiveCredentialsAsync(client, live: wantLive);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[SESSION] BindDemoForClient failed for {id}", client.Id);
        }
    }

    /// <summary>
    /// Loads decrypted Binance keys for this user into TradingCredentialStore
    /// when entering LIVE, or clears the store in DEMO so real orders cannot fire.
    /// </summary>
    private async Task SyncLiveCredentialsAsync(ClientRecord client, bool live)
    {
        try
        {
            if (!live || !client.IsLiveEnabled)
            {
                _liveCreds.Deactivate();
                _log.LogInformation("[SESSION] LIVE credentials cleared (demo mode) for {id}", client.Id);
                return;
            }

            var (key, secret) = await _db.GetBinanceKeysAsync(client.Id);
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
            {
                _liveCreds.Deactivate();
                _log.LogWarning("[SESSION] LIVE requested but keys empty for {id} — stayed deactivated", client.Id);
                return;
            }

            _liveCreds.ActivateLive(client.Id, key, secret);
            _log.LogInformation("[SESSION] LIVE credentials active for user {id}", client.Id);
        }
        catch (Exception ex)
        {
            _liveCreds.Deactivate();
            _log.LogError(ex, "[SESSION] SyncLiveCredentials failed for {id}", client.Id);
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

            // Cookie is secondary. BindDemoForClient already set _demoMode from
            // ClientRecord.TradingMode + IsLiveEnabled. Only apply cookie if it
            // is a clear override AND keys allow LIVE.
            // Cookie values written by SwitchTradingModeAsync: "0"=live, "1"=demo.
            var demoVal = await js.InvokeAsync<string?>("vertexAuth.getCookie", DemoCookie);
            if (CurrentClient != null && CurrentClient.IsLiveEnabled && !string.IsNullOrEmpty(demoVal))
            {
                bool cookieWantsLive = demoVal is "0" or "live" or "false";
                if (cookieWantsLive)
                {
                    _demoMode = false;
                    _demo.SetDemoMode(false);
                    await SyncLiveCredentialsAsync(CurrentClient, live: true);
                }
                else if (demoVal is "1" or "demo" or "true")
                {
                    _demoMode = true;
                    _demo.SetDemoMode(true);
                    _liveCreds.Deactivate();
                }
            }
            // If no client / no keys — stay in demo (BindDemoForClient already did this)
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
        try { _liveCreds.Deactivate(); } catch { }
        OnChange?.Invoke();
    }

    // ── Legacy sync Logout (for backward compat) ──────────────
    public void Logout()
    {
        CurrentClient = null;
        try { _demo.UnbindClient(); } catch { }
        try { _liveCreds.Deactivate(); } catch { }
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

        // Critical: activate THIS user's keys for LIVE, or clear for DEMO
        if (CurrentClient != null)
            await SyncLiveCredentialsAsync(CurrentClient, live: !_demoMode);

        if (_js != null)
        {
            try { await _js.InvokeVoidAsync("vertexAuth.setCookie", DemoCookie, _demoMode ? "demo" : "live", 365); }
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

