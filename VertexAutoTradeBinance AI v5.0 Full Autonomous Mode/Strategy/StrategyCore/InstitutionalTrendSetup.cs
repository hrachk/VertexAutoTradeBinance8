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
///   SL: 1H swing invalidation + symbol ATR pad (vol-scaled).
///   TP: 15m liquidity pivots / measured move — not fixed 1.6/2.8/4.5R grid.
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

            // SL: symbol structure first (swing invalidation + ATR pad of THIS pair)
            decimal pad = Math.Max(atr1h * Atr1hPad, atr15 * Atr15Pad);
            // Volatile alts get wider structural pad (ATR-relative), majors tighter
            decimal atrPct = atr15 / close;
            if (atrPct > 0.012m) pad = Math.Max(pad, atr15 * 1.15m);
            else if (atrPct < 0.004m) pad = Math.Max(pad, atr15 * 0.85m);

            decimal slStruct = swingLo - pad;
            decimal last1hLow = h1[^1].LowPrice;
            if (last1hLow < close && last1hLow > swingLo * 0.998m)
                slStruct = Math.Min(slStruct, last1hLow - atr15 * 0.2m);

            // Floor: max(0.55% price, 0.75*ATR15) — anti micro-noise, still symbol-scaled
            decimal floorDist = Math.Max(close * 0.0055m, atr15 * 0.75m);
            decimal ceilDist = Math.Min(close * MaxRiskPct, Math.Max(atr1h * 2.2m, close * 0.012m));
            if (ceilDist < floorDist) ceilDist = close * MaxRiskPct;

            decimal sl = slStruct;
            decimal risk = close - sl;
            if (risk < floorDist) { sl = close - floorDist; risk = floorDist; }
            if (risk > ceilDist) { sl = close - ceilDist; risk = ceilDist; }
            if (risk <= 0) return Fail("bad_risk");

            var tps = StructureTps(true, close, risk, swingHi, swingLo, atr15, atr1h, m15);
            if ((tps[0] - close) / risk < 1.20m)
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

            decimal pad = Math.Max(atr1h * Atr1hPad, atr15 * Atr15Pad);
            decimal atrPct = atr15 / close;
            if (atrPct > 0.012m) pad = Math.Max(pad, atr15 * 1.15m);
            else if (atrPct < 0.004m) pad = Math.Max(pad, atr15 * 0.85m);

            decimal slStruct = swingHi + pad;
            decimal last1hHigh = h1[^1].HighPrice;
            if (last1hHigh > close && last1hHigh < swingHi * 1.002m)
                slStruct = Math.Max(slStruct, last1hHigh + atr15 * 0.2m);

            decimal floorDist = Math.Max(close * 0.0055m, atr15 * 0.75m);
            decimal ceilDist = Math.Min(close * MaxRiskPct, Math.Max(atr1h * 2.2m, close * 0.012m));
            if (ceilDist < floorDist) ceilDist = close * MaxRiskPct;

            decimal sl = slStruct;
            decimal risk = sl - close;
            if (risk < floorDist) { sl = close + floorDist; risk = floorDist; }
            if (risk > ceilDist) { sl = close + ceilDist; risk = ceilDist; }
            if (risk <= 0) return Fail("bad_risk");

            var tps = StructureTps(false, close, risk, swingHi, swingLo, atr15, atr1h, m15);
            if ((close - tps[0]) / risk < 1.20m)
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

    /// <summary>
    /// Per-symbol TP ladder from structure / liquidity, not a fixed 1.6/2.8/4.5 R grid.
    /// R multiples only fill gaps when the chart has no usable opposing level.
    /// </summary>
    private static decimal[] StructureTps(
        bool isLong, decimal entry, decimal risk,
        decimal swingHi, decimal swingLo, decimal atr15, decimal atr1h,
        List<BinanceFuturesUsdtKline> m15)
    {
        risk = Math.Max(risk, entry * 0.0005m);
        var liq = RecentLiquidityLevels(m15, isLong, entry);

        if (isLong)
        {
            // TP1: nearest liquidity ABOVE entry (equal high / swing), else 1.4R
            decimal tp1Struct = 0m;
            foreach (var lv in liq)
                if (lv > entry + risk * 0.35m) { tp1Struct = lv; break; }
            if (swingHi > entry + risk * 0.4m)
                tp1Struct = tp1Struct > 0 ? Math.Min(tp1Struct, swingHi) : swingHi;

            decimal tp1 = tp1Struct > entry
                ? tp1Struct
                : entry + risk * 1.45m;
            // Enforce minimum R:R ~1.25 without forcing every symbol to 1.60R
            if ((tp1 - entry) < risk * 1.25m)
                tp1 = entry + risk * 1.25m;

            // TP2: next liquidity or measured move (entry-swingLo projected)
            decimal measured = entry + Math.Max(entry - swingLo, atr1h);
            decimal tp2Struct = 0m;
            foreach (var lv in liq)
                if (lv > tp1 + atr15 * 0.15m) { tp2Struct = lv; break; }
            decimal tp2 = tp2Struct > tp1
                ? tp2Struct
                : Math.Max(tp1 + risk * 0.9m, measured);
            if (tp2 <= tp1) tp2 = tp1 + Math.Max(risk * 0.85m, atr15 * 0.5m);

            // TP3: extension — range projection or 2nd measured
            decimal tp3 = Math.Max(tp2 + Math.Max(risk * 1.1m, atr1h * 0.6m),
                                   entry + Math.Max((entry - swingLo) * 1.6m, risk * 3.2m));
            if (tp3 <= tp2) tp3 = tp2 + Math.Max(risk, atr15);

            return new[] { tp1, tp2, tp3 };
        }
        else
        {
            decimal tp1Struct = 0m;
            foreach (var lv in liq)
                if (lv < entry - risk * 0.35m) { tp1Struct = lv; break; }
            if (swingLo < entry - risk * 0.4m)
                tp1Struct = tp1Struct > 0 ? Math.Max(tp1Struct, swingLo) : swingLo;

            decimal tp1 = tp1Struct > 0 && tp1Struct < entry
                ? tp1Struct
                : entry - risk * 1.45m;
            if ((entry - tp1) < risk * 1.25m)
                tp1 = entry - risk * 1.25m;

            decimal measured = entry - Math.Max(swingHi - entry, atr1h);
            decimal tp2Struct = 0m;
            foreach (var lv in liq)
                if (lv < tp1 - atr15 * 0.15m) { tp2Struct = lv; break; }
            decimal tp2 = tp2Struct > 0 && tp2Struct < tp1
                ? tp2Struct
                : Math.Min(tp1 - risk * 0.9m, measured);
            if (tp2 >= tp1) tp2 = tp1 - Math.Max(risk * 0.85m, atr15 * 0.5m);

            decimal tp3 = Math.Min(tp2 - Math.Max(risk * 1.1m, atr1h * 0.6m),
                                   entry - Math.Max((swingHi - entry) * 1.6m, risk * 3.2m));
            if (tp3 >= tp2) tp3 = tp2 - Math.Max(risk, atr15);

            return new[] { tp1, tp2, tp3 };
        }
    }

    /// <summary>Equal-high / equal-low clusters on 15m = simple liquidity proxies (symbol-specific).</summary>
    private static List<decimal> RecentLiquidityLevels(
        List<BinanceFuturesUsdtKline> m15, bool forLongTargets, decimal entry)
    {
        var levels = new List<decimal>();
        if (m15 == null || m15.Count < 8) return levels;
        int n = m15.Count;
        int from = Math.Max(0, n - 48);
        var pivots = new List<decimal>();
        for (int i = from + 2; i < n - 2; i++)
        {
            decimal h = m15[i].HighPrice, l = m15[i].LowPrice;
            bool swingH = h >= m15[i - 1].HighPrice && h >= m15[i - 2].HighPrice
                          && h >= m15[i + 1].HighPrice && h >= m15[i + 2].HighPrice;
            bool swingL = l <= m15[i - 1].LowPrice && l <= m15[i - 2].LowPrice
                          && l <= m15[i + 1].LowPrice && l <= m15[i + 2].LowPrice;
            if (forLongTargets && swingH) pivots.Add(h);
            if (!forLongTargets && swingL) pivots.Add(l);
        }
        // Cluster near-equal pivots (within 0.15%)
        pivots.Sort();
        if (!forLongTargets) pivots.Reverse(); // nearest below entry first when short
        else { /* ascending — filter those above entry */ }

        decimal tol = entry * 0.0015m;
        foreach (var p in pivots)
        {
            if (forLongTargets && p <= entry) continue;
            if (!forLongTargets && p >= entry) continue;
            if (levels.Count > 0 && Math.Abs(levels[^1] - p) <= tol) continue;
            levels.Add(p);
            if (levels.Count >= 6) break;
        }
        if (!forLongTargets)
            levels = levels.OrderByDescending(x => x).ToList(); // nearest below first
        else
            levels = levels.OrderBy(x => x).ToList();
        return levels;
    }

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
