using FluentAssertions;
using VertexAutoTrade.RiskEngine.Abstractions;
using VertexAutoTrade.RiskEngine.Guards;
using VertexAutoTrade.RiskEngine.Options;
using Xunit;

namespace VertexAutoTrade.RiskEngine.Tests;

public class HardRiskGuardTests
{
    private static HardRiskGuard Sut(HardRiskOptions? o = null) =>
        new(o ?? new HardRiskOptions
        {
            MaxDailyLossR = 3m,
            MaxOpenPositions = 3,
            MaxDailyLossEquityFraction = 0.03m,
            BlockHoursAfterTrip = 24,
            BaseRiskFraction = 0.01m
        });

    private static RiskSnapshot Snap(
        decimal equity = 10_000m,
        decimal pnlToday = 0m,
        decimal rToday = 0m,
        int open = 0,
        DateTime? utc = null) =>
        new(utc ?? new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc),
            equity, pnlToday, rToday, open);

    [Fact]
    public void Allows_entry_when_within_limits()
    {
        var g = Sut();
        var p = g.EvaluateEntry(Snap());
        p.Allowed.Should().BeTrue();
        p.EffectiveRiskFraction.Should().Be(0.01m);
    }

    [Fact]
    public void Blocks_when_max_open_positions_reached()
    {
        var g = Sut();
        var p = g.EvaluateEntry(Snap(open: 3));
        p.Allowed.Should().BeFalse();
        p.Reason.Should().Be(RiskBlockReason.MaxOpenPositions);
    }

    [Fact]
    public void Trips_and_blocks_on_daily_loss_R()
    {
        var g = Sut();
        var p = g.EvaluateEntry(Snap(rToday: -3.0m));
        p.Allowed.Should().BeFalse();
        p.Reason.Should().Be(RiskBlockReason.DailyLossR);

        var again = g.EvaluateEntry(Snap(rToday: -1m)); // still blocked
        again.Allowed.Should().BeFalse();
        again.Reason.Should().Be(RiskBlockReason.HardBlockActive);
    }

    [Fact]
    public void Trips_on_daily_equity_fraction()
    {
        var g = Sut();
        // −3% of 10k = −300
        var p = g.EvaluateEntry(Snap(equity: 10_000m, pnlToday: -300m));
        p.Allowed.Should().BeFalse();
        p.Reason.Should().Be(RiskBlockReason.DailyLossEquity);
    }

    [Fact]
    public void ClearBlock_restores_trading()
    {
        var g = Sut();
        g.Trip(RiskBlockReason.DailyLossR, DateTime.UtcNow, "test");
        g.ClearBlock();
        g.EvaluateEntry(Snap()).Allowed.Should().BeTrue();
    }

    [Fact]
    public void RegisterClosedR_accumulates_for_day()
    {
        var g = Sut(new HardRiskOptions { MaxDailyLossR = 2m, MaxOpenPositions = 5 });
        var day = new DateTime(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);
        g.RegisterClosedR(-1.0m, day);
        g.RegisterClosedR(-1.0m, day.AddHours(1));
        // snapshot R = 0 → uses internal −2R
        var p = g.EvaluateEntry(Snap(rToday: 0m, utc: day.AddHours(2)));
        p.Allowed.Should().BeFalse();
        p.Reason.Should().Be(RiskBlockReason.DailyLossR);
    }
}
