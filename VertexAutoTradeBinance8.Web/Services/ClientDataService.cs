using VertexAutoTradeBinance8.Web.Services.Auth;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// Resolves file paths scoped to the currently logged-in client.
/// Layout:
///   {EnginesRoot}/clients.json              — shared user registry
///   {EnginesRoot}/client_{id}/              — per-user data root
///     live_signals.json, executed_signals.json, engine_state.json, ...
///     demo-account.json, demo-dca-state.json
/// </summary>
public sealed class ClientDataService
{
    private readonly AuthSessionService _auth;
    private readonly string _enginesRoot; // parent of all client_* folders

    public const string LiveSignalsFile  = "live_signals.json";
    public const string MissedTradesFile = "missed_trades.json";
    public const string ExecutedSignals  = "executed_signals.json";
    public const string KlinesBootstrap  = "klines_bootstrap.json";
    public const string EngineState      = "engine_state.json";
    public const string DecisionTraceDir = "ai-models/decision-trace";
    public const string DemoAccountFile  = "demo-account.json";
    public const string DemoDcaFile      = "demo-dca-state.json";

    public ClientDataService(AuthSessionService auth, IConfiguration cfg)
    {
        _auth = auth;

        // SharedData:Root historically pointed at client_001.
        // Engines root = parent of that folder (or explicit SharedData:EnginesRoot).
        var root = cfg["SharedData:EnginesRoot"];
        if (string.IsNullOrWhiteSpace(root))
        {
            var legacy = cfg["SharedData:Root"] ?? Path.Combine(AppContext.BaseDirectory, "vertex-data", "client_001");
            root = Path.GetDirectoryName(legacy.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                   ?? Path.Combine(AppContext.BaseDirectory, "vertex-data");
        }
        _enginesRoot = root;
        try { Directory.CreateDirectory(_enginesRoot); } catch { }
    }

    /// <summary>Parent folder that contains clients.json and all client_* dirs.</summary>
    public string EnginesRoot => _enginesRoot;

    public string ClientId =>
        _auth.CurrentClient?.Id ?? "anonymous";

    /// <summary>Root folder for the current client.</summary>
    public string Root
    {
        get
        {
            var id = ClientId;
            if (string.IsNullOrEmpty(id) || id == "anonymous")
                return Path.Combine(_enginesRoot, "anonymous");
            return Path.Combine(_enginesRoot, $"client_{id}");
        }
    }

    public string Resolve(string fileName) => Path.Combine(Root, fileName);
    public string ResolveDir(string subDir)
    {
        var p = Path.Combine(Root, subDir);
        try { Directory.CreateDirectory(p); } catch { }
        return p;
    }

    public string LiveSignalsPath     => Resolve(LiveSignalsFile);
    public string MissedTradesPath    => Resolve(MissedTradesFile);
    public string ExecutedSignalPath  => Resolve(ExecutedSignals);
    public string KlinesBootstrapPath => Resolve(KlinesBootstrap);
    public string DecisionTracePath   => ResolveDir(DecisionTraceDir);
    public string DemoAccountPath     => Resolve(DemoAccountFile);
    public string DemoDcaPath         => Resolve(DemoDcaFile);

    public bool ClientFolderReady =>
        !string.IsNullOrEmpty(ClientId) && ClientId != "anonymous" && Directory.Exists(Root);

    /// <summary>Creates client_{id} folder structure for a user (call on register / first login).</summary>
    public void EnsureFolderFor(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return;
        var root = Path.Combine(_enginesRoot, $"client_{clientId}");
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "ai-models", "decision-trace"));
        }
        catch { /* non-fatal */ }
    }

    public void EnsureFolder() => EnsureFolderFor(ClientId);
}
