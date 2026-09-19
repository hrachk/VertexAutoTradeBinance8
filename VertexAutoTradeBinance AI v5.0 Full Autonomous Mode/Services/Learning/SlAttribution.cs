namespace VertexAutoTradeBinance8.Services.Learning;

/// <summary>Canonical close-reason tags for contextual SL memory.</summary>
public static class CloseReasonCodes
{
    public const string Tp = "TP";
    public const string TpFinal = "TP3/final";
    public const string Manual = "Manual";
    public const string SlBeHit = "SL_BE_HIT";
    public const string SlMarketCorr = "SL_MARKET_CORRELATION";
    public const string SlNews = "SL_NEWS_SPIKE";
    public const string SlWhipsaw = "SL_WHIPSAW";
    public const string DlStrategyFail = "SL_STRATEGY_FAIL";
    public const string SlGeneric = "SL";
}

public static class SlAttribution
{
    /// <summary>Weight in consecutiveStops / stopRate (strategy fail = full).</summary>
    public static decimal Weight(string? reason)
    {
        var r = (reason ?? "").ToUpperInvariant();
        if (r.Contains("STRATEGY_FAIL") || r == "SL" || r.StartsWith("SL_"))
        {
            if (r.Contains("MARKET_CORRELATION")) return 0.40m;
            if (r.Contains("NEWS_SPIKE")) return 0.35m;
            if (r.Contains("WHIPSAW")) return 0.50m;
            if (r.Contains("BE_HIT")) return 0.70m;
            if (r.Contains("STRATEGY_FAIL")) return 1.0m;
            if (r.Contains("SL") && !r.Contains("TP")) return 1.0m; // plain SL
        }
        if (r.Contains("SL") && !r.Contains("TP")) return 1.0m;
        return 0m;
    }

    public static bool IsStop(string? reason, decimal pnl)
    {
        var r = reason ?? "";
        if (r.IndexOf("TP", StringComparison.OrdinalIgnoreCase) >= 0 &&
            r.IndexOf("SL", StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        if (r.IndexOf("SL", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return pnl < 0;
    }

    /// <summary>
    /// Enrich generic "SL" with market context at close time.
    /// btc/eth delta = % change over ~exit bar / short window (caller supplies).
    /// </summary>
    public static string Classify(
        string? rawReason,
        decimal realizedPnl,
        decimal btcDeltaPct,
        decimal ethDeltaPct,
        bool newsSpikeActive,
        decimal? initialRiskPrice = null,
        decimal entry = 0,
        decimal exit = 0)
    {
        var raw = rawReason ?? "";
        if (raw.IndexOf("TP", StringComparison.OrdinalIgnoreCase) >= 0 &&
            raw.IndexOf("SL", StringComparison.OrdinalIgnoreCase) < 0)
            return raw;

        bool looksSl = raw.IndexOf("SL", StringComparison.OrdinalIgnoreCase) >= 0 || realizedPnl < 0;
        if (!looksSl) return string.IsNullOrWhiteSpace(raw) ? "UNKNOWN" : raw;

        // Already tagged
        if (raw.Contains("SL_MARKET_CORRELATION", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("SL_NEWS_SPIKE", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("SL_WHIPSAW", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("SL_STRATEGY_FAIL", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("SL_BE_HIT", StringComparison.OrdinalIgnoreCase))
            return raw;

        if (newsSpikeActive)
            return CloseReasonCodes.SlNews;

        // Correlated dump: BTC or ETH moved hard against typical alt beta
        if (Math.Abs(btcDeltaPct) >= 1.2m || Math.Abs(ethDeltaPct) >= 1.5m)
            return CloseReasonCodes.SlMarketCorr;

        // Whipsaw: exit near entry relative to planned risk (noise stop)
        if (initialRiskPrice is > 0 && entry > 0)
        {
            var move = Math.Abs(exit - entry);
            if (move > 0 && move < initialRiskPrice * 0.35m)
                return CloseReasonCodes.SlWhipsaw;
        }

        if (raw.IndexOf("BE", StringComparison.OrdinalIgnoreCase) >= 0)
            return CloseReasonCodes.SlBeHit;

        return CloseReasonCodes.DlStrategyFail;
    }
}
