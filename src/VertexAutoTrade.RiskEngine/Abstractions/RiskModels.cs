namespace VertexAutoTrade.RiskEngine.Abstractions;

public enum RiskBlockReason
{
    None = 0,
    DailyLossR,
    DailyLossEquity,
    MaxOpenPositions,
    HardBlockActive,
    CoolOffActive,
    InvalidStopDistance,
    QtyBelowMinimum
}

public sealed record RiskSnapshot(
    DateTime UtcNow,
    decimal EquityUsd,
    decimal RealizedPnlTodayUsd,
    decimal RealizedRToday,
    int OpenPositionCount,
    decimal? LastClosedR = null,
    string? LastCloseReason = null);

public sealed record ClosedTradeRiskEvent(
    DateTime ClosedAtUtc,
    decimal RealizedR,
    string CloseReason,
    string Symbol);

public sealed record PositionSizeRequest(
    decimal EquityUsd,
    decimal EntryPrice,
    decimal StopLossPrice,
    decimal RiskFraction,
    decimal? MinQty = null,
    decimal? StepSize = null,
    decimal? MinNotional = null);

public sealed record PositionSizeResult(
    bool Ok,
    decimal Quantity,
    decimal RiskUsd,
    decimal StopDistance,
    string? RejectReason = null);

public sealed record EntryPermission(
    bool Allowed,
    RiskBlockReason Reason,
    string Message,
    decimal EffectiveRiskFraction,
    DateTime? BlockedUntilUtc = null);
