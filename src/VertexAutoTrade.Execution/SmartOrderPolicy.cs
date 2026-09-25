namespace VertexAutoTrade.Execution;

/// <summary>
/// Execution policy hints (caller maps to Binance order types).
/// Prefer PostOnly/Maker; abort if slippage vs mid exceeds threshold in R.
/// </summary>
public sealed class SmartOrderPolicy
{
    public decimal MaxSlippageR { get; init; } = 0.10m;
    public bool PreferPostOnly { get; init; } = true;

    public bool IsSlippageAcceptable(decimal intendedPrice, decimal fillPrice, decimal stopDistance, bool isBuy)
    {
        if (stopDistance <= 0) return false;
        var slip = isBuy ? (fillPrice - intendedPrice) : (intendedPrice - fillPrice);
        if (slip <= 0) return true; // price improvement
        var slipR = slip / stopDistance;
        return slipR <= MaxSlippageR;
    }
}
