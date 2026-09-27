using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services.Learning;

namespace VertexAutoTradeBinance8.Services.Ml;

public sealed class ShadowMlPrediction
{
    public double PWin { get; set; }
    public double ExpectedR { get; set; }
    public string Source { get; set; } = "heuristic";
    public bool WouldSkip { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>
/// Offline/shadow gatekeeper: logs P(Win)/E(R) without blocking unless EnableMlSkipGate=true.
/// Model file optional: SharedData/ml_skip_model.json { "bias":0, "weights":{...}, "threshold":0.42 }
/// </summary>
public sealed class ShadowMlGatekeeper
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<ShadowMlGatekeeper> _log;
    private readonly TradeJournalService? _journal;
    private readonly ShadowMlKpiStore? _kpi;
    private readonly object _gate = new();
    private Dictionary<string, double>? _weights;
    private double _bias;
    private double _threshold = 0.58;
    private DateTime _modelLoadedUtc = DateTime.MinValue;

    public ShadowMlGatekeeper(IConfiguration cfg, ILogger<ShadowMlGatekeeper> log, TradeJournalService? journal = null, ShadowMlKpiStore? kpi = null)
    {
        _cfg = cfg;
        _log = log;
        _journal = journal;
        _kpi = kpi;
    }

    public bool EnableMlSkipGate =>
        _cfg.GetValue("Institutional:EnableMlHardReject", false)
        || _cfg.GetValue("MlGate:EnableMlSkipGate", false);
    public double HighConfProbe => _cfg.GetValue("TradeMemory:ProbeMinConfidence", 0.72);

    public ShadowMlPrediction Evaluate(TradeSignal signal, SymbolAdjustments? mem)
    {
        EnsureModel();
        var features = BuildFeatures(signal, mem);
        double score;
        string src;
        if (_weights != null && _weights.Count > 0)
        {
            score = _bias;
            foreach (var kv in features)
                if (_weights.TryGetValue(kv.Key, out var w))
                    score += w * kv.Value;
            // logistic
            score = 1.0 / (1.0 + Math.Exp(-Math.Clamp(score, -20, 20)));
            src = "json-model";
        }
        else
        {
            // Calibrated heuristic (institutional): conf + SymbolMemory history.
            // Designed so P(win) spreads ~0.35–0.75, NOT stuck at ~0.65.
            score = HeuristicPWin(signal, mem);
            src = "heuristic";
        }

        // Prefer Institutional threshold; fall back MlGate then model default
        double thr = _cfg.GetValue<double?>("Institutional:MlSkipThreshold")
            ?? _cfg.GetValue<double?>("MlGate:SkipThreshold")
            ?? Math.Max(_threshold, 0.58);
        var pred = new ShadowMlPrediction
        {
            PWin = score,
            ExpectedR = (score - 0.5) * 1.2,
            Source = src,
            WouldSkip = score < thr,
            Note = $"thr={thr:F2} conf={signal.Confidence}"
        };

        _log.LogInformation(
            "[ML-SHADOW] {sym} P(Win)={p:F3} E(R)={er:F2} wouldSkip={skip} src={src} gate={gate}",
            signal.Symbol, pred.PWin, pred.ExpectedR, pred.WouldSkip, pred.Source, EnableMlSkipGate);
        try { _kpi?.Record(pred); } catch { }
        try
        {
            var snap = _kpi?.GetSnapshot();
            if (snap != null && snap.TotalEvaluated > 0 && snap.TotalEvaluated % 10 == 0)
                _log.LogInformation("[ML-KPI] {label} evaluated={n} wouldSkip={s}",
                    snap.ModeLabel, snap.TotalEvaluated, snap.ShadowWouldSkip);
        }
        catch { }

        try
        {
            var root = _cfg["SharedData:Root"] ?? "";
            if (!string.IsNullOrEmpty(root))
            {
                var path = Path.Combine(root, "ml_shadow.jsonl");
                var line = JsonSerializer.Serialize(new
                {
                    utc = DateTime.UtcNow,
                    signal.Symbol,
                    pred.PWin,
                    pred.ExpectedR,
                    pred.WouldSkip,
                    pred.Source,
                    features
                });
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch { }

        return pred;
    }

    /// <summary>
    /// Production heuristic: prior 0.28 + confidence slope + memory penalties.
    /// recentStops / SoftSkip must move P(win) below typical SkipThreshold (0.58–0.62).
    /// </summary>
    private static double HeuristicPWin(TradeSignal signal, SymbolAdjustments? mem)
    {
        double conf = (double)(signal.Confidence ?? 0m);
        if (conf > 1.5) conf /= 100.0;
        conf = Math.Clamp(conf, 0.0, 1.0);

        // Base: conf 0.58 → ~0.599; conf 0.62 → ~0.621 (before memory)
        double p = 0.28 + conf * 0.55;
        if (conf < 0.55) p -= 0.05;          // soft signals pay a tax
        if (conf >= 0.70) p += 0.03;

        double avgR = mem != null && mem.Note != null && mem.Note.Contains("avgR=", StringComparison.Ordinal)
            ? TryParseAvgR(mem.Note) : 0.0;
        p += Math.Clamp(avgR, -1.5, 1.5) * 0.12;

        if (mem != null)
        {
            int stops = Math.Max(0, mem.RecentStops);
            int wins = Math.Max(0, mem.RecentWins);
            // Each recent stop hurts; 2+ consecutive-style weight
            p -= Math.Min(0.36, stops * 0.12);
            if (stops >= 2) p -= 0.10;
            p += Math.Min(0.12, wins * 0.04);

            if (mem.SoftSkip) p -= 0.18;
            if (mem.SizeMult < 1m) p -= (1.0 - (double)mem.SizeMult) * 0.20;
            if (mem.SlPadAtr > 0m) p -= Math.Min(0.08, (double)mem.SlPadAtr * 0.06);
        }

        // CORE tag slight prior (structure setups)
        if ((signal.Reason ?? "").StartsWith("CORE_", StringComparison.OrdinalIgnoreCase))
            p += 0.02;

        return Math.Clamp(p, 0.05, 0.95);
    }

    private static double TryParseAvgR(string note)
    {
        try
        {
            var i = note.IndexOf("avgR=", StringComparison.Ordinal);
            if (i < 0) return 0;
            var s = note[(i + 5)..];
            var end = s.IndexOfAny(new[] { ' ', '|' });
            if (end > 0) s = s[..end];
            return double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
        catch { return 0; }
    }

    private Dictionary<string, double> BuildFeatures(TradeSignal signal, SymbolAdjustments? mem)
    {
        double conf = (double)(signal.Confidence ?? 0m);
        if (conf > 1.5) conf /= 100.0;
        return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["conf"] = conf,
            ["sizeMult"] = mem != null ? (double)mem.SizeMult : 1.0,
            ["softSkip"] = mem?.SoftSkip == true ? 1.0 : 0.0,
            ["recentStops"] = mem != null ? mem.RecentStops : 0,
            ["recentWins"] = mem != null ? mem.RecentWins : 0,
            ["slPad"] = mem != null ? (double)mem.SlPadAtr : 0,
            ["isCore"] = (signal.Reason ?? "").StartsWith("CORE_", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0
        };
    }

    private void EnsureModel()
    {
        if ((DateTime.UtcNow - _modelLoadedUtc).TotalMinutes < 5 && _weights != null) return;
        lock (_gate)
        {
            if ((DateTime.UtcNow - _modelLoadedUtc).TotalMinutes < 5 && _weights != null) return;
            _modelLoadedUtc = DateTime.UtcNow;
            try
            {
                var root = _cfg["SharedData:Root"] ?? "";
                var path = Path.Combine(root, "ml_skip_model.json");
                if (!File.Exists(path)) { _weights = null; return; }
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var rootEl = doc.RootElement;
                _bias = rootEl.TryGetProperty("bias", out var b) ? b.GetDouble() : 0;
                _threshold = rootEl.TryGetProperty("threshold", out var t) ? t.GetDouble() : 0.42;
                _weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                if (rootEl.TryGetProperty("weights", out var w))
                {
                    foreach (var p in w.EnumerateObject())
                        _weights[p.Name] = p.Value.GetDouble();
                }
                _log.LogInformation("[ML-SHADOW] loaded model {path} weights={n}", path, _weights.Count);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[ML-SHADOW] model load failed — heuristic");
                _weights = null;
            }
        }
    }
}
