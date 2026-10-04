using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Strategy.StrategyCore;

/// <summary>
/// Professional TREND setup — single responsibility, pure logic (no I/O).
///
/// Rules (institutional):
/// 1. Direction only from 1H market structure (HH/HL or LH/LL) + EMA21/50.
/// 2. Trade location: LONG only in discount (&lt; 50% of last 1H swing range),
///    SHORT only in premium (&gt; 50%). No chasing mid-impulse.
/// 3. Trigger on 15m: reject candle into value (EMA21) in bias direction.
/// 4. Stop = 1H swing invalidation (not 15m noise).
/// 5. TP ladder from risk (min R:R 1.5 to TP1).
/// 6. Hard bans: parabolic range position, vertical session without retrace.
/// </summary>
public static class InstitutionalTrendSetup
{
    public const decimal MinConfidence = 0.62m;
    public const decimal MinRr = 1.50m;
    public const decimal StructurePadAtr15 = 0.25m;
    public const decimal MaxRiskPct = 0.032m;
    public const decimal MinRiskPct = 0.007m;

    public readonly record struct Result(bool Ok, TradeSignal? Signal, string Reason);

    public static Result TryBuild(
        string symbol,
        IReadOnlyList<BinanceFuturesUsdtKline> h1Raw,
        IReadOnlyList<BinanceFuturesUsdtKline> m15Raw,
        decimal? btcBias01 = null)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return Fail("no_symbol");
        if (h1Raw == null || h1Raw.Count < 55)
            return Fail("htf_1h_thin");
        if (m15Raw == null || m15Raw.Count < 40)
            return Fail("m15_thin");

        var h1 = h1Raw.OrderBy(x => x.OpenTime).ToList();
        var m15 = m15Raw.OrderBy(x => x.OpenTime).ToList();

        // Prefer last closed 1H if forming bar is young
        int hi = h1.Count - 1;
        var openT = h1[hi].OpenTime;
        if (openT.Kind == DateTimeKind.Unspecified)
            openT = DateTime.SpecifyKind(openT, DateTimeKind.Utc);
        if ((DateTime.UtcNow - openT.ToUniversalTime()).TotalMinutes < 50 && h1.Count >= 2)
            hi = h1.Count - 2;
        h1 = h1.Take(hi + 1).ToList();
        if (h1.Count < 55)
            return Fail("htf_1h_thin");

        var st = ReadStructure(h1);
        if (st == null)
            return Fail("no_1h_structure");

        var c1 = h1.Select(x => x.ClosePrice).ToList();
        var e21 = Ema(c1, 21);
        var e50 = Ema(c1, 50);
        int i1 = c1.Count - 1;
        decimal px1 = c1[i1], ema21 = e21[i1], ema50 = e50[i1];

        bool bull = st.IsBullish && ema21 >= ema50 * 0.999m && px1 > ema50;
        bool bear = st.IsBearish && ema21 <= ema50 * 1.001m && px1 < ema50;
        if (!bull && !bear)
            return Fail("no_clear_1h_bias");

        // Alts vs BTC
        if (btcBias01.HasValue && !IsMajor(symbol))
        {
            if (bull && btcBias01.Value < -0.45m)
                return Fail("btc_against_long");
            if (bear && btcBias01.Value > 0.45m)
                return Fail("btc_against_short");
        }

        // Volume participation on 1H
        decimal volR = VolRatio(h1, 8, 20);
        if (volR < 0.88m)
            return Fail($"dead_1h_volume:{volR:F2}");

        // Discount / premium relative to last swing
        decimal swingHi = st.LastSwingHigh;
        decimal swingLo = st.LastSwingLow;
        if (swingHi <= swingLo)
            return Fail("bad_swing_range");
        decimal eq = (swingHi + swingLo) / 2m;

        // Parabolic ban on 1H range (24 bars)
        var ban = ParabolicBan(h1, bull);
        if (ban != null)
            return Fail(ban);

        decimal atr15 = Atr(m15, 14);
        if (atr15 <= 0)
            return Fail("no_atr15");

        // 15m trigger
        var c15 = m15.Select(x => x.ClosePrice).ToList();
        var eF = Ema(c15, 21);
        var eS = Ema(c15, 55);
        int i = c15.Count - 1;
        var bar = m15[i];
        decimal close = bar.ClosePrice, open = bar.OpenPrice;
        decimal high = bar.HighPrice, low = bar.LowPrice;
        decimal zone = Math.Max(atr15 * 0.40m, close * 0.0018m);

        bool touchLong = low <= eF[i] + zone && close >= eF[i] - zone * 0.45m;
        bool touchShort = high >= eF[i] - zone && close <= eF[i] + zone * 0.45m;
        bool bullReject = close > open && (close - low) >= (high - low) * 0.42m;
        bool bearReject = close < open && (high - close) >= (high - low) * 0.42m;

        // Must hold 15m structure side of slow EMA
        if (bull && close < eS[i] * 0.996m)
            return Fail("m15_not_in_bull_value");
        if (bear && close > eS[i] * 1.004m)
            return Fail("m15_not_in_bear_value");

        // 15m impulse window ban
        if (!ImpulseOk(m15, bull, atr15, out var imp))
            return Fail(imp);

        if (bull)
        {
            // LONG only in discount (at or below equilibrium)
            if (close > eq * 1.005m)
                return Fail("long_not_in_discount");
            if (!touchLong || !bullReject)
                return Fail("no_m15_long_trigger");

            decimal sl = swingLo - atr15 * StructurePadAtr15;
            sl = Math.Min(sl, ema50); // structural floor preference
            // if ema50 is above entry that would be wrong — only use if below entry
            if (ema50 < close)
                sl = Math.Min(sl, ema50 - atr15 * 0.1m);

            decimal risk = close - sl;
            if (risk <= 0) return Fail("bad_risk");
            if (risk / close < MinRiskPct)
                sl = close - close * MinRiskPct;
            if (risk / close > MaxRiskPct)
                sl = close - close * MaxRiskPct;
            risk = close - sl;
            if (risk <= 0) return Fail("bad_risk2");

            var tps = Tps(true, close, risk);
            if ((tps[0] - close) / risk < MinRr * 0.99m)
                return Fail("rr_fail");

            decimal conf = Score(volR, true, (eq - close) / (swingHi - swingLo));
            if (conf < MinConfidence)
                return Fail($"conf_low:{conf:F2}");

            return Ok(Build(symbol, SignalSide.Buy, close, sl, tps, conf, "CORE_INST_LONG"));
        }
        else
        {
            if (close < eq * 0.995m)
                return Fail("short_not_in_premium");
            if (!touchShort || !bearReject)
                return Fail("no_m15_short_trigger");

            decimal sl = swingHi + atr15 * StructurePadAtr15;
            if (ema50 > close)
                sl = Math.Max(sl, ema50 + atr15 * 0.1m);

            decimal risk = sl - close;
            if (risk <= 0) return Fail("bad_risk");
            if (risk / close < MinRiskPct)
                sl = close + close * MinRiskPct;
            if (risk / close > MaxRiskPct)
                sl = close + close * MaxRiskPct;
            risk = sl - close;
            if (risk <= 0) return Fail("bad_risk2");

            var tps = Tps(false, close, risk);
            if ((close - tps[0]) / risk < MinRr * 0.99m)
                return Fail("rr_fail");

            decimal conf = Score(volR, true, (close - eq) / (swingHi - swingLo));
            if (conf < MinConfidence)
                return Fail($"conf_low:{conf:F2}");

            return Ok(Build(symbol, SignalSide.Sell, close, sl, tps, conf, "CORE_INST_SHORT"));
        }
    }

    private static Result Fail(string r) => new(false, null, r);
    private static Result Ok(TradeSignal s) => new(true, s, "ok");

    private static TradeSignal Build(
        string symbol, SignalSide side, decimal entry, decimal sl,
        decimal[] tps, decimal conf, string reason) => new()
    {
        Symbol = symbol,
        Side = side,
        EntryPrice = entry,
        StopLoss = sl,
        TakeProfits = tps.ToList(),
        Confidence = conf,
        Reason = reason,
        Timeframe = "FifteenMinutes",
        Time = DateTime.UtcNow
    };

    private static decimal[] Tps(bool isLong, decimal entry, decimal risk) => isLong
        ? new[] { entry + risk * 1.5m, entry + risk * 2.5m, entry + risk * 4.0m }
        : new[] { entry - risk * 1.5m, entry - risk * 2.5m, entry - risk * 4.0m };

    private static decimal Score(decimal volR, bool reject, decimal discountDepth)
    {
        decimal c = 0.55m;
        if (volR >= 1.0m) c += 0.05m;
        if (volR >= 1.2m) c += 0.04m;
        if (reject) c += 0.04m;
        if (discountDepth > 0.05m) c += 0.04m; // deeper in value
        return Math.Min(0.85m, c);
    }

    private static string? ParabolicBan(List<BinanceFuturesUsdtKline> h1, bool forLong)
    {
        int n = h1.Count;
        var win = h1.Skip(Math.Max(0, n - 24)).ToList();
        decimal hi = win.Max(x => x.HighPrice);
        decimal lo = win.Min(x => x.LowPrice);
        decimal span = hi - lo;
        if (span <= 0) return null;
        decimal close = h1[^1].ClosePrice;
        decimal pos = (close - lo) / span;
        if (forLong && pos >= 0.78m) return $"parabolic_top:{pos:F2}";
        if (!forLong && pos <= 0.22m) return $"parabolic_bot:{pos:F2}";

        var w12 = h1.Skip(Math.Max(0, n - 12)).ToList();
        decimal c0 = w12[0].OpenPrice;
        if (c0 <= 0) return null;
        decimal move = (w12[^1].ClosePrice - c0) / c0;
        if (forLong && move >= 0.10m)
        {
            decimal ih = w12.Max(x => x.HighPrice);
            decimal ret = ih > c0 ? (ih - close) / (ih - c0) : 0m;
            if (ret < 0.30m) return $"vertical_long:{move:P0}/ret{ret:F2}";
        }
        if (!forLong && move <= -0.10m)
        {
            decimal il = w12.Min(x => x.LowPrice);
            decimal ret = c0 > il ? (close - il) / (c0 - il) : 0m;
            if (ret < 0.30m) return $"vertical_short:{move:P0}/ret{ret:F2}";
        }
        return null;
    }

    private static bool ImpulseOk(
        List<BinanceFuturesUsdtKline> m15, bool forLong, decimal atr, out string reason)
    {
        reason = "";
        var win = m15.TakeLast(8).ToList();
        decimal hi = win.Max(x => x.HighPrice);
        decimal lo = win.Min(x => x.LowPrice);
        decimal span = hi - lo;
        if (span <= 0) return true;
        decimal px = m15[^1].ClosePrice;
        decimal pos = (px - lo) / span;
        bool impulse = win.Any(b => (b.HighPrice - b.LowPrice) >= atr * 1.2m);
        if (forLong && impulse && pos >= 0.85m)
        {
            reason = "m15_impulse_top";
            return false;
        }
        if (!forLong && impulse && pos <= 0.15m)
        {
            reason = "m15_impulse_bot";
            return false;
        }
        return true;
    }

    private sealed class Struct
    {
        public decimal LastSwingHigh, PrevSwingHigh, LastSwingLow, PrevSwingLow;
        public bool IsBullish => LastSwingHigh > PrevSwingHigh && LastSwingLow > PrevSwingLow;
        public bool IsBearish => LastSwingHigh < PrevSwingHigh && LastSwingLow < PrevSwingLow;
    }

    private static Struct? ReadStructure(List<BinanceFuturesUsdtKline> k)
    {
        var highs = Pivots(k, true);
        var lows = Pivots(k, false);
        if (highs.Count < 2 || lows.Count < 2) return null;
        return new Struct
        {
            LastSwingHigh = highs[^1],
            PrevSwingHigh = highs[^2],
            LastSwingLow = lows[^1],
            PrevSwingLow = lows[^2]
        };
    }

    private static List<decimal> Pivots(List<BinanceFuturesUsdtKline> k, bool high)
    {
        var list = new List<decimal>();
        const int w = 2;
        for (int i = w; i < k.Count - w; i++)
        {
            if (high)
            {
                decimal p = k[i].HighPrice;
                bool ok = true;
                for (int j = i - w; j <= i + w; j++)
                    if (j != i && k[j].HighPrice >= p) { ok = false; break; }
                if (ok) list.Add(p);
            }
            else
            {
                decimal p = k[i].LowPrice;
                bool ok = true;
                for (int j = i - w; j <= i + w; j++)
                    if (j != i && k[j].LowPrice <= p) { ok = false; break; }
                if (ok) list.Add(p);
            }
        }
        return list;
    }

    private static List<decimal> Ema(List<decimal> src, int period)
    {
        var r = new List<decimal>(src.Count);
        if (src.Count == 0) return r;
        decimal k = 2m / (period + 1);
        decimal ema = src[0];
        for (int i = 0; i < src.Count; i++)
        {
            ema = i == 0 ? src[0] : (src[i] - ema) * k + ema;
            r.Add(ema);
        }
        return r;
    }

    private static decimal Atr(List<BinanceFuturesUsdtKline> k, int period)
    {
        if (k.Count < period + 2) return 0;
        decimal sum = 0;
        for (int i = k.Count - period; i < k.Count; i++)
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

    private static decimal VolRatio(List<BinanceFuturesUsdtKline> k, int recent, int baseN)
    {
        if (k.Count < recent + baseN + 1) return 1m;
        int end = k.Count - 1;
        decimal sr = 0, sb = 0;
        for (int i = 0; i < recent; i++) sr += k[end - i].Volume;
        for (int i = 0; i < baseN; i++) sb += k[end - recent - i].Volume;
        if (sb <= 0) return 1m;
        return (sr / recent) / (sb / baseN);
    }

    private static bool IsMajor(string s)
    {
        s = s.ToUpperInvariant();
        return s.StartsWith("BTC") || s.StartsWith("ETH") || s.StartsWith("BNB")
            || s.StartsWith("SOL") || s.StartsWith("XRP");
    }
}
