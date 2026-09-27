using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VertexAutoTrade.Execution;
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

public sealed class SignalQualityEvaluator
{
    private readonly IOptionsMonitor<SignalQualityOptions> _opt;
    private readonly ILogger<SignalQualityEvaluator> _log;
    private readonly OiFundingTracker _oi;
    private readonly IHttpClientFactory _httpFactory;

    public SignalQualityEvaluator(
        IOptionsMonitor<SignalQualityOptions> opt,
        ILogger<SignalQualityEvaluator> log,
        OiFundingTracker oi,
        IHttpClientFactory httpFactory)
    {
        _opt = opt;
        _log = log;
        _oi = oi;
        _httpFactory = httpFactory;
    }

    public async Task<SignalQualityBreakdown> EvaluateAsync(TradeSignal signal, CancellationToken ct = default)
    {
        var o = _opt.CurrentValue;
        decimal wT = Norm(o.WeightTrend);
        decimal wD = Norm(o.WeightDerivatives);
        decimal wV = Norm(o.WeightVolume);
        decimal wR = Norm(o.WeightRiskReward);
        decimal sum = wT + wD + wV + wR;
        if (sum <= 0) { wT = 0.35m; wD = 0.20m; wV = 0.20m; wR = 0.25m; sum = 1m; }
        wT /= sum; wD /= sum; wV /= sum; wR /= sum;

        // Live OI + Funding refresh (public REST, fail-open)
        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(3);
            await _oi.RefreshAsync(http, signal.Symbol, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[SQ] OI/Funding refresh failed {sym}", signal.Symbol);
        }

        decimal trend = ScoreTrend(signal);
        decimal deriv = ScoreDerivativesLive(signal);
        decimal vol = ScoreVolume(signal);
        decimal rr = ScoreRiskReward(signal);

        decimal composite = 100m * (wT * trend + wD * deriv + wV * vol + wR * rr);
        composite = Math.Clamp(composite, 0m, 100m);

        return new SignalQualityBreakdown(
            composite, trend * 100m, deriv * 100m, vol * 100m, rr * 100m,
            $"Trend:{trend * 100m:F0} OI/Fund:{deriv * 100m:F0} Vol:{vol * 100m:F0} RR:{rr * 100m:F0}");
    }

    /// <summary>Sync fallback when async not available.</summary>
    public SignalQualityBreakdown Evaluate(TradeSignal signal)
        => EvaluateAsync(signal).GetAwaiter().GetResult();

    public decimal SizeFactorFromScore(decimal composite)
    {
        var o = _opt.CurrentValue;
        decimal min = o.MinQualityThreshold;
        decimal full = o.FullSizeScore <= min ? min + 20m : o.FullSizeScore;
        if (composite >= full) return 1.0m;
        if (composite < min) return 0m;
        decimal t = (composite - min) / Math.Max(1m, full - min);
        return 0.70m + 0.30m * t;
    }

    private static decimal Norm(decimal w) => w < 0 ? 0 : w;

    private static decimal ScoreTrend(TradeSignal s)
    {
        decimal c = s.Confidence ?? (s.PatternConfidence > 0 ? s.PatternConfidence / 100m : 0.55m);
        c = Math.Clamp(c, 0m, 1m);
        if (s.IsSuperSignal) c = Math.Min(1m, c + 0.08m);
        var r = (s.Reason ?? "").ToUpperInvariant();
        if (r.Contains("CORE_PULLBACK") || r.Contains("STRUCT")) c = Math.Min(1m, c + 0.05m);
        if (r.Contains("FOMO") || r.Contains("CHASE")) c = Math.Max(0m, c - 0.15m);
        return c;
    }

    private decimal ScoreDerivativesLive(TradeSignal s)
    {
        bool isLong = s.Side == SignalSide.Buy;
        // Optional price move from signal metadata not always present → null
        return _oi.ScoreDerivatives(s.Symbol, isLong, null);
    }

    private static decimal ScoreVolume(TradeSignal s)
    {
        if (s.LiquidityScore is decimal ls && ls > 0)
            return Math.Clamp(ls, 0.15m, 1m);
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
        if (rr >= 2.5m) return 1.0m;
        if (rr >= 2.0m) return 0.92m;
        if (rr >= 1.5m) return 0.78m;
        if (rr >= 1.2m) return 0.62m;
        if (rr >= 1.0m) return 0.50m;
        return Math.Clamp(rr / 2.0m, 0.15m, 0.45m);
    }
}
