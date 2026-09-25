using FluentAssertions;
using VertexAutoTrade.RiskEngine.Abstractions;
using VertexAutoTrade.RiskEngine.Sizing;
using Xunit;

namespace VertexAutoTrade.RiskEngine.Tests;

public class PositionSizerTests
{
    [Fact]
    public void Calculates_qty_from_equity_risk_and_stop_distance()
    {
        // Equity 10_000, risk 1% = 100 USD, stop distance 10 → qty = 10
        var s = new PositionSizer();
        var r = s.Calculate(new PositionSizeRequest(
            EquityUsd: 10_000m,
            EntryPrice: 100m,
            StopLossPrice: 90m,
            RiskFraction: 0.01m));

        r.Ok.Should().BeTrue();
        r.RiskUsd.Should().Be(100m);
        r.StopDistance.Should().Be(10m);
        r.Quantity.Should().Be(10m);
    }

    [Fact]
    public void Applies_hard_cap_on_risk_usd()
    {
        var s = new PositionSizer(hardCapRiskUsd: 50m);
        var r = s.Calculate(new PositionSizeRequest(10_000m, 100m, 90m, 0.01m));
        r.Ok.Should().BeTrue();
        r.RiskUsd.Should().Be(50m);
        r.Quantity.Should().Be(5m);
    }

    [Fact]
    public void Rejects_entry_equals_stop()
    {
        var s = new PositionSizer();
        var r = s.Calculate(new PositionSizeRequest(10_000m, 100m, 100m, 0.01m));
        r.Ok.Should().BeFalse();
        r.RejectReason.Should().Contain("distance");
    }

    [Fact]
    public void Respects_step_size_and_min_qty()
    {
        var s = new PositionSizer();
        var r = s.Calculate(new PositionSizeRequest(
            10_000m, 100m, 90m, 0.01m,
            MinQty: 1m, StepSize: 0.5m));
        r.Ok.Should().BeTrue();
        r.Quantity.Should().Be(10m);
    }

    [Fact]
    public void Rejects_below_min_notional()
    {
        var s = new PositionSizer();
        var r = s.Calculate(new PositionSizeRequest(
            EquityUsd: 100m,
            EntryPrice: 50_000m,
            StopLossPrice: 49_000m,
            RiskFraction: 0.01m,
            MinNotional: 100m));
        // risk = 1 USD, dist = 1000 → qty = 0.001, notional = 50 < 100
        r.Ok.Should().BeFalse();
        r.RejectReason.Should().Contain("minNotional");
    }
}
