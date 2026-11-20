namespace VertexAutoTrade.Core.Risk;

/// <summary>
/// Which price arms protective stops. MARK_PRICE (default) is Binance's index-based fair price:
/// a single-venue wick on the last traded price does not trigger it. On 2026-11-20 four Demo
/// shorts (AMDUSDT, ANTUSDT, SOXLUSDT, XAGUSDT) were stopped by one spike on the last price and
/// three of them then ran 1.3–5R in the signal's direction. Take-profits keep CONTRACT_PRICE
/// (a real trade at the level is what fills them).
/// Set from configuration "Trading:StopWorkingType" (MARK_PRICE | CONTRACT_PRICE).
/// </summary>
public static class StopTriggerPolicy
{
    public static string StopWorkingType { get; private set; } = "MARK_PRICE";

    public static bool UseMarkForStops =>
        string.Equals(StopWorkingType, "MARK_PRICE", StringComparison.OrdinalIgnoreCase);

    public static void Configure(string? value)
    {
        if (string.Equals(value, "CONTRACT_PRICE", StringComparison.OrdinalIgnoreCase)) StopWorkingType = "CONTRACT_PRICE";
        else StopWorkingType = "MARK_PRICE";
    }

    /// <summary>workingType to send for an algo/conditional order of <paramref name="orderType"/>.</summary>
    public static string Resolve(string orderType, string requested)
        => string.Equals(orderType, "STOP_MARKET", StringComparison.OrdinalIgnoreCase)
           || string.Equals(orderType, "STOP", StringComparison.OrdinalIgnoreCase)
            ? StopWorkingType
            : requested;
}
