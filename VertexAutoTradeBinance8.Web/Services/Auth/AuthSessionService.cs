namespace VertexAutoTradeBinance8.Web.Services.Auth;

/// <summary>
/// Scoped per Blazor circuit. Holds the currently logged-in client.
/// Injected into pages as @inject AuthSessionService Auth.
/// </summary>
public sealed class AuthSessionService
{
    public ClientRecord? CurrentClient { get; private set; }
    public bool IsAuthenticated => CurrentClient != null;
    public bool IsDemo => CurrentClient?.Plan == "demo";
    public bool IsLive => CurrentClient is { Plan: "live" or "pro", IsLiveEnabled: true };

    public event Action? OnChange;

    private readonly ClientDbService _db;
    private readonly ILogger<AuthSessionService> _log;

    public AuthSessionService(ClientDbService db, ILogger<AuthSessionService> log)
    {
        _db  = db;
        _log = log;
    }

    public async Task<(bool ok, string error)> LoginAsync(string email, string password)
    {
        var (ok, error, client) = await _db.LoginAsync(email, password);
        if (ok && client != null)
        {
            CurrentClient = client;
            _log.LogInformation("[SESSION] Login: {Id}", client.Id);
            OnChange?.Invoke();
        }
        return (ok, error);
    }

    public async Task<(bool ok, string error)>
        RegisterAsync(string email, string password, string displayName)
    {
        var (ok, error, client) = await _db.RegisterAsync(email, password, displayName);
        if (ok && client != null)
        {
            CurrentClient = client;
            OnChange?.Invoke();
        }
        return (ok, error);
    }

    public void Logout()
    {
        _log.LogInformation("[SESSION] Logout: {Id}", CurrentClient?.Id);
        CurrentClient = null;
        OnChange?.Invoke();
    }

    public async Task RefreshAsync()
    {
        if (CurrentClient == null) return;
        var fresh = await _db.FindByIdAsync(CurrentClient.Id);
        if (fresh != null) { CurrentClient = fresh; OnChange?.Invoke(); }
    }
}
