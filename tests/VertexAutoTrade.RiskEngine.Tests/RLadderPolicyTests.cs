using FluentAssertions;
using VertexAutoTrade.Core.Risk;
using Xunit;

namespace VertexAutoTrade.RiskEngine.Tests;

public class RLadderPolicyTests
{
    private static readonly RLadderOptions O = new RLadderOptions().Normalized();

    [Fact]
    public void Targets_are_R_multiples_for_long_and_short()
    {
        RLadderPolicy.TargetPrices(100m, 99m, true, O).Should().Equal(102m, 103m, 105m, 107m);
        RLadderPolicy.TargetPrices(100m, 101m, false, O).Should().Equal(98m, 97m, 95m, 93m);
    }

    [Theory]
    [InlineData(1.9, 0, null)]
    [InlineData(2.0, 1, 0.0)]
    [InlineData(3.2, 2, 1.0)]
    [InlineData(4.0, 2, 2.0)]
    [InlineData(5.5, 3, 3.0)]
    [InlineData(6.4, 3, 4.0)]
    public void Lock_ladder_follows_peak(double peak, int filled, double? expected)
    {
        var r = RLadderPolicy.LockedStopR((decimal)peak, filled, O);
        if (expected is null) r.Should().BeNull();
        else r.Should().Be((decimal)expected.Value);
    }

    [Fact]
    public void Break_even_includes_fee_buffer()
    {
        RLadderPolicy.LockedStopPrice(100m, 1m, true, 2m, 1, O).Should().Be(100.12m);
        RLadderPolicy.LockedStopPrice(100m, 1m, false, 2m, 1, O).Should().Be(99.88m);
    }

    [Fact]
    public void Room_filter_measures_R_to_opposing_level()
    {
        RLadderPolicy.RoomR(100m, 99m, true, 104m).Should().Be(4m);
        RLadderPolicy.RoomR(100m, 101m, false, 98.5m).Should().Be(1.5m);
        RLadderPolicy.RoomR(100m, 99m, true, null).Should().Be(decimal.MaxValue);
    }

    [Fact]
    public void Expectancy_matches_hand_calculation()
    {
        // k0 0.70×−1, k1 0.12×0.5, k2 0.10×1.75, k3 0.04×3.25, k4 0.04×4.25 = −0.165
        RLadderPolicy.ExpectancyR(new[] { 0.30m, 0.18m, 0.08m, 0.04m }, O).Should().Be(-0.165m);
    }

    [Fact]
    public void Normalized_sorts_targets_and_rescales_weights()
    {
        var o = new RLadderOptions { TargetsR = new[] { 3m, 2m, 0m }, Weights = new[] { 1m, 1m } }.Normalized();
        o.TargetsR.Should().Equal(2m, 3m);
        o.Weights.Sum().Should().Be(1m);
    }

    [Fact]
    public void Drawdown_scaler_cuts_risk_never_raises_it()
    {
        EquityCurveRiskScaler.Multiplier(100m, 100m).Should().Be(1m);
        EquityCurveRiskScaler.Multiplier(96m, 100m).Should().Be(0.85m);
        EquityCurveRiskScaler.Multiplier(90m, 100m).Should().Be(0.5m);
        EquityCurveRiskScaler.Multiplier(80m, 100m).Should().Be(0.25m);
    }

    [Fact]
    public void Scaling_replays_fixed_fractional_compounding()
    {
        // 250 $, 10% of CURRENT balance per trade, 1:4, 25 trades (7 wins) → ≈ 615.30 $
        var seq = "LLWWLLWLLLWLWLLLLWLWLWLLL".Select(c => c == 'W').ToList();
        var rows = RiskScaling.Simulate(250m, 0.10m, 4m, seq);
        rows[0].RiskUsd.Should().Be(25m);
        rows[2].ResultUsd.Should().Be(81m);
        rows[^1].Balance.Should().BeApproximately(615.30m, 0.05m);
    }

    [Fact]
    public void Volume_is_risk_divided_by_stop_distance()
    {
        RiskScaling.QtyForRisk(26m, 76.17m, 76.04m).Should().Be(200m);
        RiskScaling.Kelly(0.30m, 4m).Should().Be(0.125m);
        RiskScaling.ExpectancyR(0.30m, 4m).Should().Be(0.5m);
    }
}
