using FluentAssertions;
using VertexAutoTrade.Core.Risk;
using Xunit;

namespace VertexAutoTrade.RiskEngine.Tests;

public class CorrelationMathTests
{
    [Fact]
    public void Identical_Series_Correlation_Near_One()
    {
        var closes = Enumerable.Range(1, 30).Select(i => 100m + i).ToList();
        var r = CorrelationMath.PearsonLogReturns(closes, closes, 48);
        r.Should().NotBeNull();
        r!.Value.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void Inverse_Series_Correlation_Negative()
    {
        // Build two price paths with opposite log-returns (not just mirrored levels:
        // arithmetic mirror K-p yields near-perfect *positive* corr on log-returns of trends).
        var a = new List<decimal> { 100m };
        var b = new List<decimal> { 100m };
        for (int i = 0; i < 40; i++)
        {
            double ret = Math.Sin(i * 0.7) * 0.02;
            a.Add(a[^1] * (decimal)(1.0 + ret));
            b.Add(b[^1] * (decimal)(1.0 - ret));
        }

        var r = CorrelationMath.PearsonLogReturns(a, b, 48);
        r.Should().NotBeNull();
        r!.Value.Should().BeLessThan(-0.9);
    }

    [Fact]
    public void Pearson_On_Explicit_Inverse_Vectors_Is_Minus_One()
    {
        var x = new double[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var y = new double[] { -1, -2, -3, -4, -5, -6, -7, -8 };
        var r = CorrelationMath.Pearson(x, y);
        r.Should().NotBeNull();
        r!.Value.Should().BeApproximately(-1.0, 1e-9);
    }
}
