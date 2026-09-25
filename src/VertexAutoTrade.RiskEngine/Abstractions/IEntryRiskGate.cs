namespace VertexAutoTrade.RiskEngine.Abstractions;

/// <summary>
/// Composite gate: HardRisk → AntiTilt → (caller applies PositionSizer).
/// Must run BEFORE strategy execution / order send.
/// </summary>
public interface IEntryRiskGate
{
    EntryPermission Evaluate(RiskSnapshot snapshot, decimal configuredBaseRiskFraction);
}
