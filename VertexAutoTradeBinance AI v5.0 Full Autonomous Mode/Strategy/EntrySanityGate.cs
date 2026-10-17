using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Strategy;

/// <summary>
/// Last-line entry quality gate for ALL strategies (CORE / RANGE / any future leg).
/// Applied in StrategyRouter (best-effort) and TradingWorker (hard reject before orders).
///
/// TREND SL geometry aligned with InstitutionalTrendSetup: 0.9% .. 2.8%.
/// RANGE fades are allowed a tighter stop (0.45% .. 2.2%) — a 0.9% floor
/// made mean-reversion R:R impossible and the leg never fired.
///
/// Anti-chase is "vertical move with no retrace", not "price is high in the
/// 24h range". An uptrend lives in the top of its range; banning that banned every trend.
/// </summary>
public static class EntrySanityGate
{
    public const decimal MinRiskPct = 0.009m;
    public const decimal MaxRiskPct = 0.028m;
    public const decimal RangeMinRiskPct = 0.0045m;
    public const decimal RangeMaxRiskPct = 0.022m;

    /// <summary>Hard reject → do not trade.</summary>
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

        signal.EnsureSetup();
        if (string.IsNullOrWhiteSpace(signal.Setup) && string.IsNullOrWhiteSpace(signal.Reason))
        {
            reason = "SANITY_NO_SETUP";
            return false;
        }
        if (!ExecutableStrategyPolicy.IsLiveExecutable(signal.Setup ?? signal.Reason))
        {
            reason = $"SANITY_BAD_SETUP {(signal.Setup ?? signal.Reason)}";
            return false;
        }

        bool isLong = signal.Side == SignalSide.Buy;
        decimal entry = signal.EntryPrice;
        if (entry <= 0)
        {
            reason = "BAD_ENTRY";
            return false;
        }

        bool rangeLeg = ExecutableStrategyPolicy.IsRangeLeg(signal.Setup ?? signal.Reason);
        decimal minRisk = rangeLeg ? RangeMinRiskPct : MinRiskPct;
        decimal maxRisk = rangeLeg ? RangeMaxRiskPct : MaxRiskPct;

        if (signal.StopLoss > 0)
        {
            decimal risk = Math.Abs(entry - signal.StopLoss);
            decimal riskPct = risk / entry;
            if (riskPct < minRisk)
            {
                reason = $"SANITY_MICRO_SL {riskPct:P2} (min {minRisk:P1})";
                return false;
            }
            if (riskPct > maxRisk)
            {
                reason = $"SANITY_SL_TOO_FAR {riskPct:P2} (max {maxRisk:P1})";
                return false;
            }
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
        else
        {
            reason = "SANITY_NO_SL";
            return false;
        }

        if (!rangeLeg)
        {
            if (h1 != null && h1.Count >= 16)
            {
                var list = h1.OrderBy(x => x.OpenTime).ToList();
                var para = PassParabolic(list, isLong);
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
        }

        if (signal.TakeProfits is { Count: > 0 })
        {
            decimal tp1 = signal.TakeProfits[0];
            decimal reward = isLong ? tp1 - entry : entry - tp1;
            decimal risk = Math.Abs(entry - signal.StopLoss);
            if (reward <= 0 || risk <= 0 || reward / risk < 1.15m)
            {
                reason = $"SANITY_RR {(risk > 0 ? reward / risk : 0m):F2}";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Replace the planned close with the live price only when geometry still holds.
    /// Blind overwrite used to move entry through the stop or kill R:R after the signal was built.
    /// </summary>
    public static bool AllowPriceUpdate(TradeSignal signal, decimal livePrice, out string reason)
    {
        reason = "";
        if (signal == null || signal.EntryPrice <= 0 || livePrice <= 0 || signal.StopLoss <= 0)
        {
            reason = "BAD_PRICE";
            return false;
        }

        decimal planned = signal.EntryPrice;
        bool isLong = signal.Side == SignalSide.Buy;
        decimal drift = (livePrice - planned) / planned;
        decimal atrPct = signal.Atr is decimal atr && atr > 0 ? atr / planned : 0.01m;
        // About one 1H ATR of adverse drift — normal lag is fine, a new impulse is not.
        decimal maxChase = Math.Clamp(atrPct * 1.0m, 0.006m, 0.018m);

        if (isLong && drift > maxChase)
        {
            reason = $"CHASE_LONG drift={drift:P2}";
            return false;
        }
        if (!isLong && drift < -maxChase)
        {
            reason = $"CHASE_SHORT drift={drift:P2}";
            return false;
        }
        if (isLong && livePrice <= signal.StopLoss)
        {
            reason = "LIVE_THROUGH_SL";
            return false;
        }
        if (!isLong && livePrice >= signal.StopLoss)
        {
            reason = "LIVE_THROUGH_SL";
            return false;
        }

        bool rangeLeg = ExecutableStrategyPolicy.IsRangeLeg(signal.Setup ?? signal.Reason);
        decimal minRisk = rangeLeg ? RangeMinRiskPct : MinRiskPct;
        decimal maxRisk = rangeLeg ? RangeMaxRiskPct : MaxRiskPct;
        decimal risk = Math.Abs(livePrice - signal.StopLoss);
        decimal riskPct = risk / livePrice;
        if (riskPct < minRisk)
        {
            reason = $"DRIFT_MICRO_SL {riskPct:P2}";
            return false;
        }
        if (riskPct > maxRisk)
        {
            reason = $"DRIFT_SL_TOO_FAR {riskPct:P2}";
            return false;
        }

        if (signal.TakeProfits is { Count: > 0 })
        {
            decimal tp1 = signal.TakeProfits[0];
            decimal reward = isLong ? tp1 - livePrice : livePrice - tp1;
            if (reward <= 0 || reward / risk < 1.15m)
            {
                reason = $"DRIFT_RR {(risk > 0 ? reward / risk : 0m):F2}";
                return false;
            }
            signal.RRatio = reward / risk;
        }

        signal.EntryPrice = livePrice;
        return true;
    }

    private static (bool ok, string reason) PassParabolic(List<BinanceFuturesUsdtKline> h1, bool isLong)
    {
        int n = h1.Count;
        var w12 = h1.Skip(Math.Max(0, n - 12)).ToList();
        if (w12.Count < 4) return (true, "");
        decimal c0 = w12[0].OpenPrice;
        decimal close = h1[^1].ClosePrice;
        if (c0 <= 0 || close <= 0) return (true, "");

        decimal hi = w12.Max(x => x.HighPrice);
        decimal lo = w12.Min(x => x.LowPrice);
        decimal move = (w12[^1].ClosePrice - c0) / c0;

        if (isLong && move >= 0.10m && hi > c0)
        {
            decimal retrace = (hi - close) / (hi - c0);
            if (retrace < 0.20m)
                return (false, $"SANITY_PARABOLIC_LONG move={move:P1} retrace={retrace:F2}");
        }
        if (!isLong && move <= -0.10m && c0 > lo)
        {
            decimal retrace = (close - lo) / (c0 - lo);
            if (retrace < 0.20m)
                return (false, $"SANITY_PARABOLIC_SHORT move={move:P1} retrace={retrace:F2}");
        }

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
        if (isLong && anyImpulse && wPos >= 0.92m)
        {
            reason = $"SANITY_IMPULSE_TOP pos={wPos:F2}";
            return false;
        }
        if (!isLong && anyImpulse && wPos <= 0.08m)
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
            var bar = k[i];
            decimal prevClose = k[i - 1].ClosePrice;
            decimal tr = Math.Max(bar.HighPrice - bar.LowPrice,
                Math.Max(Math.Abs(bar.HighPrice - prevClose), Math.Abs(bar.LowPrice - prevClose)));
            sum += tr;
        }
        return sum / period;
    }
}
