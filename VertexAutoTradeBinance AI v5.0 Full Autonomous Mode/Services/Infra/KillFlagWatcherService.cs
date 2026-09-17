using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services.Infra;

/// <summary>
/// When TWA/Web writes emergency_kill.flag, run FlattenAll once (Live+Demo) so Mini App KILL is full-power.
/// </summary>
public sealed class KillFlagWatcherService : BackgroundService
{
    private readonly IConfiguration _cfg;
    private readonly FlattenAllService _flatten;
    private readonly EmergencyControlService _kill;
    private readonly ILogger<KillFlagWatcherService> _log;
    private string? _lastFingerprint;

    public KillFlagWatcherService(
        IConfiguration cfg,
        FlattenAllService flatten,
        EmergencyControlService kill,
        ILogger<KillFlagWatcherService> log)
    {
        _cfg = cfg;
        _flatten = flatten;
        _kill = kill;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = _cfg["SharedData:Root"] ?? "";
        var path = Path.Combine(root, "emergency_kill.flag");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path);
                    var fp = text.GetHashCode().ToString("X") + ":" + new FileInfo(path).Length;
                    if (fp != _lastFingerprint)
                    {
                        _lastFingerprint = fp;
                        _kill.ActivateKill("flag-watcher");
                        // Only auto-flatten when originated from TWA (avoid double flatten on bot /kill)
                        if (text.Contains("twa", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("FLATTEN", StringComparison.OrdinalIgnoreCase))
                        {
                            _log.LogWarning("[KILL-WATCH] TWA/flag kill detected → FlattenAll");
                            await _flatten.ExecuteAsync("kill-flag-watcher", stoppingToken);
                        }
                    }
                }
                else _lastFingerprint = null;
            }
            catch (Exception ex) { _log.LogDebug(ex, "[KILL-WATCH]"); }

            try { await Task.Delay(2000, stoppingToken); } catch (TaskCanceledException) { break; }
        }
    }
}
