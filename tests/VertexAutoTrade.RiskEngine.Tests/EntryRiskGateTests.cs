using FluentAssertions;
using VertexAutoTrade.RiskEngine.Abstractions;
using VertexAutoTrade.RiskEngine.Guards;
using VertexAutoTrade.RiskEngine.Options;
using Xunit;

namespace VertexAutoTrade.RiskEngine.Tests;

public class EntryRiskGateTests
{
    [Fact]
    public void Hard_block_takes_priority_over_tilt()
    {
        var hard = new HardRiskGuard(new HardRiskOptions { MaxOpenPositions = 1 });
        var tilt = new AntiTiltCircuitBreaker(new AntiTiltOptions());
        var gate = new EntryRiskGate(hard, tilt);

        var p = gate.Evaluate(
            new RiskSnapshot(DateTime.UtcNow, 10_000m, 0, 0, OpenPositionCount: 1),
            0.01m);

        p.Allowed.Should().BeFalse();
        p.Reason.Should().Be(RiskBlockReason.MaxOpenPositions);
    }

    [Fact]
    public void Applies_tilt_risk_multiplier_when_hard_allows()
    {
        var hard = new HardRiskGuard(new HardRiskOptions { MaxOpenPositions = 5, BaseRiskFraction = 0.01m });
        var tilt = new AntiTiltCircuitBreaker(new AntiTiltOptions
        {
            RollingWindowTrades = 20,
            AvgRRiskCutThreshold = 0.20m,
            WeakAvgRRiskMultiplier = 0.5m,
            ConsecutiveStrategyFailsToCoolOff = 99
        });
        for (int i = 0; i < 12; i++)
            tilt.OnTradeClosed(new ClosedTradeRiskEvent(DateTime.UtcNow, -1m, "SL_STRATEGY_FAIL", "X"));

        var gate = new EntryRiskGate(hard, tilt);
        var p = gate.Evaluate(
            new RiskSnapshot(DateTime.UtcNow, 10_000m, 0, 0, 0),
            0.01m);

        p.Allowed.Should().BeTrue();
        p.EffectiveRiskFraction.Should().Be(0.005m);
    }
}
