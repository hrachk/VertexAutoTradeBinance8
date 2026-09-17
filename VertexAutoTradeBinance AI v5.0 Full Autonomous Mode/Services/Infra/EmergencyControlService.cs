using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services.Infra;

/// <summary>
/// File-backed kill / pause for AutoTrade. Telegram /kill writes the flag;
/// TradingWorker refuses new entries while active.
/// </summary>
public sealed class EmergencyControlService
{
    private readonly string _flagPath;
    private readonly ILogger<EmergencyControlService> _log;
    private volatile bool _kill;

    public EmergencyControlService(IConfiguration cfg, ILogger<EmergencyControlService> log)
    {
        var root = cfg["SharedData:Root"] ?? @"C:\Vertex\Engines\client_001";
        Directory.CreateDirectory(root);
        _flagPath = Path.Combine(root, "emergency_kill.flag");
        _log = log;
        _kill = File.Exists(_flagPath);
    }

    public bool IsKillActive
    {
        get
        {
            if (_kill) return true;
            if (File.Exists(_flagPath)) { _kill = true; return true; }
            return false;
        }
    }

    public void ActivateKill(string reason)
    {
        _kill = true;
        try
        {
            File.WriteAllText(_flagPath, $"{DateTime.UtcNow:o}\n{reason}\n");
        }
        catch (Exception ex) { _log.LogWarning(ex, "[KILL] write flag failed"); }
        _log.LogError("[KILL] ACTIVATED — {reason}", reason);
    }

    public void ClearKill()
    {
        _kill = false;
        try { if (File.Exists(_flagPath)) File.Delete(_flagPath); } catch { }
        _log.LogWarning("[KILL] cleared");
    }
}
