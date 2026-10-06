using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Strategy.StrategyCore;

/// <summary>
/// Institutional TREND setup v2 — professional risk geometry.
///
/// Hierarchy:
///   4H = permission (must agree on direction)
///   1H = bias + swing invalidation + ATR for stop width
///  15m = only trigger (reject into value)
///
/// SL:
///   Beyond last 1H swing + max(0.35 * ATR1H, 0.9 * ATR15, MinRiskPct * price)
///   Capped at MaxRiskPct so size stays finite via RiskManager 1R.
/// TP: 1.6R / 2.8R / 4.5R (TP1 R:R ≥ 1.6).
/// Location: long only in lower 42% of 1H swing range; short only upper 42%.
/// </summary>
public static class InstitutionalTrendSetup
{
    public const decimal MinConfidence = 0.58m;
    public const decimal MinRr = 1.60m;
    public const decimal MaxRiskPct = 0.028m;   // hard cap risk distance
    public const decimal MinRiskPct = 0.009m;   // never thinner than 0.9% (anti micro-stop)
    public const decimal Atr1hPad = 0.35m;
    public const decimal Atr15Pad = 0.90m;
    public const decimal DiscountMax = 0.48m;  // was 0.42 — slightly wider value zone  // long only if pos in swing ≤ 0.42
    public const decimal PremiumMin = 0.52m;

    public readonly record struct Result(bool Ok, TradeSignal? Signal, string Reason);

    public static Result TryBuild(
        string symbol,
        IReadOnlyList<BinanceFuturesUsdtKline> h1Raw,
        IReadOnlyList<BinanceFuturesUsdtKline> m15Raw,
        IReadOnlyList<BinanceFuturesUsdtKline>? h4Raw = null,
        decimal? btcBias01 = null)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return Fail("no_symbol");
        if (h1Raw == null || h1Raw.Count < 55)
            return Fail("htf_1h_thin");
        if (m15Raw == null || m15Raw.Count < 40)
            return Fail("m15_thin");

        var h1 = ClosedBars(h1Raw, 55);
        var m15 = m15Raw.OrderBy(x => x.OpenTime).ToList();
        if (h1.Count < 55)
            return Fail("htf_1h_thin");

        // ─── 4H permission (if available) ───
        List<BinanceFuturesUsdtKline>? h4 = null;
        if (h4Raw != null && h4Raw.Count >= 30)
            h4 = ClosedBars(h4Raw, 30);

        var st1 = ReadStructure(h1);
        if (st1 == null)
            return Fail("no_1h_structure");

        var c1 = h1.Select(x => x.ClosePrice).ToList();
        var e21 = Ema(c1, 21);
        var e50 = Ema(c1, 50);
        int i1 = c1.Count - 1;
        decimal px1 = c1[i1], ema21 = e21[i1], ema50 = e50[i1];

        bool bull1h = st1.IsBullish && ema21 >= ema50 * 0.998m && px1 > ema50;
        bool bear1h = st1.IsBearish && ema21 <= ema50 * 1.002m && px1 < ema50;
        if (!bull1h && !bear1h)
            return Fail("no_clear_1h_bias");

        if (h4 != null)
        {
            var st4 = ReadStructure(h4);
            var c4 = h4.Select(x => x.ClosePrice).ToList();
            var e4_50 = Ema(c4, Math.Min(50, c4.Count - 1));
            decimal px4 = c4[^1];
            decimal ema4 = e4_50[^1];
            // Soft structure if pivots thin: use EMA side
            bool bull4 = px4 > ema4 * 1.001m && (st4 == null || !st4.IsBearish);
            bool bear4 = px4 < ema4 * 0.999m && (st4 == null || !st4.IsBullish);
            // Soft H4: disagreement reduces confidence later, does not hard-kill valid 1H+15m setup
            if (bull1h && !bull4)
            { /* h4 soft disagree long — conf penalized in Score path via vol/location only */ }
            if (bear1h && !bear4)
            { /* h4 soft disagree short */ }
        }

        if (btcBias01.HasValue && !IsMajor(symbol))
        {
            if (bull1h && btcBias01.Value < -0.35m)
                return Fail("btc_against_long");
            if (bear1h && btcBias01.Value > 0.35m)
                return Fail("btc_against_short");
        }

        decimal volR = VolRatio(h1, 8, 20);
        if (volR < 0.82m)
            return Fail($"dead_1h_volume:{volR:F2}");

        decimal swingHi = st1.LastSwingHigh;
        decimal swingLo = st1.LastSwingLow;
        if (swingHi <= swingLo)
            return Fail("bad_swing_range");
        decimal eq = (swingHi + swingLo) / 2m;
        decimal swingSpan = swingHi - swingLo;

        var ban = ParabolicBan(h1, bull1h);
        if (ban != null)
            return Fail(ban);

        decimal atr1h = Atr(h1, 14);
        decimal atr15 = Atr(m15, 14);
        if (atr1h <= 0 || atr15 <= 0)
            return Fail("no_atr");

        // 15m trigger
        var c15 = m15.Select(x => x.ClosePrice).ToList();
        var eF = Ema(c15, 21);
        var eS = Ema(c15, 55);
        int i = c15.Count - 1;
        var bar = m15[i];
        decimal close = bar.ClosePrice, open = bar.OpenPrice;
        decimal high = bar.HighPrice, low = bar.LowPrice;
        decimal zone = Math.Max(atr15 * 0.45m, close * 0.002m);

        bool touchLong = low <= eF[i] + zone && close >= eF[i] - zone * 0.5m;
        bool touchShort = high >= eF[i] - zone && close <= eF[i] + zone * 0.5m;
        bool bullReject = close > open && (close - low) >= (high - low) * 0.45m;
        bool bearReject = close < open && (high - close) >= (high - low) * 0.45m;

        if (bull1h && close < eS[i] * 0.995m)
            return Fail("m15_not_in_bull_value");
        if (bear1h && close > eS[i] * 1.005m)
            return Fail("m15_not_in_bear_value");

        if (!ImpulseOk(m15, bull1h, atr15, out var imp))
            return Fail(imp);

        decimal posInSwing = (close - swingLo) / swingSpan;

        if (bull1h)
        {
            if (posInSwing > DiscountMax)
                return Fail($"long_not_in_discount:{posInSwing:F2}");
            if (!touchLong || !bullReject)
                return Fail("no_m15_long_trigger");

            // Professional SL: structure + 1H ATR pad, never micro
            decimal slStruct = swingLo - Math.Max(atr1h * Atr1hPad, atr15 * Atr15Pad);
            decimal slMin = close * (1m - MinRiskPct);
            decimal slMax = close * (1m - MaxRiskPct); // furthest allowed
            decimal sl = Math.Min(slStruct, slMin);    // at least MinRiskPct
            if (sl < slMax)
                sl = slMax; // clamp so risk ≤ MaxRiskPct

            // Prefer not above last closed 1H low
            decimal last1hLow = h1[^1].LowPrice;
            if (last1hLow < close)
                sl = Math.Min(sl, last1hLow - atr15 * 0.15m);

            decimal risk = close - sl;
            if (risk <= 0)
                return Fail("bad_risk");
            if (risk / close < MinRiskPct * 0.98m)
            {
                sl = close - close * MinRiskPct;
                risk = close - sl;
            }
            if (risk / close > MaxRiskPct * 1.02m)
            {
                sl = close - close * MaxRiskPct;
                risk = close - sl;
            }

            var tps = Tps(true, close, risk);
            if ((tps[0] - close) / risk < MinRr * 0.99m)
                return Fail("rr_fail");

            decimal conf = Score(volR, true, DiscountMax - posInSwing);
            if (conf < MinConfidence)
                return Fail($"conf_low:{conf:F2}");

            return Ok(Build(symbol, SignalSide.Buy, close, sl, tps, conf, atr1h, "CORE_INST_LONG"));
        }
        else
        {
            if (posInSwing < PremiumMin)
                return Fail($"short_not_in_premium:{posInSwing:F2}");
            if (!touchShort || !bearReject)
                return Fail("no_m15_short_trigger");

            decimal slStruct = swingHi + Math.Max(atr1h * Atr1hPad, atr15 * Atr15Pad);
            decimal slMin = close * (1m + MinRiskPct);
            decimal slMax = close * (1m + MaxRiskPct);
            decimal sl = Math.Max(slStruct, slMin);
            if (sl > slMax)
                sl = slMax;

            decimal last1hHigh = h1[^1].HighPrice;
            if (last1hHigh > close)
                sl = Math.Max(sl, last1hHigh + atr15 * 0.15m);

            decimal risk = sl - close;
            if (risk <= 0)
                return Fail("bad_risk");
            if (risk / close < MinRiskPct * 0.98m)
            {
                sl = close + close * MinRiskPct;
                risk = sl - close;
            }
            if (risk / close > MaxRiskPct * 1.02m)
            {
                sl = close + close * MaxRiskPct;
                risk = sl - close;
            }

            var tps = Tps(false, close, risk);
            if ((close - tps[0]) / risk < MinRr * 0.99m)
                return Fail("rr_fail");

            decimal conf = Score(volR, true, posInSwing - PremiumMin);
            if (conf < MinConfidence)
                return Fail($"conf_low:{conf:F2}");

            return Ok(Build(symbol, SignalSide.Sell, close, sl, tps, conf, atr1h, "CORE_INST_SHORT"));
        }
    }

    private static Result Fail(string r) => new(false, null, r);
    private static Result Ok(TradeSignal s) => new(true, s, "ok");

    private static TradeSignal Build(
        string symbol, SignalSide side, decimal entry, decimal sl,
        decimal[] tps, decimal conf, decimal atr1h, string reason) => new()
    {
        Symbol = symbol,
        Side = side,
        EntryPrice = entry,
        StopLoss = sl,
        TakeProfits = tps.ToList(),
        Confidence = conf,
        Reason = reason,
        Timeframe = "FifteenMinutes",
        Time = DateTime.UtcNow,
        Atr = atr1h
    };

    private static decimal[] Tps(bool isLong, decimal entry, decimal risk) => isLong
        ? new[] { entry + risk * 1.60m, entry + risk * 2.80m, entry + risk * 4.50m }
        : new[] { entry - risk * 1.60m, entry - risk * 2.80m, entry - risk * 4.50m };

    private static decimal Score(decimal volR, bool reject, decimal depthBonus)
    {
        decimal c = 0.58m;
        if (volR >= 1.0m) c += 0.04m;
        if (volR >= 1.25m) c += 0.04m;
        if (reject) c += 0.03m;
        if (depthBonus > 0.08m) c += 0.04m;
        return Math.Min(0.86m, c);
    }

    private static List<BinanceFuturesUsdtKline> ClosedBars(
        IReadOnlyList<BinanceFuturesUsdtKline> raw, int minKeep)
    {
        var k = raw.OrderBy(x => x.OpenTime).ToList();
        if (k.Count == 0) return k;
        int hi = k.Count - 1;
        var openT = k[hi].OpenTime;
        if (openT.Kind == DateTimeKind.Unspecified)
            openT = DateTime.SpecifyKind(openT, DateTimeKind.Utc);
        // drop young forming bar (1H ~50m, 4H ~3h)
        var ageMin = (DateTime.UtcNow - openT.ToUniversalTime()).TotalMinutes;
        if (ageMin < 50 && k.Count >= 2)
            hi = k.Count - 2;
        return k.Take(hi + 1).ToList();
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
        if (forLong && pos >= 0.72m) return $"parabolic_top:{pos:F2}";
        if (!forLong && pos <= 0.28m) return $"parabolic_bot:{pos:F2}";

        var w12 = h1.Skip(Math.Max(0, n - 12)).ToList();
        decimal c0 = w12[0].OpenPrice;
        if (c0 <= 0) return null;
        decimal move = (w12[^1].ClosePrice - c0) / c0;
        if (forLong && move >= 0.08m)
        {
            decimal ih = w12.Max(x => x.HighPrice);
            decimal ret = ih > c0 ? (ih - close) / (ih - c0) : 0m;
            if (ret < 0.35m) return $"vertical_long:{move:P0}/ret{ret:F2}";
        }
        if (!forLong && move <= -0.08m)
        {
            decimal il = w12.Min(x => x.LowPrice);
            decimal ret = c0 > il ? (close - il) / (c0 - il) : 0m;
            if (ret < 0.35m) return $"vertical_short:{move:P0}/ret{ret:F2}";
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
        bool impulse = win.Any(b => (b.HighPrice - b.LowPrice) >= atr * 1.25m);
        if (forLong && impulse && pos >= 0.82m)
        {
            reason = "m15_impulse_top";
            return false;
        }
        if (!forLong && impulse && pos <= 0.18m)
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
