using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Strategy.StrategyCore;

/// <summary>
/// Institutional TREND setup v3 — coherent pullback, not contradictory filters.
///
/// Hierarchy:
///   4H = permission (hard-block alts only when structure AND EMA disagree)
///   1H = bias (EMA stack + higher-low / lower-high) and swing invalidation
///  15m = trigger only (EMA21 touch + reclaim inside the last 4 closed bars)
///
/// Location is a retrace of the last 1H impulse (about 15–72%), NOT "price must
/// sit in the bottom 42% of the swing" while also requiring an uptrend.
/// That pair of rules cannot be true at the same time, so the engine emitted nothing.
///
/// SL is the swing invalidation ± ATR pad.
///   risk < 0.9%  → widen away from noise (never a micro-stop)
///   risk > 2.8%  → skip. Never pull the stop inside the swing.
/// </summary>
public static class InstitutionalTrendSetup
{
    public const decimal MinConfidence = 0.56m;
    public const decimal MinRr = 1.35m;
    public const decimal MaxRiskPct = 0.028m;
    public const decimal MinRiskPct = 0.009m;
    public const decimal Atr1hPad = 0.35m;
    public const decimal Atr15Pad = 0.90m;
    public const decimal MinRetrace = 0.15m;
    public const decimal MaxRetrace = 0.72m;

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

        var h1 = ClosedBars(h1Raw, barMinutes: 60);
        var m15 = m15Raw.OrderBy(x => x.OpenTime).ToList();
        if (h1.Count < 55)
            return Fail("htf_1h_thin");

        List<BinanceFuturesUsdtKline>? h4 = null;
        if (h4Raw != null && h4Raw.Count >= 30)
            h4 = ClosedBars(h4Raw, barMinutes: 240);

        var st1 = ReadStructure(h1);
        if (st1 == null)
            return Fail("no_1h_structure");

        var c1 = h1.Select(x => x.ClosePrice).ToList();
        var e21 = Ema(c1, 21);
        var e50 = Ema(c1, 50);
        int i1 = c1.Count - 1;
        decimal px1 = c1[i1], ema21 = e21[i1], ema50 = e50[i1];

        bool higherLows = st1.LastSwingLow >= st1.PrevSwingLow * 0.9975m;
        bool higherHighs = st1.LastSwingHigh >= st1.PrevSwingHigh * 0.9975m;
        bool lowerHighs = st1.LastSwingHigh <= st1.PrevSwingHigh * 1.0025m;
        bool lowerLows = st1.LastSwingLow <= st1.PrevSwingLow * 1.0025m;

        // Uptrend = EMA stack + defended higher low. A fresh HH is a bonus, not a requirement:
        // the trade IS the pullback before the next high.
        bool upTrend = ema21 >= ema50 * 0.997m
                       && higherLows
                       && px1 > st1.LastSwingLow * 1.001m
                       && px1 >= ema50 * 0.988m
                       && !(st1.IsBearish && px1 < ema21);
        bool downTrend = ema21 <= ema50 * 1.003m
                         && lowerHighs
                         && px1 < st1.LastSwingHigh * 0.999m
                         && px1 <= ema50 * 1.012m
                         && !(st1.IsBullish && px1 > ema21);
        if (upTrend && downTrend)
        {
            if (ema21 >= ema50) downTrend = false;
            else upTrend = false;
        }
        if (!upTrend && !downTrend)
            return Fail("no_clear_1h_bias");

        bool fullStructure = upTrend ? (higherHighs && higherLows) : (lowerHighs && lowerLows);

        if (btcBias01.HasValue && !IsMajor(symbol))
        {
            if (upTrend && btcBias01.Value < -0.50m)
                return Fail("btc_against_long");
            if (downTrend && btcBias01.Value > 0.50m)
                return Fail("btc_against_short");
        }

        // Pullbacks are quiet. 0.82 killed the exact bars we want. 0.55 still rejects a dead tape.
        decimal volR = VolRatio(h1, 8, 20);
        if (volR < 0.55m)
            return Fail($"dead_1h_volume:{volR:F2}");

        decimal swingHi = st1.LastSwingHigh;
        decimal swingLo = st1.LastSwingLow;
        if (swingHi <= swingLo || swingLo <= 0)
            return Fail("bad_swing_range");
        decimal swingSpan = swingHi - swingLo;

        decimal atr1h = Atr(h1, 14);
        decimal atr15 = Atr(m15, 14);
        if (atr1h <= 0 || atr15 <= 0)
            return Fail("no_atr");

        var c15 = m15.Select(x => x.ClosePrice).ToList();
        var eF = Ema(c15, 21);
        var eS = Ema(c15, 55);
        int i = c15.Count - 1;
        var bar = m15[i];
        decimal close = bar.ClosePrice;
        if (close <= 0)
            return Fail("bad_price");

        decimal posInSwing = (close - swingLo) / swingSpan;
        decimal retrace = upTrend ? 1m - posInSwing : posInSwing;

        var chase = ChaseBan(h1, upTrend, ema21, atr1h, retrace);
        if (chase != null)
            return Fail(chase);

        bool h4Hard = H4Against(upTrend, h4, out decimal h4Pen);
        if (h4Hard && !IsMajor(symbol))
            return Fail(upTrend ? "h4_against_long" : "h4_against_short");

        if (!ImpulseOk(m15, upTrend, atr15, out var imp))
            return Fail(imp);

        if (upTrend)
        {
            if (retrace < MinRetrace)
                return Fail($"long_chase:{retrace:F2}");
            if (retrace > MaxRetrace)
                return Fail($"long_too_deep:{retrace:F2}");
            if (close < eS[i] - atr15 * 1.6m)
                return Fail("m15_not_in_bull_value");
            if (!Trigger(m15, eF, atr15, forLong: true, out bool strongReject))
                return Fail("no_m15_long_trigger");

            if (!TryStop(true, close, swingLo, atr1h, atr15, out decimal sl, out decimal risk, out string slWhy))
                return Fail(slWhy);

            var tps = StructureTps(true, close, risk, swingHi, swingLo, atr15, atr1h, m15);
            decimal rr = (tps[0] - close) / risk;
            if (rr < MinRr)
                return Fail("rr_fail");

            decimal conf = Score(volR, strongReject, retrace, fullStructure) - h4Pen;
            if (conf < MinConfidence)
                return Fail($"conf_low:{conf:F2}");

            return Ok(Build(symbol, SignalSide.Buy, close, sl, tps, conf, atr1h, rr, "CORE_INST_LONG"));
        }

        if (retrace < MinRetrace)
            return Fail($"short_chase:{retrace:F2}");
        if (retrace > MaxRetrace)
            return Fail($"short_too_deep:{retrace:F2}");
        if (close > eS[i] + atr15 * 1.6m)
            return Fail("m15_not_in_bear_value");
        if (!Trigger(m15, eF, atr15, forLong: false, out bool strongRejectS))
            return Fail("no_m15_short_trigger");

        if (!TryStop(false, close, swingHi, atr1h, atr15, out decimal slS, out decimal riskS, out string slWhyS))
            return Fail(slWhyS);

        var tpsS = StructureTps(false, close, riskS, swingHi, swingLo, atr15, atr1h, m15);
        decimal rrS = (close - tpsS[0]) / riskS;
        if (rrS < MinRr)
            return Fail("rr_fail");

        decimal confS = Score(volR, strongRejectS, retrace, fullStructure) - h4Pen;
        if (confS < MinConfidence)
            return Fail($"conf_low:{confS:F2}");

        return Ok(Build(symbol, SignalSide.Sell, close, slS, tpsS, confS, atr1h, rrS, "CORE_INST_SHORT"));
    }

    /// <summary>
    /// Structural stop. Widen to the 0.9% noise floor. Never tighten inside the swing.
    /// </summary>
    private static bool TryStop(
        bool isLong, decimal close, decimal swingInvalidation,
        decimal atr1h, decimal atr15,
        out decimal sl, out decimal risk, out string why)
    {
        sl = 0;
        risk = 0;
        why = "";
        decimal pad = Math.Max(atr1h * Atr1hPad, atr15 * Atr15Pad);
        decimal atrPct = atr15 / close;
        if (atrPct > 0.012m) pad = Math.Max(pad, atr15 * 1.15m);

        decimal slStruct = isLong ? swingInvalidation - pad : swingInvalidation + pad;
        decimal structRisk = isLong ? close - slStruct : slStruct - close;
        if (structRisk <= 0)
        {
            why = "bad_risk";
            return false;
        }

        decimal minDist = Math.Max(close * MinRiskPct, atr15 * 0.80m);
        decimal maxDist = close * MaxRiskPct;
        if (minDist > maxDist)
        {
            why = "sl_band_impossible";
            return false;
        }

        // Invalidation farther than the hard cap: skip. Clamping the stop closer
        // puts it inside the swing and is how "SL_STRATEGY_FAIL" gets flooded.
        if (structRisk > maxDist)
        {
            why = $"sl_too_wide:{structRisk / close:P2}";
            return false;
        }

        if (structRisk < minDist)
        {
            sl = isLong ? close - minDist : close + minDist;
            risk = minDist;
            return true;
        }

        sl = slStruct;
        risk = structRisk;
        return true;
    }

    /// <summary>
    /// 15m trigger over the last 4 closed bars: a touch of EMA21 plus either a
    /// rejection wick or a bullish/bearish reclaim. One perfect candle is not required.
    /// </summary>
    private static bool Trigger(
        List<BinanceFuturesUsdtKline> m15, List<decimal> emaFast, decimal atr15,
        bool forLong, out bool strongReject)
    {
        strongReject = false;
        int i = m15.Count - 1;
        if (i < 4 || emaFast.Count != m15.Count) return false;
        int from = i - 3;
        bool touched = false;
        for (int j = from; j <= i; j++)
        {
            var b = m15[j];
            decimal zone = Math.Max(atr15 * 0.55m, b.ClosePrice * 0.0022m);
            decimal rng = b.HighPrice - b.LowPrice;
            if (forLong)
            {
                if (b.LowPrice <= emaFast[j] + zone && b.ClosePrice >= emaFast[j] - zone)
                    touched = true;
                if (rng > 0 && b.ClosePrice > b.OpenPrice
                    && (b.ClosePrice - b.LowPrice) >= rng * 0.55m
                    && b.LowPrice <= emaFast[j] + zone)
                    strongReject = true;
            }
            else
            {
                if (b.HighPrice >= emaFast[j] - zone && b.ClosePrice <= emaFast[j] + zone)
                    touched = true;
                if (rng > 0 && b.ClosePrice < b.OpenPrice
                    && (b.HighPrice - b.ClosePrice) >= rng * 0.55m
                    && b.HighPrice >= emaFast[j] - zone)
                    strongReject = true;
            }
        }

        var cur = m15[i];
        bool reclaim = forLong
            ? cur.ClosePrice >= emaFast[i] && cur.ClosePrice > cur.OpenPrice
            : cur.ClosePrice <= emaFast[i] && cur.ClosePrice < cur.OpenPrice;
        return touched && (strongReject || reclaim);
    }

    private static bool H4Against(bool forLong, List<BinanceFuturesUsdtKline>? h4, out decimal penalty)
    {
        penalty = 0m;
        if (h4 == null || h4.Count < 20) return false;
        var st = ReadStructure(h4);
        var c = h4.Select(x => x.ClosePrice).ToList();
        int period = Math.Min(50, Math.Max(8, c.Count / 2));
        var ema = Ema(c, period);
        decimal px = c[^1];
        decimal e = ema[^1];
        if (e <= 0) return false;

        if (forLong)
        {
            bool wrongSide = px < e * 0.997m;
            bool bearStruct = st != null && st.IsBearish && !st.IsBullish;
            if (wrongSide && bearStruct) { penalty = 0.08m; return true; }
            if (wrongSide || bearStruct) penalty = 0.04m;
            return false;
        }

        bool wrongShort = px > e * 1.003m;
        bool bullStruct = st != null && st.IsBullish && !st.IsBearish;
        if (wrongShort && bullStruct) { penalty = 0.08m; return true; }
        if (wrongShort || bullStruct) penalty = 0.04m;
        return false;
    }

    private static Result Fail(string r) => new(false, null, r);
    private static Result Ok(TradeSignal s) => new(true, s, "ok");

    private static TradeSignal Build(
        string symbol, SignalSide side, decimal entry, decimal sl,
        decimal[] tps, decimal conf, decimal atr1h, decimal rr, string reason) => new()
    {
        Symbol = symbol,
        Side = side,
        EntryPrice = entry,
        StopLoss = sl,
        TakeProfits = tps.ToList(),
        Confidence = Math.Clamp(conf, 0m, 0.90m),
        Reason = reason,
        Setup = reason,
        Timeframe = "FifteenMinutes",
        Time = DateTime.UtcNow,
        Atr = atr1h,
        RRatio = rr
    };

    /// <summary>
    /// Per-symbol TP ladder from structure / liquidity, not a fixed R grid.
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
            decimal tp1Struct = 0m;
            foreach (var lv in liq)
                if (lv > entry + risk * 0.35m) { tp1Struct = lv; break; }
            if (swingHi > entry + risk * 0.4m)
                tp1Struct = tp1Struct > 0 ? Math.Min(tp1Struct, swingHi) : swingHi;

            decimal tp1 = tp1Struct > entry
                ? tp1Struct
                : entry + risk * MinRr;
            if ((tp1 - entry) < risk * MinRr)
                tp1 = entry + risk * MinRr;

            decimal measured = entry + Math.Max(entry - swingLo, atr1h);
            decimal tp2Struct = 0m;
            foreach (var lv in liq)
                if (lv > tp1 + atr15 * 0.15m) { tp2Struct = lv; break; }
            decimal tp2 = tp2Struct > tp1
                ? tp2Struct
                : Math.Max(tp1 + risk * 0.9m, measured);
            if (tp2 <= tp1) tp2 = tp1 + Math.Max(risk * 0.85m, atr15 * 0.5m);

            decimal tp3 = Math.Max(tp2 + Math.Max(risk * 1.1m, atr1h * 0.6m),
                                   entry + Math.Max((entry - swingLo) * 1.6m, risk * 3.2m));
            if (tp3 <= tp2) tp3 = tp2 + Math.Max(risk, atr15);

            return new[] { tp1, tp2, tp3 };
        }

        decimal tp1StructS = 0m;
        foreach (var lv in liq)
            if (lv < entry - risk * 0.35m) { tp1StructS = lv; break; }
        if (swingLo < entry - risk * 0.4m)
            tp1StructS = tp1StructS > 0 ? Math.Max(tp1StructS, swingLo) : swingLo;

        decimal tp1s = tp1StructS > 0 && tp1StructS < entry
            ? tp1StructS
            : entry - risk * MinRr;
        if ((entry - tp1s) < risk * MinRr)
            tp1s = entry - risk * MinRr;

        decimal measuredS = entry - Math.Max(swingHi - entry, atr1h);
        decimal tp2StructS = 0m;
        foreach (var lv in liq)
            if (lv < tp1s - atr15 * 0.15m) { tp2StructS = lv; break; }
        decimal tp2s = tp2StructS > 0 && tp2StructS < tp1s
            ? tp2StructS
            : Math.Min(tp1s - risk * 0.9m, measuredS);
        if (tp2s >= tp1s) tp2s = tp1s - Math.Max(risk * 0.85m, atr15 * 0.5m);

        decimal tp3s = Math.Min(tp2s - Math.Max(risk * 1.1m, atr1h * 0.6m),
                               entry - Math.Max((swingHi - entry) * 1.6m, risk * 3.2m));
        if (tp3s >= tp2s) tp3s = tp2s - Math.Max(risk, atr15);

        return new[] { tp1s, tp2s, tp3s };
    }

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
        pivots.Sort();
        if (!forLongTargets) pivots.Reverse();

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
            levels = levels.OrderByDescending(x => x).ToList();
        else
            levels = levels.OrderBy(x => x).ToList();
        return levels;
    }

    private static decimal Score(decimal volR, bool reject, decimal retrace, bool fullStructure)
    {
        decimal c = fullStructure ? 0.64m : 0.58m;
        if (volR >= 0.90m) c += 0.03m;
        if (volR >= 1.20m) c += 0.03m;
        if (reject) c += 0.04m;
        if (retrace >= 0.28m && retrace <= 0.58m) c += 0.04m;
        return Math.Min(0.88m, c);
    }

    private static List<BinanceFuturesUsdtKline> ClosedBars(
        IReadOnlyList<BinanceFuturesUsdtKline> raw, int barMinutes)
    {
        var k = raw.OrderBy(x => x.OpenTime).ToList();
        if (k.Count == 0) return k;
        int hi = k.Count - 1;
        var openT = k[hi].OpenTime;
        if (openT.Kind == DateTimeKind.Unspecified)
            openT = DateTime.SpecifyKind(openT, DateTimeKind.Utc);
        var ageMin = (DateTime.UtcNow - openT.ToUniversalTime()).TotalMinutes;
        // Drop the candle that has not yet closed (1H = 60, 4H = 240). The old
        // 50-minute cutoff kept a 4H bar that was still three hours from close.
        if (ageMin < barMinutes - 1 && k.Count >= 2)
            hi = k.Count - 2;
        return k.Take(hi + 1).ToList();
    }

    /// <summary>
    /// Chase = extended from the 1H EMA without a real retrace.
    /// Being in the upper part of a 24h range is what an uptrend looks like — that is not a ban.
    /// </summary>
    private static string? ChaseBan(
        List<BinanceFuturesUsdtKline> h1, bool forLong, decimal ema21, decimal atr, decimal retrace)
    {
        if (atr <= 0) return null;
        decimal close = h1[^1].ClosePrice;
        decimal ext = forLong ? (close - ema21) / atr : (ema21 - close) / atr;
        if (ext > 2.2m && retrace < MinRetrace)
            return forLong ? $"extended_long:{ext:F1}atr" : $"extended_short:{ext:F1}atr";

        int n = h1.Count;
        var w12 = h1.Skip(Math.Max(0, n - 12)).ToList();
        decimal c0 = w12[0].OpenPrice;
        if (c0 <= 0) return null;
        decimal move = (w12[^1].ClosePrice - c0) / c0;
        if (forLong && move >= 0.10m && retrace < 0.22m)
            return $"vertical_long:{move:P0}";
        if (!forLong && move <= -0.10m && retrace < 0.22m)
            return $"vertical_short:{move:P0}";
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
        if (forLong && impulse && pos >= 0.88m)
        {
            reason = "m15_impulse_top";
            return false;
        }
        if (!forLong && impulse && pos <= 0.12m)
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
                    if (j != i && k[j].HighPrice > p) { ok = false; break; }
                if (ok) list.Add(p);
            }
            else
            {
                decimal p = k[i].LowPrice;
                bool ok = true;
                for (int j = i - w; j <= i + w; j++)
                    if (j != i && k[j].LowPrice < p) { ok = false; break; }
                if (ok) list.Add(p);
            }
        }
        return list;
    }

    private static List<decimal> Ema(List<decimal> src, int period)
    {
        var r = new List<decimal>(src.Count);
        if (src.Count == 0 || period < 1) return r;
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
