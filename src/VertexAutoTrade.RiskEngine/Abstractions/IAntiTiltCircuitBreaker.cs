namespace VertexAutoTrade.RiskEngine.Abstractions;

public interface IAntiTiltCircuitBreaker
{
    /// <summary>Update consecutive fails + rolling avg R. Returns current risk multiplier (1.0 or reduced).</summary>
    decimal OnTradeClosed(ClosedTradeRiskEvent evt);

    EntryPermission EvaluateEntry(DateTime utcNow, decimal baseRiskFraction);

    decimal CurrentRiskMultiplier { get; }
    decimal RollingAvgR { get; }
    int ConsecutiveStrategyFails { get; }
    bool IsInCoolOff(DateTime utcNow, out DateTime? untilUtc);
}
