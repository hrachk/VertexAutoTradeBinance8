using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class AiStopLossOptimizer
    {
        private readonly ILogger<AiStopLossOptimizer> _logger;

        public AiStopLossOptimizer(ILogger<AiStopLossOptimizer> logger)
        {
            _logger = logger;
        }

        private static decimal Atr(
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            int period,
            int lastIndex)
        {
            if (klines.Count < period + 2)
                return 0m;

            int start = Math.Max(1, lastIndex - period + 1);
            decimal sumTr = 0m;
            int trCount = 0;

            for (int i = start; i <= lastIndex; i++)
            {
                var curr = klines[i];
                var prev = klines[i - 1];

                decimal tr1 = curr.HighPrice - curr.LowPrice;
                decimal tr2 = Math.Abs(curr.HighPrice - prev.ClosePrice);
                decimal tr3 = Math.Abs(curr.LowPrice - prev.ClosePrice);

                decimal tr = Math.Max(tr1, Math.Max(tr2, tr3));
                sumTr += tr;
                trCount++;
            }

            return trCount > 0 ? sumTr / trCount : 0m;
        }

        /// <summary>
        /// Динамический множитель SL по ATR и волатильности/режиму.
        /// </summary>
        private static decimal GetDynamicSlAtrMult(string trend, decimal atrPct)
        {
            // atrPct ожидаем в долях (0.001 = 0.1 %)
            bool strongTrend = trend == "UP" || trend == "DOWN";
            bool ultraLowVol = atrPct < 0.0010m;    // <0.10%
            bool lowVol = atrPct < 0.0020m;    // <0.20%
            bool highVol = atrPct > 0.0040m;    // >0.40%

            if (strongTrend)
            {
                if (ultraLowVol)
                    return 1.5m;   // quiet trend — still needs breathing room
                if (lowVol)
                    return 1.7m;   // normal trend
                if (highVol)
                    return 2.0m;   // volatile trend — wide SL mandatory
                return 1.8m;
            }
            else
            {
                // Range / Squeeze / unclear regime — even wider
                if (ultraLowVol)
                    return 2.0m;
                if (highVol)
                    return 2.5m;   // choppy range — widest SL

                return 2.2m;
            }
        }

        /// <summary>
        /// Optimizes Stop Loss placement using dynamic ATR distance and structure-aware logic.
        /// Also applies regime-aware TP boost to signal.TakeProfits (in-place update).
        ///
        /// SL improvements:
        ///  - Anti-stophunt: push SL past recent significant wick if wicked past our SL
        ///  - Dynamic floor: min SL = ATR × dynMult (regime-aware: wider in chop/range)
        ///  - Low-vol tighten: in quiet+trend, trim SL by 30% to improve RR
        ///
        /// TP improvements:
        ///  - If signal already has TakeProfits (from StrategyEngine with regime boost):
        ///    apply additional confidence/trend fine-tuning.
        ///  - If signal has no TPs (emergency fallback): build from ATR × 1.5/2.5/3.5.
        ///  - MeanReversion signals: TP never touched (thesis = return to mean).
        /// </summary>
        public decimal OptimizeSlAndTp(
            string symbol,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            TradeSignal signal,
            AiDecision decision)
        {
            if (klines == null || klines.Count < 10)
                return signal.StopLoss;

            int lastIndex = klines.Count - 1;
            var last = klines[lastIndex];

            decimal atr14 = signal.Atr ?? Atr(klines, 14, lastIndex);
            if (atr14 <= 0m)
                return signal.StopLoss;

            bool isLong     = signal.Side == SignalSide.Buy;
            decimal oldSl   = signal.StopLoss;
            decimal newSl   = oldSl;
            decimal dist    = Math.Abs(signal.EntryPrice - oldSl);
            bool isMeanRev  = signal.Reason?.StartsWith("MEANREV_", StringComparison.OrdinalIgnoreCase) == true;

            // ── 1. REMOVED: Low-vol tighten was shrinking SL by 30% ─────────
            // This was the #1 cause of stop-outs: in a quiet trend the SL
            // was pulled INTO the noise band. The idea "quiet trend = tight SL"
            // is backwards — quiet trends have random micro-wicks that look
            // like nothing on the chart but are enough to trigger a tight SL.
            // Correct approach: keep the structural SL where it was placed.

            // ── 2. Anti-stophunt: push SL past significant wick ──────────────
            decimal upperWick = last.HighPrice - Math.Max(last.OpenPrice, last.ClosePrice);
            decimal lowerWick = Math.Min(last.OpenPrice, last.ClosePrice) - last.LowPrice;

            if (isLong && lowerWick > atr14 * 1.2m)
            {
                var candidate = last.LowPrice - atr14 * 0.2m;
                if (candidate < newSl) newSl = candidate;
            }
            else if (!isLong && upperWick > atr14 * 1.2m)
            {
                var candidate = last.HighPrice + atr14 * 0.2m;
                if (candidate > newSl) newSl = candidate;
            }

            // ── 3. Dynamic SL floor (regime-aware minimum distance) ───────────
            decimal dynMult     = GetDynamicSlAtrMult(decision.Trend, decision.AtrPct);
            decimal minDist     = atr14 * dynMult;
            decimal currentDist = Math.Abs(signal.EntryPrice - newSl);
            if (currentDist < minDist)
            {
                newSl = isLong
                    ? signal.EntryPrice - minDist
                    : signal.EntryPrice + minDist;
            }

            // ── 4. TP optimization ────────────────────────────────────────────
            // MeanReversion: never touch TPs (thesis = price returns to mean).
            if (!isMeanRev)
            {
                if (signal.TakeProfits != null && signal.TakeProfits.Count > 0)
                {
                    // TP already set by StrategyEngine (with regime boost).
                    // Apply small confidence fine-tune only if TP seems too small.
                    decimal tp1 = signal.TakeProfits[0];
                    decimal tp1Dist = Math.Abs(tp1 - signal.EntryPrice);
                    decimal slDist  = Math.Abs(signal.EntryPrice - newSl);

                    // If RR1 < 1.4 after SL adjustment, push TP1 a bit further
                    if (slDist > 0 && tp1Dist / slDist < 1.4m)
                    {
                        decimal targetDist = slDist * 1.5m; // target RR = 1.5
                        decimal diff = targetDist - tp1Dist;
                        int dir = isLong ? 1 : -1;
                        signal.TakeProfits[0] = tp1 + dir * diff;
                        _logger.LogInformation(
                            "[TP_ADJUST][{sym}] TP1 pushed for RR≥1.5: {old:F4}→{new:F4}",
                            symbol, tp1, signal.TakeProfits[0]);
                    }
                }
                else
                {
                    // No TPs at all: build minimal fallback
                    decimal slD  = Math.Abs(signal.EntryPrice - newSl);
                    int dir = isLong ? 1 : -1;
                    signal.TakeProfits = new System.Collections.Generic.List<decimal>
                    {
                        signal.EntryPrice + dir * slD * 1.5m,  // RR = 1.5
                        signal.EntryPrice + dir * slD * 2.5m,  // RR = 2.5
                        signal.EntryPrice + dir * slD * 3.5m,  // RR = 3.5
                    };
                    _logger.LogInformation(
                        "[TP_FALLBACK][{sym}] Built TP from SL dist: TP1={tp1:F4} TP2={tp2:F4} TP3={tp3:F4}",
                        symbol, signal.TakeProfits[0], signal.TakeProfits[1], signal.TakeProfits[2]);
                }
            }

            _logger.LogInformation(
                "[SL_OPT][{sym}] oldSL={old:F4} → newSL={new:F4} | atr={atr:F6} dynMult={mult:F2} trend={trend} isMeanRev={mr}",
                symbol, oldSl, newSl, atr14, dynMult, decision.Trend, isMeanRev);

            return newSl;
        }

    }
}

