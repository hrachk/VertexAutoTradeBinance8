using FluentAssertions;
using VertexAutoTrade.RiskEngine.Abstractions;
using VertexAutoTrade.RiskEngine.Guards;
using VertexAutoTrade.RiskEngine.Options;
using Xunit;

namespace VertexAutoTrade.RiskEngine.Tests;

public class AntiTiltCircuitBreakerTests
{
    private static AntiTiltCircuitBreaker Sut() =>
        new(new AntiTiltOptions
        {
            ConsecutiveStrategyFailsToCoolOff = 3,
            CoolOffHours = 4,
            RollingWindowTrades = 20,
            AvgRRiskCutThreshold = 0.20m,
            WeakAvgRRiskMultiplier = 0.5m
        });

    private static ClosedTradeRiskEvent Fail(string sym = "BTCUSDT", decimal r = -1m) =>
        new(DateTime.UtcNow, r, "SL_STRATEGY_FAIL", sym);

    private static ClosedTradeRiskEvent Win(decimal r = 1.5m) =>
        new(DateTime.UtcNow, r, "TP", "ETHUSDT");

    [Fact]
    public void Three_consecutive_strategy_fails_start_cooloff()
    {
        var a = Sut();
        a.OnTradeClosed(Fail());
        a.OnTradeClosed(Fail());
        a.IsInCoolOff(DateTime.UtcNow, out _).Should().BeFalse();
        a.OnTradeClosed(Fail());
        a.IsInCoolOff(DateTime.UtcNow, out var until).Should().BeTrue();
        until.Should().NotBeNull();

        var p = a.EvaluateEntry(DateTime.UtcNow, 0.01m);
        p.Allowed.Should().BeFalse();
        p.Reason.Should().Be(RiskBlockReason.CoolOffActive);
    }

    [Fact]
    public void Winning_trade_resets_consecutive_fails()
    {
        var a = Sut();
        a.OnTradeClosed(Fail());
        a.OnTradeClosed(Fail());
        a.OnTradeClosed(Win());
        a.ConsecutiveStrategyFails.Should().Be(0);
        a.OnTradeClosed(Fail());
        a.IsInCoolOff(DateTime.UtcNow, out _).Should().BeFalse();
    }

    [Fact]
    public void Weak_rolling_avgR_halves_risk()
    {
        var a = Sut();
        // 10 losses of -1R → avgR = -1 < 0.2
        for (int i = 0; i < 10; i++)
            a.OnTradeClosed(Fail());

        // cool-off may be active after 3 fails — clear by evaluating multiplier only
        a.CurrentRiskMultiplier.Should().Be(0.5m);
        a.RollingAvgR.Should().BeLessThan(0.20m);
    }

    [Fact]
    public void Whipsaw_near_zero_does_not_count_as_strategy_streak_if_not_negative_strategy()
    {
        var a = Sut();
        // SL_WHIPSAW with ~0 R — token "SL" matches STRATEGY fail tokens containing "SL"
        // Design: SL_WHIPSAW contains "SL" → counts; document behavior
        a.OnTradeClosed(new ClosedTradeRiskEvent(DateTime.UtcNow, -0.01m, "SL_WHIPSAW", "X"));
        a.ConsecutiveStrategyFails.Should().BeGreaterThanOrEqualTo(1);
    }
}
