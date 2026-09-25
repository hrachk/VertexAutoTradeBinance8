using System.Text.Json;
using VertexAutoTrade.Core.Pipeline;

namespace VertexAutoTrade.MLEngine;

/// <summary>
/// Heuristic + optional JSON linear model (ml_skip_model.json) compatible with existing shadow pipeline.
/// P_win &lt; SkipThreshold → REJECT; else if &lt; ProbeThreshold → size×0.25.
/// </summary>
public sealed class MlSetupClassifier
{
    private readonly decimal _skipThreshold;
    private readonly decimal _probeThreshold;
    private readonly bool _hardReject;
    private decimal _bias;
    private readonly Dictionary<string, decimal> _weights = new(StringComparer.OrdinalIgnoreCase);

    public MlSetupClassifier(decimal skipThreshold = 0.42m, decimal probeThreshold = 0.55m, bool hardReject = false)
    {
        _skipThreshold = skipThreshold;
        _probeThreshold = probeThreshold;
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
        if (root.TryGetProperty("threshold", out var t))
        {
            // optional override
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
        // logistic
        var p = 1m / (1m + (decimal)Math.Exp((double)(-z)));
        return Math.Clamp(p, 0.01m, 0.99m);
    }

    public GateDecision Evaluate(TradeFeatureVector f)
    {
        var p = PredictPWin(f);
        if (p < _skipThreshold)
        {
            if (_hardReject)
                return GateDecision.Reject(GateLayer.MlClassifier, "ML_SKIP", $"P(win)={p:F2} < {_skipThreshold}");
            return GateDecision.Ok(GateLayer.MlClassifier, $"ML_SHADOW_SKIP P={p:F2}", sizeMult: 1m); // shadow: do not block
        }
        if (p < _probeThreshold)
            return GateDecision.Ok(GateLayer.MlClassifier, $"ML_PROBE P={p:F2}", sizeMult: 0.25m);
        return GateDecision.Ok(GateLayer.MlClassifier, $"ML_OK P={p:F2}", sizeMult: 1m);
    }

    private decimal W(string name, decimal x) =>
        _weights.TryGetValue(name, out var w) ? w * x : 0m;

    private static decimal HeuristicPWin(TradeFeatureVector f)
    {
        // Prior 0.48; boost confidence; penalize chop + high spread + extreme funding
        decimal p = 0.48m;
        p += (f.Confidence - 0.5m) * 0.35m;
        if (f.RegimeCode == 3) p -= 0.12m;
        if (f.RegimeCode == 1) p += 0.05m;
        p -= Math.Min(0.10m, Math.Abs(f.Funding) * 50m);
        p -= Math.Min(0.08m, f.SpreadBps / 1000m);
        p -= f.NewsImpact * 0.08m;
        return Math.Clamp(p, 0.05m, 0.95m);
    }
}
