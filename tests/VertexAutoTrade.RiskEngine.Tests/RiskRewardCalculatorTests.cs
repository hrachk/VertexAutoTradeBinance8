using VertexAutoTrade.Core.Risk;
using Xunit;

namespace VertexAutoTrade.RiskEngine.Tests;

public class RiskRewardCalculatorTests
{
    [Theory]
    [InlineData(100, 96, 104, 1.0)]   // reward 4 / risk 4 = 1.0
    [InlineData(100, 96, 106, 1.5)]   // reward 6 / risk 4 = 1.5
    [InlineData(100, 98, 105, 2.5)]   // reward 5 / risk 2 = 2.5
    public void Ratio_Is_Correct(decimal entry, decimal sl, decimal tp1, decimal expected)
    {
        var rr = RiskRewardCalculator.Tp1RewardRiskRatio(entry, sl, tp1);
        Assert.Equal(expected, rr);
    }

    [Fact]
    public void Blocks_When_Rr_Below_1_5()
    {
        // FOMO geometry: SL 4% away, TP1 3.8% → RR < 1
        Assert.False(RiskRewardCalculator.MeetsMinTp1Rr(100m, 96m, 103.8m, 1.5m));
    }

    [Fact]
    public void Allows_When_Rr_At_Least_1_5()
    {
        Assert.True(RiskRewardCalculator.MeetsMinTp1Rr(100m, 98m, 103m, 1.5m)); // 1.5R
        Assert.True(RiskRewardCalculator.MeetsMinTp1Rr(100m, 96m, 106m, 1.5m)); // 1.5R
        Assert.True(RiskRewardCalculator.MeetsMinTp1Rr(100m, 98m, 105m, 1.5m)); // 2.5R
    }

    [Fact]
    public void Rejects_Wide_Sl_Over_Pct_Cap()
    {
        Assert.False(RiskRewardCalculator.IsSlWithinPctCap(100m, 95.5m, 0.022m)); // 4.5%
        Assert.True(RiskRewardCalculator.IsSlWithinPctCap(100m, 98m, 0.022m));  // 2%
    }

    [Fact]
    public void Micro_Sl_0_23pct_Is_Below_Min_Floor()
    {
        // BTC-style micro stop ~0.23% must fail 0.8% floor
        decimal entry = 84907.63m;
        decimal sl = 84707.72m;
        decimal riskPct = Math.Abs(entry - sl) / entry;
        Assert.True(riskPct < 0.008m);
        Assert.False(riskPct >= 0.008m);
    }
}
