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
        var a = Enumerable.Range(1, 30).Select(i => 100m + i).ToList();
        var b = Enumerable.Range(1, 30).Select(i => 200m - i).ToList();
        var r = CorrelationMath.PearsonLogReturns(a, b, 48);
        r.Should().NotBeNull();
        r!.Value.Should().BeLessThan(-0.5);
    }
}
