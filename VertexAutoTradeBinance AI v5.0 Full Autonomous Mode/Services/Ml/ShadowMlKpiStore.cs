using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services.Ml;

public sealed class ShadowMlKpiSnapshot
{
    public int TotalEvaluated { get; set; }
    public int ShadowWouldSkip { get; set; }
    public double SkipRatePct => TotalEvaluated > 0 ? 100.0 * ShadowWouldSkip / TotalEvaluated : 0;
    public bool HardGateEnabled { get; set; }
    public double Threshold { get; set; }
    public string ModeLabel => HardGateEnabled
        ? $"[ML Gate: HARD REJECT ENABLED (thr={Threshold:P0})]"
        : $"[ML Gate: SHADOW MODE (Skip≈{SkipRatePct:F0}%)]";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ShadowMlKpiStore
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<ShadowMlKpiStore> _log;
    private int _total;
    private int _wouldSkip;
    private readonly object _lock = new();

    public ShadowMlKpiStore(IConfiguration cfg, ILogger<ShadowMlKpiStore> log)
    {
        _cfg = cfg;
        _log = log;
    }

    public void Record(ShadowMlPrediction pred)
    {
        lock (_lock)
        {
            _total++;
            if (pred.WouldSkip) _wouldSkip++;
        }
        try
        {
            var root = _cfg["SharedData:Root"] ?? "";
            if (string.IsNullOrEmpty(root)) return;
            var snap = GetSnapshot();
            File.WriteAllText(Path.Combine(root, "ml_shadow_kpi.json"),
                JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public ShadowMlKpiSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new ShadowMlKpiSnapshot
            {
                TotalEvaluated = _total,
                ShadowWouldSkip = _wouldSkip,
                HardGateEnabled = _cfg.GetValue("MlGate:EnableMlSkipGate", false),
                Threshold = _cfg.GetValue("MlGate:SkipThreshold", 0.42),
                UpdatedUtc = DateTime.UtcNow
            };
        }
    }
}
