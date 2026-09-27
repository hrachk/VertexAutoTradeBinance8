using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services.SignalQuality;

public sealed record SignalQualityBreakdown(
    decimal Composite,
    decimal Trend,
    decimal Derivatives,
    decimal Volume,
    decimal RiskReward,
    string Summary);

/// <summary>
/// Multi-factor setup quality: Trend / Derivatives proxy / Volume-liquidity / R:R.
/// Institutional filter before risk slots are spent on weak EV setups.
/// </summary>
public sealed class SignalQualityEvaluator
{
    private readonly IOptionsMonitor<SignalQualityOptions> _opt;
    private readonly ILogger<SignalQualityEvaluator> _log;

    public SignalQualityEvaluator(
        IOptionsMonitor<SignalQualityOptions> opt,
        ILogger<SignalQualityEvaluator> log)
    {
        _opt = opt;
        _log = log;
    }

    public SignalQualityBreakdown Evaluate(TradeSignal signal)
    {
        var o = _opt.CurrentValue;
        decimal wT = Norm(o.WeightTrend);
        decimal wD = Norm(o.WeightDerivatives);
        decimal wV = Norm(o.WeightVolume);
        decimal wR = Norm(o.WeightRiskReward);
        decimal sum = wT + wD + wV + wR;
        if (sum <= 0) { wT = 0.35m; wD = 0.20m; wV = 0.20m; wR = 0.25m; sum = 1m; }
        wT /= sum; wD /= sum; wV /= sum; wR /= sum;

        decimal trend = ScoreTrend(signal);
        decimal deriv = ScoreDerivativesProxy(signal);
        decimal vol = ScoreVolume(signal);
        decimal rr = ScoreRiskReward(signal);

        decimal composite = 100m * (wT * trend + wD * deriv + wV * vol + wR * rr);
        composite = Math.Clamp(composite, 0m, 100m);

        return new SignalQualityBreakdown(
            composite, trend * 100m, deriv * 100m, vol * 100m, rr * 100m,
            $"Trend:{trend * 100m:F0} OI/Der:{deriv * 100m:F0} Vol:{vol * 100m:F0} RR:{rr * 100m:F0}");
    }

    /// <summary>Maps quality score → SizeMultiplier factor (1.0 at FullSizeScore+).</summary>
    public decimal SizeFactorFromScore(decimal composite)
    {
        var o = _opt.CurrentValue;
        decimal min = o.MinQualityThreshold;
        decimal full = o.FullSizeScore <= min ? min + 20m : o.FullSizeScore;
        if (composite >= full) return 1.0m;
        if (composite < min) return 0m;
        // linear 0.70 .. 1.0 between min and full
        decimal t = (composite - min) / Math.Max(1m, full - min);
        return 0.70m + 0.30m * t;
    }

    private static decimal Norm(decimal w) => w < 0 ? 0 : w;

    private static decimal ScoreTrend(TradeSignal s)
    {
        // Confidence is primary trend/setup strength from CORE/AI (0..1)
        decimal c = s.Confidence ?? (s.PatternConfidence > 0 ? s.PatternConfidence / 100m : 0.55m);
        c = Math.Clamp(c, 0m, 1m);
        // Super-signal boost
        if (s.IsSuperSignal) c = Math.Min(1m, c + 0.08m);
        // Reason tags
        var r = (s.Reason ?? "").ToUpperInvariant();
        if (r.Contains("CORE_PULLBACK") || r.Contains("STRUCT")) c = Math.Min(1m, c + 0.05m);
        if (r.Contains("FOMO") || r.Contains("CHASE")) c = Math.Max(0m, c - 0.15m);
        return c;
    }

    private static decimal ScoreDerivativesProxy(TradeSignal s)
    {
        // Without live OI feed in path: use liquidity/score proxies + neutral baseline.
        // When LiquidityScore set by SmartFlow, interpret mid-high as healthy participation.
        decimal baseScore = 0.55m;
        if (s.LiquidityScore is decimal ls && ls > 0)
        {
            // ls often 0..1
            baseScore = Math.Clamp(0.35m + ls * 0.55m, 0.20m, 0.95m);
        }
        return baseScore;
    }

    private static decimal ScoreVolume(TradeSignal s)
    {
        if (s.LiquidityScore is decimal ls && ls > 0)
            return Math.Clamp(ls, 0.15m, 1m);
        // unknown → mild neutral (don't over-penalize)
        return 0.60m;
    }

    private static decimal ScoreRiskReward(TradeSignal s)
    {
        decimal entry = s.EntryPrice;
        decimal sl = s.StopLoss;
        if (entry <= 0 || sl <= 0) return 0.40m;
        decimal risk = Math.Abs(entry - sl);
        if (risk <= 0) return 0.30m;
        decimal tp1 = s.TakeProfits is { Count: > 0 } ? s.TakeProfits[0] : 0;
        if (tp1 <= 0) return 0.45m;
        decimal rr = Math.Abs(tp1 - entry) / risk;
        // 1.0R → 0.45, 1.5R → 0.70, 2.0R+ → 0.95
        if (rr >= 2.5m) return 1.0m;
        if (rr >= 2.0m) return 0.92m;
        if (rr >= 1.5m) return 0.78m;
        if (rr >= 1.2m) return 0.62m;
        if (rr >= 1.0m) return 0.50m;
        return Math.Clamp(rr / 2.0m, 0.15m, 0.45m);
    }
}
