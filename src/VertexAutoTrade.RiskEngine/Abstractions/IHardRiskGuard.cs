namespace VertexAutoTrade.RiskEngine.Abstractions;

public interface IHardRiskGuard
{
    EntryPermission EvaluateEntry(RiskSnapshot snapshot);

    /// <summary>Call after a hard trip to lock trading.</summary>
    void Trip(RiskBlockReason reason, DateTime utcNow, string detail);

    void ClearBlock();

    bool IsTradingBlocked(DateTime utcNow, out DateTime? untilUtc, out string? detail);

    /// <summary>Optional: accumulate realized R for the UTC day.</summary>
    void RegisterClosedR(decimal realizedR, DateTime closedAtUtc);
}
