using VertexAutoTrade.Core.Market;
using VertexAutoTrade.Core.Pipeline;

namespace VertexAutoTrade.Regime;

/// <summary>
/// Lightweight regime from OHLC series (caller supplies closes/high/low).
/// ADX proxy via efficiency ratio + ATR ratio + BB width.
/// </summary>
public sealed class MarketRegimeAnalyzer
{
    private readonly RegimeOptions _opt;
    public MarketRegimeAnalyzer(RegimeOptions? opt = null) => _opt = opt ?? new RegimeOptions();

    public MarketRegimeKind Classify(IReadOnlyList<decimal> closes, IReadOnlyList<decimal> highs, IReadOnlyList<decimal> lows)
    {
        if (closes.Count < 30) return MarketRegimeKind.Unknown;

        var atr = AvgTrueRange(highs, lows, closes, 14);
        var atrLong = AvgTrueRange(highs, lows, closes, 50);
        var atrRatio = atrLong > 0 ? atr / atrLong : 1m;

        var mid = Sma(closes, 20);
        var std = Stdev(closes, 20);
        var bbWidth = mid > 0 && std > 0 ? (2m * std) / mid : 0m;

        // Efficiency ratio as ADX proxy: net move / sum abs moves
        var er = EfficiencyRatio(closes, 14);

        if (_opt.BlockHighVolatilityChop && atrRatio >= _opt.AtrRatioChopThreshold && er < 0.25m)
            return MarketRegimeKind.HighVolatilityChop;
        if (bbWidth >= _opt.BbWidthChopThreshold && er < 0.20m)
            return MarketRegimeKind.HighVolatilityChop;
        if (er * 100m >= _opt.AdxTrendThreshold * 0.5m) // scale ER to rough ADX-like
            return MarketRegimeKind.Trend;
        return MarketRegimeKind.Range;
    }

    public GateDecision EvaluateEntry(MarketRegimeKind regime)
    {
        if (regime == MarketRegimeKind.HighVolatilityChop && _opt.BlockHighVolatilityChop)
            return GateDecision.Reject(GateLayer.Regime, "REGIME_CHOP", "HighVolatilityChop — new entries blocked");
        return GateDecision.Ok(GateLayer.Regime, regime.ToString());
    }

    private static decimal EfficiencyRatio(IReadOnlyList<decimal> c, int n)
    {
        if (c.Count < n + 1) return 0m;
        var net = Math.Abs(c[^1] - c[^(n + 1)]);
        decimal sum = 0;
        for (int i = c.Count - n; i < c.Count; i++)
            sum += Math.Abs(c[i] - c[i - 1]);
        return sum > 0 ? net / sum : 0m;
    }

    private static decimal AvgTrueRange(IReadOnlyList<decimal> h, IReadOnlyList<decimal> l, IReadOnlyList<decimal> c, int n)
    {
        if (c.Count < n + 1) return 0m;
        decimal sum = 0;
        int cnt = 0;
        for (int i = c.Count - n; i < c.Count; i++)
        {
            var tr = Math.Max(h[i] - l[i], Math.Max(Math.Abs(h[i] - c[i - 1]), Math.Abs(l[i] - c[i - 1])));
            sum += tr; cnt++;
        }
        return cnt > 0 ? sum / cnt : 0m;
    }

    private static decimal Sma(IReadOnlyList<decimal> c, int n)
    {
        if (c.Count < n) return 0m;
        decimal s = 0;
        for (int i = c.Count - n; i < c.Count; i++) s += c[i];
        return s / n;
    }

    private static decimal Stdev(IReadOnlyList<decimal> c, int n)
    {
        var m = Sma(c, n);
        if (m == 0 || c.Count < n) return 0m;
        decimal v = 0;
        for (int i = c.Count - n; i < c.Count; i++)
        {
            var d = c[i] - m;
            v += d * d;
        }
        return (decimal)Math.Sqrt((double)(v / n));
    }
}
