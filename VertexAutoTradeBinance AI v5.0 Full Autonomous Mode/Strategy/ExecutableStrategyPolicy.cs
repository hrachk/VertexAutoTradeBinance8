namespace VertexAutoTradeBinance8.Strategy;

/// <summary>
/// Dual-strategy live policy: TREND leg (CORE_/TREND_*) and RANGE spread leg (RANGE_*/MEANREV_*).
/// </summary>
public static class ExecutableStrategyPolicy
{
    public static bool IsLiveExecutable(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return false;
        var r = reason.Trim();
        int pipe = r.IndexOf('|');
        if (pipe > 0) r = r.Substring(0, pipe);

        return r.StartsWith("CORE_", StringComparison.OrdinalIgnoreCase)
            || r.StartsWith("TREND_", StringComparison.OrdinalIgnoreCase)
            || r.StartsWith("RANGE_", StringComparison.OrdinalIgnoreCase)
            || r.StartsWith("MEANREV_", StringComparison.OrdinalIgnoreCase)
            || r.StartsWith("SCALP_", StringComparison.OrdinalIgnoreCase)
            || r.StartsWith("INST_", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTrendLeg(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return false;
        var r = reason;
        int pipe = r.IndexOf('|');
        if (pipe > 0) r = r.Substring(0, pipe);
        return r.StartsWith("CORE_", StringComparison.OrdinalIgnoreCase)
            || r.StartsWith("TREND_", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRangeLeg(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return false;
        var r = reason;
        int pipe = r.IndexOf('|');
        if (pipe > 0) r = r.Substring(0, pipe);
        return r.StartsWith("RANGE_", StringComparison.OrdinalIgnoreCase)
            || r.StartsWith("MEANREV_", StringComparison.OrdinalIgnoreCase)
            || r.StartsWith("SCALP_", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCoreMajorSymbol(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        var s = symbol.Trim().ToUpperInvariant();
        return s.StartsWith("BTC") || s.StartsWith("ETH") || s.StartsWith("BNB")
            || s.StartsWith("SOL") || s.StartsWith("XRP");
    }

    public static string NormalizeLeg(string? reason)
    {
        if (IsTrendLeg(reason)) return "TREND";
        if (IsRangeLeg(reason)) return "RANGE";
        return "OTHER";
    }

    public static string CleanSetupReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "";
        var r = reason.Trim();
        int pipe = r.IndexOf('|');
        if (pipe > 0) r = r.Substring(0, pipe);
        return r;
    }
}
