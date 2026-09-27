using System.Text.Json;
using VertexAutoTrade.Core.Pipeline;

namespace VertexAutoTrade.MLEngine;

/// <summary>
/// Heuristic + optional JSON linear model (ml_skip_model.json).
/// P_win &lt; SkipThreshold → REJECT when hardReject; else if &lt; ProbeThreshold → size×0.25.
/// Defaults calibrated so typical CORE conf 0.58 is near the skip edge; memory can push below.
/// </summary>
public sealed class MlSetupClassifier
{
    private readonly decimal _skipThreshold;
    private readonly decimal _probeThreshold;
    private readonly bool _hardReject;
    private decimal _bias;
    private readonly Dictionary<string, decimal> _weights = new(StringComparer.OrdinalIgnoreCase);

    public MlSetupClassifier(decimal skipThreshold = 0.58m, decimal probeThreshold = 0.68m, bool hardReject = false)
    {
        _skipThreshold = skipThreshold;
        _probeThreshold = Math.Max(probeThreshold, skipThreshold + 0.05m);
        _hardReject = hardReject;
    }

    public void LoadLinearModel(string jsonPath)
    {
        if (!File.Exists(jsonPath)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root = doc.RootElement;
        if (root.TryGetProperty("bias", out var b))
            _bias = b.GetDecimal();
        if (root.TryGetProperty("weights", out var w) && w.ValueKind == JsonValueKind.Object)
        {
            _weights.Clear();
            foreach (var p in w.EnumerateObject())
                _weights[p.Name] = p.Value.GetDecimal();
        }
    }

    public decimal PredictPWin(TradeFeatureVector f)
    {
        if (_weights.Count == 0)
            return HeuristicPWin(f);

        decimal z = _bias;
        z += W("confidence", f.Confidence);
        z += W("atr_pct", f.AtrPct);
        z += W("funding", f.Funding);
        z += W("spread_bps", f.SpreadBps);
        z += W("hour", f.HourUtc);
        z += W("news", f.NewsImpact);
        z += W("regime", f.RegimeCode);
        z += W("side", f.SideSign);
        z += W("recent_stops", f.RecentStops);
        z += W("recent_wins", f.RecentWins);
        z += W("size_mult", f.SizeMult);
        z += W("soft_skip", f.SoftSkip ? 1m : 0m);
        z += W("avg_r", f.AvgRealizedR);
        var p = 1m / (1m + (decimal)Math.Exp((double)(-z)));
        return Math.Clamp(p, 0.01m, 0.99m);
    }

    public GateDecision Evaluate(TradeFeatureVector f)
    {
        var p = PredictPWin(f);
        if (p < _skipThreshold)
        {
            if (_hardReject)
                return GateDecision.Reject(GateLayer.MlClassifier, "ML_SKIP",
                    $"P(win)={p:F2} < {_skipThreshold}");
            return GateDecision.Ok(GateLayer.MlClassifier, $"ML_SHADOW_SKIP P={p:F2}", sizeMult: 1m);
        }
        if (p < _probeThreshold)
            return GateDecision.Ok(GateLayer.MlClassifier, $"ML_PROBE P={p:F2}", sizeMult: 0.25m);
        return GateDecision.Ok(GateLayer.MlClassifier, $"ML_OK P={p:F2}", sizeMult: 1m);
    }

    private decimal W(string name, decimal x) =>
        _weights.TryGetValue(name, out var w) ? w * x : 0m;

    /// <summary>Same calibration as ShadowMlGatekeeper — must not collapse to ~0.65.</summary>
    public static decimal HeuristicPWin(TradeFeatureVector f)
    {
        decimal conf = f.Confidence;
        if (conf > 1.5m) conf /= 100m;
        conf = Math.Clamp(conf, 0m, 1m);

        decimal p = 0.28m + conf * 0.55m;
        if (conf < 0.55m) p -= 0.05m;
        if (conf >= 0.70m) p += 0.03m;

        p += Math.Clamp(f.AvgRealizedR, -1.5m, 1.5m) * 0.12m;

        int stops = Math.Max(0, f.RecentStops);
        int wins = Math.Max(0, f.RecentWins);
        p -= Math.Min(0.36m, stops * 0.12m);
        if (stops >= 2) p -= 0.10m;
        p += Math.Min(0.12m, wins * 0.04m);

        if (f.SoftSkip) p -= 0.18m;
        if (f.SizeMult < 1m) p -= (1m - f.SizeMult) * 0.20m;
        if (f.SlPadAtr > 0m) p -= Math.Min(0.08m, f.SlPadAtr * 0.06m);

        if (f.RegimeCode == 3) p -= 0.10m;
        if (f.RegimeCode == 1) p += 0.04m;
        p -= Math.Min(0.08m, Math.Abs(f.Funding) * 40m);
        p -= Math.Min(0.06m, f.SpreadBps / 1200m);
        p -= f.NewsImpact * 0.10m;

        return Math.Clamp(p, 0.05m, 0.95m);
    }
}
