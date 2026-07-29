using VertexAutoTradeBinance8.Web.Services.Auth;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// Resolves file paths scoped to the currently logged-in client.
/// Each client gets their own folder: C:\Vertex\Engines\client_XXX\
/// Falls back to the global SharedData:Root when no user is logged in
/// (e.g. engine-side reads, system services).
/// 
/// Inject as Scoped so it picks up the per-circuit AuthSessionService.
/// </summary>
public sealed class ClientDataService
{
    private readonly AuthSessionService _auth;
    private readonly string _globalRoot;

    // Known file names (constants to avoid typos across the codebase)
    public const string LiveSignalsFile    = "live_signals.json";
    public const string MissedTradesFile   = "missed_trades.json";
    public const string ExecutedSignals    = "executed_signals.json";
    public const string KlinesBootstrap    = "klines_bootstrap.json";
    public const string EngineState        = "engine_state.json";
    public const string DecisionTraceDir   = "ai-models/decision-trace";

    public ClientDataService(AuthSessionService auth, IConfiguration cfg)
    {
        _auth       = auth;
        _globalRoot = cfg["SharedData:Root"] ?? @"C:\Vertex\Engines\client_001";
    }

    /// <summary>
    /// Root folder for the current client.
    /// Falls back to global root if not authenticated.
    /// </summary>
    public string Root =>
        _auth.IsAuthenticated
            ? _auth.CurrentClient!.DataFolder
            : _globalRoot;

    /// <summary>Resolve a file name to the client-scoped absolute path.</summary>
    public string Resolve(string fileName) => Path.Combine(Root, fileName);

    /// <summary>Resolve a sub-directory path inside the client root.</summary>
    public string ResolveDir(string subDir)
    {
        var dir = Path.Combine(Root, subDir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── Convenience properties ────────────────────────────────
    public string LiveSignalsPath    => Resolve(LiveSignalsFile);
    public string MissedTradesPath   => Resolve(MissedTradesFile);
    public string ExecutedSignalPath => Resolve(ExecutedSignals);
    public string KlinesBootstrapPath=> Resolve(KlinesBootstrap);
    public string DecisionTracePath  => ResolveDir(DecisionTraceDir);

    /// <summary>
    /// Returns true if the client folder exists and is accessible.
    /// </summary>
    public bool ClientFolderReady =>
        Directory.Exists(Root);

    /// <summary>
    /// Ensures the client data folder exists (creates if missing).
    /// </summary>
    public void EnsureFolder()
    {
        try { Directory.CreateDirectory(Root); }
        catch { /* ignore — may be read-only in some envs */ }
    }

    /// <summary>
    /// Client ID of current session (or "system" if not authenticated).
    /// Useful for logging.
    /// </summary>
    public string ClientId =>
        _auth.CurrentClient?.Id ?? "system";
}
