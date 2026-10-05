using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Strategy;

/// <summary>
/// Last-line entry quality gate for ALL strategies (CORE / RANGE / any future leg).
/// Applied in StrategyRouter (best-effort) and TradingWorker (hard reject before orders).
/// Closes parabolic-chase holes that individual engines miss after pumps.
/// </summary>
public static class EntrySanityGate
{
    /// <summary>Hard reject → do not trade. Soft reasons still log.</summary>
    public static bool Allow(
        TradeSignal signal,
        IReadOnlyList<BinanceFuturesUsdtKline>? m15,
        IReadOnlyList<BinanceFuturesUsdtKline>? h1,
        out string reason)
    {
        reason = "";
        if (signal == null)
        {
            reason = "NULL_SIGNAL";
            return false;
        }

        bool isLong = signal.Side == SignalSide.Buy;
        decimal entry = signal.EntryPrice;
        if (entry <= 0)
        {
            reason = "BAD_ENTRY";
            return false;
        }

        // RANGE / mean-reversion trades AT extremes by design — do not apply TREND chase bans
        bool rangeLeg = ExecutableStrategyPolicy.IsRangeLeg(signal.Reason);

        // Geometry: micro SL / absurd SL
        if (signal.StopLoss > 0)
        {
            decimal risk = Math.Abs(entry - signal.StopLoss);
            decimal riskPct = risk / entry;
            if (riskPct < 0.004m)
            {
                reason = $"SANITY_MICRO_SL {riskPct:P2}";
                return false;
            }
            if (riskPct > 0.06m)
            {
                reason = $"SANITY_SL_TOO_FAR {riskPct:P2}";
                return false;
            }
            // Side consistency
            if (isLong && signal.StopLoss >= entry)
            {
                reason = "SANITY_SL_SIDE_LONG";
                return false;
            }
            if (!isLong && signal.StopLoss <= entry)
            {
                reason = "SANITY_SL_SIDE_SHORT";
                return false;
            }
        }

        // TREND-only chase bans (RANGE leg intentionally fades extremes)
        if (!rangeLeg)
        {
            if (h1 != null && h1.Count >= 16)
            {
                var list = h1.OrderBy(x => x.OpenTime).ToList();
                var para = PassParabolic(list, isLong, entry);
                if (!para.ok)
                {
                    reason = para.reason;
                    return false;
                }
            }

            if (m15 != null && m15.Count >= 8)
            {
                var list = m15.OrderBy(x => x.OpenTime).ToList();
                decimal atr = Atr(list, 14);
                if (atr > 0 && !PassImpulseWindow(list, isLong, atr, out var imp))
                {
                    reason = imp;
                    return false;
                }
            }

            var refBars = (h1 != null && h1.Count >= 12) ? h1.OrderBy(x => x.OpenTime).ToList()
                : (m15 != null && m15.Count >= 20) ? m15.OrderBy(x => x.OpenTime).ToList()
                : null;
            if (refBars != null)
            {
                var w = refBars.TakeLast(Math.Min(refBars.Count, 24)).ToList();
                decimal open0 = w[0].OpenPrice;
                if (open0 > 0)
                {
                    decimal move = (entry - open0) / open0;
                    if (isLong && move >= 0.18m)
                    {
                        reason = $"SANITY_SESSION_PUMP move={move:P1}";
                        return false;
                    }
                    if (!isLong && move <= -0.18m)
                    {
                        reason = $"SANITY_SESSION_DUMP move={move:P1}";
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static (bool ok, string reason) PassParabolic(
        List<BinanceFuturesUsdtKline> h1, bool isLong, decimal close)
    {
        int n = h1.Count;
        var win = h1.Skip(Math.Max(0, n - 24)).ToList();
        decimal hi = win.Max(x => x.HighPrice);
        decimal lo = win.Min(x => x.LowPrice);
        decimal span = hi - lo;
        if (span <= 0) return (true, "");

        decimal pos = (close - lo) / span;
        if (isLong && pos >= 0.80m)
            return (false, $"SANITY_PARABOLIC_TOP pos={pos:F2}");
        if (!isLong && pos <= 0.20m)
            return (false, $"SANITY_PARABOLIC_BOTTOM pos={pos:F2}");

        var w12 = h1.Skip(Math.Max(0, n - 12)).ToList();
        decimal c0 = w12[0].OpenPrice;
        if (c0 > 0)
        {
            decimal move = (w12[^1].ClosePrice - c0) / c0;
            if (isLong && move >= 0.10m)
            {
                decimal impulseHigh = w12.Max(x => x.HighPrice);
                decimal retrace = impulseHigh > c0 ? (impulseHigh - close) / (impulseHigh - c0) : 0m;
                if (retrace < 0.28m)
                    return (false, $"SANITY_PARABOLIC_LONG move={move:P1} retrace={retrace:F2}");
            }
            if (!isLong && move <= -0.10m)
            {
                decimal impulseLow = w12.Min(x => x.LowPrice);
                decimal retrace = c0 > impulseLow ? (close - impulseLow) / (c0 - impulseLow) : 0m;
                if (retrace < 0.28m)
                    return (false, $"SANITY_PARABOLIC_SHORT move={move:P1} retrace={retrace:F2}");
            }
        }

        int sameDir = 0;
        for (int i = Math.Max(0, n - 3); i < n; i++)
        {
            var b = h1[i];
            if (isLong && b.ClosePrice > b.OpenPrice) sameDir++;
            if (!isLong && b.ClosePrice < b.OpenPrice) sameDir++;
        }
        if (isLong && sameDir >= 3 && pos >= 0.68m)
            return (false, "SANITY_3GREEN_TOP");
        if (!isLong && sameDir >= 3 && pos <= 0.32m)
            return (false, "SANITY_3RED_BOTTOM");

        return (true, "");
    }

    private static bool PassImpulseWindow(
        List<BinanceFuturesUsdtKline> k, bool isLong, decimal atr, out string reason)
    {
        reason = "";
        var win = k.TakeLast(8).ToList();
        decimal wHi = win.Max(x => x.HighPrice);
        decimal wLo = win.Min(x => x.LowPrice);
        decimal wSpan = wHi - wLo;
        if (wSpan <= 0) return true;
        decimal px = k[^1].ClosePrice;
        decimal wPos = (px - wLo) / wSpan;
        bool anyImpulse = win.Any(b => (b.HighPrice - b.LowPrice) >= atr * 1.20m);
        if (isLong && anyImpulse && wPos >= 0.86m)
        {
            reason = $"SANITY_IMPULSE_TOP pos={wPos:F2}";
            return false;
        }
        if (!isLong && anyImpulse && wPos <= 0.14m)
        {
            reason = $"SANITY_IMPULSE_BOT pos={wPos:F2}";
            return false;
        }
        return true;
    }

    private static decimal Atr(List<BinanceFuturesUsdtKline> k, int period)
    {
        if (k.Count < period + 2) return 0;
        decimal sum = 0;
        int start = k.Count - period;
        for (int i = start; i < k.Count; i++)
        {
            decimal tr = k[i].HighPrice - k[i].LowPrice;
            if (i > 0)
            {
                tr = Math.Max(tr, Math.Abs(k[i].HighPrice - k[i - 1].ClosePrice));
                tr = Math.Max(tr, Math.Abs(k[i].LowPrice - k[i - 1].ClosePrice));
            }
            sum += tr;
        }
        return sum / period;
    }
}
