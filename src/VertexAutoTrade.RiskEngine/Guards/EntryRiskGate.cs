using VertexAutoTrade.RiskEngine.Abstractions;

namespace VertexAutoTrade.RiskEngine.Guards;

/// <summary>
/// Ordered gate: HardRisk first, then AntiTilt.
/// Strategy / execution must call this before sizing and order send.
/// </summary>
public sealed class EntryRiskGate : IEntryRiskGate
{
    private readonly IHardRiskGuard _hard;
    private readonly IAntiTiltCircuitBreaker _tilt;

    public EntryRiskGate(IHardRiskGuard hard, IAntiTiltCircuitBreaker tilt)
    {
        _hard = hard;
        _tilt = tilt;
    }

    public EntryPermission Evaluate(RiskSnapshot snapshot, decimal configuredBaseRiskFraction)
    {
        var hard = _hard.EvaluateEntry(snapshot);
        if (!hard.Allowed)
            return hard;

        var baseRisk = configuredBaseRiskFraction > 0
            ? configuredBaseRiskFraction
            : hard.EffectiveRiskFraction;

        return _tilt.EvaluateEntry(snapshot.UtcNow, baseRisk);
    }
}
