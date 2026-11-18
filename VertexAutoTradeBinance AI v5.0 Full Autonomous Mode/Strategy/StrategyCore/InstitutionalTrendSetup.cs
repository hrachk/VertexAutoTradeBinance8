using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;
using VertexAutoTrade.Core.Risk;

namespace VertexAutoTradeBinance8.Strategy.StrategyCore;

/// <summary>
/// Institutional TREND setup v3 — coherent pullback, not contradictory filters.
///
/// Hierarchy:
///   4H close must still be on the trade side of EMA21.
///   1H direction is EMA21 vs EMA50, close vs EMA21, and the slope.
///   A bearish swing (lower high and lower low) cancels a long even
///   while EMA21 is still above EMA50 — that lag was the inverted entry.
///  15m stack must agree, and the signal bar must close back through EMA21.
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
    public const decimal MinConfidence = 0.42m;
    public const decimal MinRr = 1.15m;
    public const decimal MaxRiskPct = 0.032m;
    public const decimal MinRiskPct = 0.007m;
    public const decimal Atr1hPad = 0.30m;
    public const decimal Atr15Pad = 0.80m;
    public const decimal MinRetrace = 0.18m;
    public const decimal MaxRetrace = 0.88m;

    public readonly record struct Result(bool Ok, TradeSignal? Signal, string Reason);

    public static Result TryBuild(
        string symbol,
        IReadOnlyList<BinanceFuturesUsdtKline> h1Raw,
        IReadOnlyList<BinanceFuturesUsdtKline> m15Raw,
        IReadOnlyList<BinanceFuturesUsdtKline>? h4Raw = null,
        decimal? btcBias01 = null,
        IReadOnlyList<BinanceFuturesUsdtKline>? d1Raw = null)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return Fail("no_symbol");
        if (h1Raw == null || h1Raw.Count < 55)
            return Fail("htf_1h_thin");
        if (m15Raw == null || m15Raw.Count < 40)
            return Fail("m15_thin");

        var h1 = ClosedBars(h1Raw, barMinutes: 60);
        var m15 = ClosedBars(m15Raw, barMinutes: 15);
        if (h1.Count < 55)
            return Fail("htf_1h_thin");
        if (m15.Count < 40)
            return Fail("m15_thin");

        List<BinanceFuturesUsdtKline>? h4 = null;
        if (h4Raw != null && h4Raw.Count >= 30)
            h4 = ClosedBars(h4Raw, barMinutes: 240);

        var st1 = ReadStructure(h1);

        var c1 = h1.Select(x => x.ClosePrice).ToList();
        var e21 = Ema(c1, 21);
        var e50 = Ema(c1, 50);
        int i1 = c1.Count - 1;
        decimal px1 = c1[i1], ema21 = e21[i1], ema50 = e50[i1];
        decimal ema21Prev = e21[Math.Max(0, i1 - 3)];
        bool structBear = st1 != null && st1.IsBearish && !st1.IsBullish;
        bool structBull = st1 != null && st1.IsBullish && !st1.IsBearish;

        // Bias is the trend, not the location of the last close.
        // Requiring close > EMA21 AND a rising EMA21 made a pullback
        // (the only entry we want) fail as no_clear_1h_bias, so scans
        // emitted nothing for hours. A long may sit under EMA21 as long
        // as EMA50, the trend anchor, is not lost.
        bool stackedLong = ema21 > ema50;
        bool stackedShort = ema21 < ema50;
        decimal cushion = Math.Max(px1 * 0.004m, Math.Abs(px1 - ema21));
        bool upTrend = stackedLong && px1 >= ema50 - cushion * 0.35m;
        bool downTrend = stackedShort && px1 <= ema50 + cushion * 0.35m;

        // A real rollover: structure flipped AND price lost the anchor.
        // Two noisy pivots while price is still above EMA50 are not a short.
        if (upTrend && structBear && px1 < ema50)
            return Fail("ema_lag_downtrend");
        if (downTrend && structBull && px1 > ema50)
            return Fail("ema_lag_uptrend");

        if (upTrend && downTrend)
        {
            if (ema21 >= ema50) downTrend = false;
            else upTrend = false;
        }
        if (!upTrend && !downTrend)
            return Fail("no_clear_1h_bias");

        decimal softPenalty = 0m;
        if (upTrend && structBear) softPenalty += 0.08m;
        if (downTrend && structBull) softPenalty += 0.08m;
        if (stackedLong && ema21 < ema21Prev) softPenalty += 0.04m;
        if (stackedShort && ema21 > ema21Prev) softPenalty += 0.04m;

        // Soft: rising/falling closes are a penalty, not a hard block (crypto is noisy)
        if (c1.Count >= 9)
        {
            bool rising = c1[^1] > c1[^4] && c1[^4] >= c1[^8];
            bool falling = c1[^1] < c1[^4] && c1[^4] <= c1[^8];
            if (downTrend && rising) softPenalty += 0.12m;
            if (upTrend && falling) softPenalty += 0.12m;
        }

        if (d1Raw == null || d1Raw.Count < 30)
            return Fail("no_daily");
        {
            var d1 = ClosedBars(d1Raw, barMinutes: 1440);
            if (d1.Count < 25)
                return Fail("no_daily");
            var cd = d1.Select(x => x.ClosePrice).ToList();
            var ed = Ema(cd, 21);
            if (ed[^1] <= 0)
                return Fail("no_daily");
            // Daily EMA21 against is a soft penalty (lag is common on crypto)
            if (upTrend && cd[^1] < ed[^1]) softPenalty += 0.18m;
            if (downTrend && cd[^1] > ed[^1]) softPenalty += 0.18m;
        }

        bool higherLows = st1 != null && st1.LastSwingLow >= st1.PrevSwingLow * 0.995m;
        bool higherHighs = st1 != null && st1.LastSwingHigh >= st1.PrevSwingHigh * 0.995m;
        bool lowerHighs = st1 != null && st1.LastSwingHigh <= st1.PrevSwingHigh * 1.005m;
        bool lowerLows = st1 != null && st1.LastSwingLow <= st1.PrevSwingLow * 1.005m;
        bool fullStructure = upTrend ? (higherHighs && higherLows) : (lowerHighs && lowerLows);

        if (btcBias01.HasValue && !symbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase))
        {
            // BTC bias against is soft — reduces size/conf, does not kill the setup.
            // The hard BTC reversal pause is applied later, once the swing is known.
            if (btcBias01.Value > -1.5m && btcBias01.Value < 1.5m)
            {
                if (upTrend && btcBias01.Value < 0m) softPenalty += 0.10m;
                if (downTrend && btcBias01.Value > 0m) softPenalty += 0.10m;
            }
        }

        decimal volR = VolRatio(h1, 8, 20);
        if (volR < 0.25m)
            return Fail($"dead_1h_volume:{volR:F2}");
        if (volR < 0.45m) softPenalty += 0.08m;

        decimal swingHi;
        decimal swingLo;
        if (st1 != null)
        {
            swingHi = st1.LastSwingHigh;
            swingLo = st1.LastSwingLow;
        }
        else
        {
            var w = h1.TakeLast(16).ToList();
            swingHi = w.Max(x => x.HighPrice);
            swingLo = w.Min(x => x.LowPrice);
        }
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

        // 15m stack against the trade is a flip only when price is also
        // through the slow average. A pullback often dips EMA21 under EMA55
        // for a few bars; killing that killed the entry.
        if (upTrend && eF[i] < eS[i] && close < eS[i])
            return Fail("m15_stack_against_long");
        if (downTrend && eF[i] > eS[i] && close > eS[i])
            return Fail("m15_stack_against_short");
        if (upTrend && eF[i] < eS[i]) softPenalty += 0.06m;
        if (downTrend && eF[i] > eS[i]) softPenalty += 0.06m;

        if (h4 != null && h4.Count >= 30)
        {
            var c4 = h4.Select(x => x.ClosePrice).ToList();
            var e4 = Ema(c4, 21);
            decimal px4 = c4[^1], ema4 = e4[^1];
            if (ema4 > 0)
            {
                // 0.3% through a 4H average is noise. Only a real break is a veto.
                if (upTrend && px4 < ema4 * 0.985m)
                    return Fail("h4_below_ema");
                if (downTrend && px4 > ema4 * 1.015m)
                    return Fail("h4_above_ema");
                if (upTrend && px4 < ema4) softPenalty += 0.06m;
                if (downTrend && px4 > ema4) softPenalty += 0.06m;
            }
        }

        if (!TrendConfirmed(h1, e21, e50, upTrend, out var trendWhy))
            return Fail(trendWhy);

        decimal posInSwing = (close - swingLo) / swingSpan;
        decimal retrace = upTrend ? 1m - posInSwing : posInSwing;

        // A pullback that has already reclaimed the middle of the last swing,
        // or traded through the last pivot, is a change of character.
        // EMAs lag that turn by days. Pause. Do not flip to the other side.
        var pause = ReversalPause(h1, st1, upTrend, close, swingHi, swingLo, volR);
        if (pause != null)
            return Fail(pause);

        var sessionBlock = SessionFade(h1, st1, upTrend, m15[^1].OpenTime, retrace, volR);
        if (sessionBlock != null)
            return Fail(sessionBlock);

        if (btcBias01.HasValue && !symbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase))
        {
            // 2 / -2 is a BTC reversal pause, not a normal trend bias.
            if (downTrend && btcBias01.Value >= 2m)
                return Fail("btc_reversal_pause");
            if (upTrend && btcBias01.Value <= -2m)
                return Fail("btc_reversal_pause");
        }

        var chase = ChaseBan(h1, upTrend, ema21, atr1h, retrace);
        if (chase != null)
            return Fail(chase);

        bool h4Hard = H4Against(upTrend, h4, out decimal h4Pen);
        if (h4Hard && !IsMajor(symbol))
            return Fail(upTrend ? "h4_against_long" : "h4_against_short");

        if (!ImpulseOk(m15, upTrend, atr15, out var imp))
            return Fail(imp);

        if (FalseSignal(m15, eF, atr15, upTrend, out var fake))
            return Fail(fake);

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

            var ladder = RLadderPolicy.Current;
            decimal sl, risk;
            string slWhy;
            bool stopOk = ladder.Enabled && ladder.UsePullbackStop
                ? TryStopPullback(true, close, atr15, m15, out sl, out risk, out slWhy)
                : TryStop(true, close, swingLo, atr1h, atr15, m15, out sl, out risk, out slWhy);
            if (!stopOk)
                return Fail(slWhy);

            decimal[] tps;
            decimal rr;
            if (ladder.Enabled)
            {
                var opp = NearestOpposingLevel(true, close, h1, h4, d1Raw);
                decimal room = RLadderPolicy.RoomR(close, sl, true, opp);
                if (ladder.MinRoomR > 0 && room < ladder.MinRoomR)
                    return Fail($"no_room:{Math.Min(room, 99m):F1}R");
                tps = RLadderPolicy.TargetPrices(close, sl, true, ladder);
                rr = ladder.TargetsR[0];
            }
            else
            {
                tps = StructureTps(true, close, risk, swingHi, swingLo, atr15, atr1h, m15);
                rr = (tps[0] - close) / risk;
                if (rr < MinRr)
                    return Fail("rr_fail");
            }

            decimal conf = Score(volR, strongReject, retrace, fullStructure) - Math.Min(0.18m, h4Pen + softPenalty);
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

        var ladderS = RLadderPolicy.Current;
        decimal slS, riskS;
        string slWhyS;
        bool stopOkS = ladderS.Enabled && ladderS.UsePullbackStop
            ? TryStopPullback(false, close, atr15, m15, out slS, out riskS, out slWhyS)
            : TryStop(false, close, swingHi, atr1h, atr15, m15, out slS, out riskS, out slWhyS);
        if (!stopOkS)
            return Fail(slWhyS);

        decimal[] tpsS;
        decimal rrS;
        if (ladderS.Enabled)
        {
            var oppS = NearestOpposingLevel(false, close, h1, h4, d1Raw);
            decimal roomS = RLadderPolicy.RoomR(close, slS, false, oppS);
            if (ladderS.MinRoomR > 0 && roomS < ladderS.MinRoomR)
                return Fail($"no_room:{Math.Min(roomS, 99m):F1}R");
            tpsS = RLadderPolicy.TargetPrices(close, slS, false, ladderS);
            rrS = ladderS.TargetsR[0];
        }
        else
        {
            tpsS = StructureTps(false, close, riskS, swingHi, swingLo, atr15, atr1h, m15);
            rrS = (close - tpsS[0]) / riskS;
            if (rrS < MinRr)
                return Fail("rr_fail");
        }

        decimal confS = Score(volR, strongRejectS, retrace, fullStructure) - Math.Min(0.18m, h4Pen + softPenalty);
        if (confS < MinConfidence)
            return Fail($"conf_low:{confS:F2}");

        return Ok(Build(symbol, SignalSide.Sell, close, slS, tpsS, confS, atr1h, rrS, "CORE_INST_SHORT"));
    }

    /// <summary>
    /// Stop sits past the liquidity pool, not on it.
    /// The pool is the further of the 1H swing and the last 15m wick.
    /// Resting stops cluster on that wick and on the round number just beyond it.
    /// A sweep runs a short distance through the pool and comes back, so the
    /// stop is the pool plus that room. If the distance exceeds MaxRiskPct the
    /// trade is skipped. The stop is never pulled back into the pool.
    /// Size is 1R of equity, so a wider stop reduces quantity, not the dollar risk.
    /// </summary>
    private static bool TryStop(
        bool isLong, decimal close, decimal swingInvalidation,
        decimal atr1h, decimal atr15,
        List<BinanceFuturesUsdtKline> m15,
        out decimal sl, out decimal risk, out string why)
    {
        sl = 0;
        risk = 0;
        why = "";

        decimal pool = swingInvalidation;
        int from = Math.Max(0, m15.Count - 10);
        for (int k = from; k < m15.Count; k++)
        {
            decimal wick = isLong ? m15[k].LowPrice : m15[k].HighPrice;
            pool = isLong ? Math.Min(pool, wick) : Math.Max(pool, wick);
        }

        decimal localInvalid = isLong
            ? close - Math.Max(atr15 * 1.4m, atr1h * 0.55m)
            : close + Math.Max(atr15 * 1.4m, atr1h * 0.55m);

        decimal inv = isLong
            ? Math.Min(pool, localInvalid)
            : Math.Max(pool, localInvalid);

        // Structural pad, then the extra distance a sweep wick travels
        // through the pool before the bar closes back inside.
        decimal pad = Math.Max(atr1h * Atr1hPad, atr15 * Atr15Pad);
        decimal sweepRoom = Math.Max(atr15 * 0.45m, close * 0.0018m);
        pad += sweepRoom;

        decimal slStruct = isLong ? inv - pad : inv + pad;
        slStruct = NudgePastRound(slStruct, close, isLong);
        decimal structRisk = isLong ? close - slStruct : slStruct - close;
        if (structRisk <= 0)
        {
            why = "bad_risk";
            return false;
        }

        decimal minDist = Math.Max(close * MinRiskPct, atr15 * 0.70m);
        decimal maxDist = close * MaxRiskPct;
        if (minDist > maxDist)
        {
            why = "sl_band_impossible";
            return false;
        }

        if (structRisk > maxDist)
        {
            why = $"sl_too_wide:{structRisk / close:P2}";
            return false;
        }

        if (structRisk < minDist)
        {
            why = $"stop_inside_noise:{structRisk / close:P2}";
            return false;
        }

        sl = slStruct;
        risk = structRisk;
        return true;
    }

    /// <summary>
    /// Pullback stop (RLadder StopMode=Pullback). The entry is timed on 15m, so the
    /// invalidation is the 15m pullback extreme, not the far 1H impulse origin.
    /// The old 1H-sized stop (1–3.2%) with 15m-pivot targets capped every trade at
    /// ≈1.15R (client_001 logs). Stop = pullback extreme ± sweep room, never inside
    /// noise (≥ max(MinRiskPct, 1.0×ATR15)), never wider than MaxRiskPct.
    /// </summary>
    private static bool TryStopPullback(
        bool isLong, decimal close, decimal atr15,
        List<BinanceFuturesUsdtKline> m15,
        out decimal sl, out decimal risk, out string why)
    {
        sl = 0; risk = 0; why = "";
        if (m15.Count < 10 || atr15 <= 0 || close <= 0) { why = "pb_stop_thin"; return false; }

        decimal pool = isLong ? decimal.MaxValue : 0m;
        for (int k = m15.Count - 8; k < m15.Count; k++)
            pool = isLong ? Math.Min(pool, m15[k].LowPrice) : Math.Max(pool, m15[k].HighPrice);

        decimal sweepRoom = Math.Max(atr15 * 0.35m, close * 0.0015m);
        decimal s = isLong ? pool - sweepRoom : pool + sweepRoom;
        s = NudgePastRound(s, close, isLong);

        decimal minDist = Math.Max(close * MinRiskPct * 0.85m, atr15 * 1.0m);
        decimal maxDist = close * MaxRiskPct;
        decimal dist = isLong ? close - s : s - close;
        if (dist <= 0) { why = "bad_risk"; return false; }
        if (dist < minDist)
        {
            // Widen away from noise; never pull a stop closer than the floor.
            dist = minDist;
            s = isLong ? close - dist : close + dist;
        }
        if (dist > maxDist) { why = $"sl_too_wide:{dist / close:P2}"; return false; }

        sl = s;
        risk = dist;
        return true;
    }

    /// <summary>
    /// Nearest opposing higher-timeframe liquidity beyond entry: major 1H swings (±4-bar
    /// fractal, the minor ±2 pivots sit every few bars and would veto everything), 4H swings
    /// and the previous daily high/low. null = no level overhead/underfoot (open air).
    /// </summary>
    private static decimal? NearestOpposingLevel(
        bool isLong, decimal entry,
        List<BinanceFuturesUsdtKline> h1,
        List<BinanceFuturesUsdtKline>? h4,
        IReadOnlyList<BinanceFuturesUsdtKline>? d1Raw)
    {
        var levels = new List<decimal>();
        levels.AddRange(Pivots(h1, high: isLong, wing: 4));
        if (h4 != null && h4.Count >= 10) levels.AddRange(Pivots(h4, high: isLong, wing: 2));
        if (d1Raw != null && d1Raw.Count >= 3)
        {
            var d1 = ClosedBars(d1Raw, 1440);
            if (d1.Count >= 2) levels.Add(isLong ? d1[^1].HighPrice : d1[^1].LowPrice);
        }
        decimal minGap = entry * 0.0005m;
        decimal? best = null;
        foreach (var lv in levels)
        {
            if (isLong && lv > entry + minGap && (best is null || lv < best)) best = lv;
            if (!isLong && lv < entry - minGap && lv > 0 && (best is null || lv > best)) best = lv;
        }
        return best;
    }

    /// <summary>
    /// Round prices (65000, 1800, 48.00) are where the resting stops sit.
    /// If the structural stop lands on one, move it further out, never closer.
    /// </summary>
    private static decimal NudgePastRound(decimal sl, decimal price, bool isLong)
    {
        decimal step = RoundStep(price);
        if (step <= 0) return sl;
        decimal rem = sl % step;
        if (rem < 0) rem += step;
        decimal dist = Math.Min(rem, step - rem);
        if (dist > price * 0.0004m) return sl;
        decimal nudge = Math.Max(price * 0.0008m, step * 0.12m);
        return isLong ? sl - nudge : sl + nudge;
    }

    private static decimal RoundStep(decimal price)
    {
        decimal a = Math.Abs(price);
        if (a >= 10000m) return 100m;
        if (a >= 1000m) return 10m;
        if (a >= 100m) return 1m;
        if (a >= 10m) return 0.1m;
        if (a >= 1m) return 0.01m;
        if (a >= 0.1m) return 0.001m;
        return 0.0001m;
    }

    /// <summary>
    /// 15m trigger: pullback into EMA21 zone + close back through it +
    /// volume confirmation. Avoids late chase entries that hit the stop.
    /// </summary>
    private static bool Trigger(
        List<BinanceFuturesUsdtKline> m15, List<decimal> emaFast, decimal atr15,
        bool forLong, out bool strongReject)
    {
        strongReject = false;
        int i = m15.Count - 1;
        if (i < 8 || emaFast.Count != m15.Count) return false;

        // Volume confirmation: trigger bar should not be a dead bar
        decimal volAvg = 0m;
        int volN = 0;
        for (int k = Math.Max(0, i - 10); k < i; k++)
        {
            volAvg += m15[k].Volume;
            volN++;
        }
        if (volN > 0) volAvg /= volN;
        bool volOk = volAvg <= 0 || m15[i].Volume >= volAvg * 0.65m;

        int from = Math.Max(0, i - 6);
        bool touched = false;
        for (int j = from; j <= i; j++)
        {
            var b = m15[j];
            decimal zone = Math.Max(atr15 * 0.85m, b.ClosePrice * 0.0035m);
            decimal rng = b.HighPrice - b.LowPrice;
            if (forLong)
            {
                if (b.LowPrice <= emaFast[j] + zone && b.ClosePrice >= emaFast[j] - zone * 1.2m)
                    touched = true;
                if (rng > 0 && b.ClosePrice >= b.OpenPrice
                    && (b.ClosePrice - b.LowPrice) >= rng * 0.45m
                    && b.LowPrice <= emaFast[j] + zone)
                    strongReject = true;
            }
            else
            {
                if (b.HighPrice >= emaFast[j] - zone && b.ClosePrice <= emaFast[j] + zone * 1.2m)
                    touched = true;
                if (rng > 0 && b.ClosePrice <= b.OpenPrice
                    && (b.HighPrice - b.ClosePrice) >= rng * 0.45m
                    && b.HighPrice >= emaFast[j] - zone)
                    strongReject = true;
            }
        }

        var cur = m15[i];
        decimal rngCur = cur.HighPrice - cur.LowPrice;
        bool through = forLong
            ? cur.ClosePrice > emaFast[i]
            : cur.ClosePrice < emaFast[i];
        bool wonBar = rngCur <= 0 || (forLong
            ? (cur.ClosePrice - cur.LowPrice) >= rngCur * 0.40m && cur.ClosePrice >= cur.OpenPrice
            : (cur.HighPrice - cur.ClosePrice) >= rngCur * 0.40m && cur.ClosePrice <= cur.OpenPrice);

        // Not extended: price should still be near the average (pullback entry, not chase)
        decimal dist = Math.Abs(cur.ClosePrice - emaFast[i]);
        bool notExtended = dist <= atr15 * 1.8m;

        return touched && through && volOk && notExtended && (strongReject || wonBar);
    }

    /// <summary>
    /// The label "uptrend" is not enough. Three of the last four 1H closes
    /// must sit on the trade side of EMA21, EMA50 must point the same way,
    /// and the path must actually be directional (not a chop above the average).
    /// </summary>
    private static bool TrendConfirmed(
        List<BinanceFuturesUsdtKline> h1,
        List<decimal> ema21,
        List<decimal> ema50,
        bool forLong,
        out string why)
    {
        why = "";
        int n = h1.Count;
        if (n < 20 || ema21.Count != n || ema50.Count != n)
        {
            why = "trend_thin";
            return false;
        }

        // Trend lives above/below EMA50. Requiring 3 of the last 4 closes
        // above EMA21 forbids the pullback the trigger is built to buy.
        int onSide = 0;
        for (int k = 0; k < 6; k++)
        {
            int idx = n - 1 - k;
            bool ok = forLong
                ? h1[idx].ClosePrice > ema50[idx]
                : h1[idx].ClosePrice < ema50[idx];
            if (ok) onSide++;
        }
        if (onSide < 4)
        {
            why = forLong ? "trend_unconfirmed_long" : "trend_unconfirmed_short";
            return false;
        }

        int back = n - 6;
        bool slowAgrees = forLong
            ? ema50[n - 1] >= ema50[back]
            : ema50[n - 1] <= ema50[back];
        if (!slowAgrees)
        {
            why = forLong ? "ema50_still_falling" : "ema50_still_rising";
            return false;
        }

        decimal er = Efficiency(h1, 14);
        if (er < 0.08m)
        {
            why = $"chop_er:{er:F2}";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Bars that look like a trigger and are not. A dominant wick against
    /// the trade, a climax close in the wrong half, or a one-tick nick
    /// back across EMA21 after a real break.
    /// </summary>
    private static bool FalseSignal(
        List<BinanceFuturesUsdtKline> m15,
        List<decimal> emaFast,
        decimal atr,
        bool forLong,
        out string why)
    {
        why = "";
        int i = m15.Count - 1;
        if (i < 3 || atr <= 0 || emaFast.Count != m15.Count) return false;
        var b = m15[i];
        decimal rng = b.HighPrice - b.LowPrice;
        if (rng <= 0) return false;

        decimal upperWick = b.HighPrice - Math.Max(b.OpenPrice, b.ClosePrice);
        decimal lowerWick = Math.Min(b.OpenPrice, b.ClosePrice) - b.LowPrice;
        if (forLong && upperWick >= rng * 0.55m && upperWick > atr * 0.35m)
        {
            why = "false_long_upper_wick";
            return true;
        }
        if (!forLong && lowerWick >= rng * 0.55m && lowerWick > atr * 0.35m)
        {
            why = "false_short_lower_wick";
            return true;
        }

        if (rng >= atr * 2.2m)
        {
            decimal pos = (b.ClosePrice - b.LowPrice) / rng;
            if (forLong && pos < 0.55m)
            {
                why = "false_long_climax";
                return true;
            }
            if (!forLong && pos > 0.45m)
            {
                why = "false_short_climax";
                return true;
            }
        }

        // A nick is a close that never got back through the average.
        // Closing just through EMA21 is the trigger, not a false signal.
        var prev = m15[i - 1];
        if (forLong
            && prev.ClosePrice < emaFast[i - 1] - atr * 0.35m
            && b.ClosePrice <= emaFast[i])
        {
            why = "false_long_nick";
            return true;
        }
        if (!forLong
            && prev.ClosePrice > emaFast[i - 1] + atr * 0.35m
            && b.ClosePrice >= emaFast[i])
        {
            why = "false_short_nick";
            return true;
        }
        return false;
    }

    private static decimal Efficiency(List<BinanceFuturesUsdtKline> k, int bars)
    {
        if (k.Count < bars + 1) return 0;
        int end = k.Count - 1;
        int start = end - bars;
        decimal net = Math.Abs(k[end].ClosePrice - k[start].ClosePrice);
        decimal path = 0;
        for (int i = start + 1; i <= end; i++)
            path += Math.Abs(k[i].ClosePrice - k[i - 1].ClosePrice);
        return path <= 0 ? 0 : net / path;
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
        decimal c = fullStructure ? 0.68m : 0.62m;
        if (volR >= 0.90m) c += 0.03m;
        if (volR >= 1.20m) c += 0.03m;
        if (reject) c += 0.04m;
        if (retrace >= 0.28m && retrace <= 0.58m) c += 0.04m;
        return Math.Min(0.88m, c);
    }

    /// <summary>
    /// BTC (and any symbol) : true when new shorts must wait.
    /// EMA can still be stacked short. The swing cannot.
    /// </summary>
    public static bool ShortsPaused(IReadOnlyList<BinanceFuturesUsdtKline> h1Raw)
        => DirectionPaused(h1Raw, blockingShorts: true);

    public static bool LongsPaused(IReadOnlyList<BinanceFuturesUsdtKline> h1Raw)
        => DirectionPaused(h1Raw, blockingShorts: false);

    private static bool DirectionPaused(IReadOnlyList<BinanceFuturesUsdtKline> h1Raw, bool blockingShorts)
    {
        if (h1Raw == null || h1Raw.Count < 40) return false;
        var h1 = ClosedBars(h1Raw, 60);
        if (h1.Count < 30) return false;
        var st = ReadStructure(h1);
        decimal swingHi, swingLo;
        if (st != null)
        {
            swingHi = st.LastSwingHigh;
            swingLo = st.LastSwingLow;
        }
        else
        {
            var w = h1.TakeLast(16).ToList();
            swingHi = w.Max(x => x.HighPrice);
            swingLo = w.Min(x => x.LowPrice);
        }
        if (swingHi <= swingLo) return false;
        decimal close = h1[^1].ClosePrice;
        decimal span = swingHi - swingLo;
        decimal pos = (close - swingLo) / span;
        bool upTrend = !blockingShorts;
        decimal retrace = upTrend ? 1m - pos : pos;
        decimal volR = VolRatio(h1, 8, 20);
        if (ReversalPause(h1, st, upTrend, close, swingHi, swingLo, volR) != null)
            return true;
        return SessionFade(h1, st, upTrend, h1[^1].OpenTime, retrace, volR) != null;
    }

    /// <summary>
    /// Dow / swing fact, not a new indicator.
    /// A bearish continuation exists only while price is still under the last
    /// lower high and has not reclaimed the midpoint of that swing on rising
    /// closes. The mirror applies to longs. Past that, wait for the next structure.
    /// </summary>
    private static string? ReversalPause(
        List<BinanceFuturesUsdtKline> h1, Struct? st, bool upTrend,
        decimal close, decimal swingHi, decimal swingLo, decimal volR)
    {
        if (h1.Count < 8 || swingHi <= swingLo || close <= 0) return null;
        decimal mid = (swingHi + swingLo) / 2m;
        bool risingCloses = h1[^1].ClosePrice > h1[^2].ClosePrice
            && h1[^2].ClosePrice > h1[^3].ClosePrice;
        bool fallingCloses = h1[^1].ClosePrice < h1[^2].ClosePrice
            && h1[^2].ClosePrice < h1[^3].ClosePrice;

        if (!upTrend)
        {
            if (st != null && close > st.LastSwingHigh)
                return "choch_up";
            bool higherLow = st != null && st.LastSwingLow > st.PrevSwingLow;
            bool structureTurning = higherLow || RisingLows(h1, 4);
            if (close > mid && risingCloses && structureTurning)
                return "reversal_pause_short";
            if (close > mid && risingCloses && BounceAbsorbs(h1, volR))
                return "reversal_volume_short";
            return null;
        }

        if (st != null && close < st.LastSwingLow)
            return "choch_down";
        bool lowerHigh = st != null && st.LastSwingHigh < st.PrevSwingHigh;
        bool structureFailing = lowerHigh || FallingHighs(h1, 4);
        if (close < mid && fallingCloses && structureFailing)
            return "reversal_pause_long";
        if (close < mid && fallingCloses && BounceAbsorbs(h1, volR))
            return "reversal_volume_long";
        return null;
    }

    /// <summary>
    /// Cash sessions are the clocks that actually move crypto liquidity:
    /// Tokyo 09:00–15:00, London 08:00–16:30, New York 09:30–16:00, DST-aware.
    /// A weekday impulse that has already given back 40% in the weekend book
    /// or after the New York cash close is being faded. Do not add to it.
    /// Asia range faded by the New York cash session is the same fact.
    /// </summary>
    private static string? SessionFade(
        List<BinanceFuturesUsdtKline> h1, Struct? st, bool upTrend,
        DateTime entryOpen, decimal retrace, decimal volR)
    {
        if (!DeskClocksReady) return null;
        var entryUtc = AsUtc(entryOpen);
        bool thin = ThinBook(entryUtc);
        if (st == null)
            return thin && volR < 0.55m ? "thin_session_volume" : null;

        decimal pivot = upTrend ? st.LastSwingLow : st.LastSwingHigh;
        int idx = FindPivot(h1, pivot, high: !upTrend);
        if (idx < 0)
            return thin && volR < 0.55m ? "thin_session_volume" : null;

        var impulse = DeskAt(AsUtc(h1[idx].OpenTime));
        var entry = DeskAt(entryUtc);
        bool impulseWasCash = impulse is DeskSession.Tokyo or DeskSession.London
            or DeskSession.NewYork or DeskSession.Overlap;
        if (thin && impulseWasCash && retrace >= 0.40m)
            return "session_fade";
        if (thin && volR < 0.55m)
            return "thin_session_volume";
        if (!thin
            && impulse == DeskSession.Tokyo
            && entry is DeskSession.NewYork or DeskSession.Overlap
            && retrace >= 0.50m)
            return "session_fade";
        return null;
    }

    private enum DeskSession { Tokyo, London, NewYork, Overlap, Off }

    private static readonly TimeZoneInfo TzTokyo = SafeZone("Asia/Tokyo", "Tokyo Standard Time");
    private static readonly TimeZoneInfo TzLondon = SafeZone("Europe/London", "GMT Standard Time");
    private static readonly TimeZoneInfo TzNewYork = SafeZone("America/New_York", "Eastern Standard Time");
    private static bool DeskClocksReady =>
        TzTokyo.Id != TimeZoneInfo.Utc.Id
        && TzLondon.Id != TimeZoneInfo.Utc.Id
        && TzNewYork.Id != TimeZoneInfo.Utc.Id;

    private static TimeZoneInfo SafeZone(string iana, string windows)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }
        try { return TimeZoneInfo.FindSystemTimeZoneById(windows); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    private static DateTime AsUtc(DateTime t)
    {
        if (t.Kind == DateTimeKind.Utc) return t;
        if (t.Kind == DateTimeKind.Local) return t.ToUniversalTime();
        return DateTime.SpecifyKind(t, DateTimeKind.Utc);
    }

    private static bool InCash(DateTime utc, TimeZoneInfo tz, TimeSpan from, TimeSpan to)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, tz).TimeOfDay;
        return local >= from && local < to;
    }

    private static DeskSession DeskAt(DateTime utc)
    {
        bool tokyo = InCash(utc, TzTokyo, new TimeSpan(9, 0, 0), new TimeSpan(15, 0, 0));
        bool london = InCash(utc, TzLondon, new TimeSpan(8, 0, 0), new TimeSpan(16, 30, 0));
        bool ny = InCash(utc, TzNewYork, new TimeSpan(9, 30, 0), new TimeSpan(16, 0, 0));
        if (london && ny) return DeskSession.Overlap;
        if (ny) return DeskSession.NewYork;
        if (london) return DeskSession.London;
        if (tokyo) return DeskSession.Tokyo;
        return DeskSession.Off;
    }

    /// <summary>
    /// Friday after the New York cash close through Sunday 18:00 New York,
    /// when equity index futures reopen. Also the weekday gap from that
    /// close until the Tokyo cash open.
    /// </summary>
    private static bool ThinBook(DateTime utc)
    {
        var ny = TimeZoneInfo.ConvertTimeFromUtc(utc, TzNewYork);
        if (ny.DayOfWeek == DayOfWeek.Saturday) return true;
        if (ny.DayOfWeek == DayOfWeek.Sunday && ny.TimeOfDay < new TimeSpan(18, 0, 0))
            return true;
        if (ny.DayOfWeek == DayOfWeek.Friday && ny.TimeOfDay >= new TimeSpan(16, 0, 0))
            return true;
        if (DeskAt(utc) != DeskSession.Off) return false;
        var tokyo = TimeZoneInfo.ConvertTimeFromUtc(utc, TzTokyo);
        return ny.TimeOfDay >= new TimeSpan(16, 0, 0) && tokyo.Hour < 9;
    }

    private static int FindPivot(List<BinanceFuturesUsdtKline> h1, decimal price, bool high)
    {
        for (int i = h1.Count - 3; i >= 0; i--)
        {
            decimal p = high ? h1[i].HighPrice : h1[i].LowPrice;
            if (p == price) return i;
        }
        return -1;
    }

    private static bool RisingLows(List<BinanceFuturesUsdtKline> h1, int n)
    {
        if (h1.Count < n) return false;
        for (int i = h1.Count - n + 1; i < h1.Count; i++)
            if (h1[i].LowPrice < h1[i - 1].LowPrice) return false;
        return true;
    }

    private static bool FallingHighs(List<BinanceFuturesUsdtKline> h1, int n)
    {
        if (h1.Count < n) return false;
        for (int i = h1.Count - n + 1; i < h1.Count; i++)
            if (h1[i].HighPrice > h1[i - 1].HighPrice) return false;
        return true;
    }

    private static bool BounceAbsorbs(List<BinanceFuturesUsdtKline> h1, decimal volR)
    {
        if (h1.Count < 14 || volR < 0.80m) return false;
        decimal recent = 0m, prior = 0m;
        int n = h1.Count;
        for (int i = 0; i < 4; i++) recent += h1[n - 1 - i].Volume;
        for (int i = 0; i < 8; i++) prior += h1[n - 5 - i].Volume;
        if (prior <= 0) return false;
        return (recent / 4m) / (prior / 8m) >= 1.25m;
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
        if (ageMin < barMinutes && k.Count >= 2)
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

    private static Struct? SyntheticSwing(List<BinanceFuturesUsdtKline> k)
    {
        if (k == null || k.Count < 12) return null;
        int from = Math.Max(0, k.Count - 24);
        var win = k.Skip(from).ToList();
        decimal hi = win.Max(x => x.HighPrice);
        decimal lo = win.Min(x => x.LowPrice);
        if (hi <= lo || lo <= 0) return null;
        // One direction only. Equal pivots used to set IsBullish and IsBearish
        // at the same time, so a dip in an uptrend was sold.
        bool rising = win[^1].ClosePrice >= win[0].OpenPrice;
        return new Struct
        {
            LastSwingHigh = rising ? hi : hi * 0.997m,
            PrevSwingHigh = rising ? hi * 0.997m : hi,
            LastSwingLow = rising ? lo : lo * 0.997m,
            PrevSwingLow = rising ? lo * 0.997m : lo
        };
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

    private static List<decimal> Pivots(List<BinanceFuturesUsdtKline> k, bool high, int wing = 2)
    {
        var list = new List<decimal>();
        int w = Math.Max(1, wing);
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
