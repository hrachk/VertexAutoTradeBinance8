using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// HyperSensitivity-ядро подтверждения сигналов.
    /// </summary>
    public class PredictiveEngineV4ConfirmationService
    {
        private readonly ILogger<PredictiveEngineV4ConfirmationService> _logger;

        public PredictiveEngineV4ConfirmationService(
            ILogger<PredictiveEngineV4ConfirmationService> logger)
        {
            _logger = logger;
        }

        // ---------------- helpers ----------------

        private static decimal Atr(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period, int lastIndex)
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

        private static decimal Ema(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period, int index)
        {
            int start = Math.Max(0, index - period + 1);
            int count = index - start + 1;
            if (count <= 0) return klines[index].ClosePrice;

            decimal k = 2m / (period + 1);
            decimal ema = klines[start].ClosePrice;

            for (int i = start + 1; i <= index; i++)
            {
                var close = klines[i].ClosePrice;
                ema = close * k + ema * (1 - k);
            }

            return ema;
        }

        private static decimal Clamp01(decimal v)
        {
            if (v < 0m) return 0m;
            if (v > 1m) return 1m;
            return v;
        }

        // ------------- main decision -------------
        public AiDecision Decide(
            string symbol,
            KlineInterval timeframe,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            TradeSignal signal)
        {
            if (klines == null || klines.Count < 60)
            {
                return new AiDecision(false, "BLOCK", 0m, 0m, "FLAT", 0m, 0m, false, signal.IsSuperSignal, "NO_DATA");
            }

            int lastIndex = klines.Count - 1;
            var last = klines[lastIndex];

            // === ATR ===
            var atr14 = Atr(klines, 14, lastIndex);
            if (atr14 <= 0m || last.ClosePrice <= 0m)
            {
                return new AiDecision(false, "BLOCK", 0m, 0m, "FLAT", 0m, 0m, false, signal.IsSuperSignal, "NO_ATR");
            }

            var atrPct = atr14 / last.ClosePrice;

            // ======================================================
            // SLOPE + ACCELERATION
            // ======================================================
            decimal slope = 0m;
            decimal prevSlope = 0m;
            int lookback = 20;

            if (lastIndex >= lookback)
            {
                var past = klines[lastIndex - lookback].ClosePrice;
                if (past > 0)
                    slope = (last.ClosePrice - past) / past;
            }

            if (lastIndex >= lookback * 2)
            {
                var p1 = klines[lastIndex - lookback].ClosePrice;
                var p2 = klines[lastIndex - lookback * 2].ClosePrice;

                if (p2 > 0)
                    prevSlope = (p1 - p2) / p2;
            }

            decimal acceleration = slope - prevSlope;

            // ======================================================
            // EMA TREND
            // ======================================================
            var ema21 = Ema(klines, 21, lastIndex);
            var ema50 = Ema(klines, 50, lastIndex);

            string trend;
            decimal trendScore;

            if (ema21 > ema50 && last.ClosePrice > ema21 && acceleration > 0)
            {
                trend = "UP";
                trendScore = 1.0m;
            }
            else if (ema21 < ema50 && last.ClosePrice < ema21 && acceleration < 0)
            {
                trend = "DOWN";
                trendScore = 1.0m;
            }
            else
            {
                trend = "FLAT";
                trendScore = 0.3m;
            }

            // ======================================================
            // BODY
            // ======================================================
            decimal body = Math.Abs(last.ClosePrice - last.OpenPrice);
            decimal bodyAtr = body / atr14;

            decimal bodyScore =
                bodyAtr >= 1.5m ? 1.0m :
                bodyAtr >= 1.0m ? 0.8m :
                bodyAtr >= 0.6m ? 0.5m :
                0.2m;

            // ======================================================
            // ORDERFLOW
            // ======================================================
            decimal buy = 0m;
            decimal sell = 0m;

            int flowStart = Math.Max(1, lastIndex - 10);

            for (int i = flowStart; i <= lastIndex; i++)
            {
                buy += klines[i].TakerBuyBaseVolume;
                sell += (klines[i].Volume - klines[i].TakerBuyBaseVolume);
            }

            decimal pressure = (buy - sell) / Math.Max(1m, buy + sell);

            // ======================================================
            // TRAP DETECTION
            // ======================================================
            bool weakMove = Math.Abs(acceleration) < atrPct * 0.5m;
            bool extremeBody = bodyAtr > 1.8m;
            bool weakPressure = Math.Abs(pressure) < 0.15m;

            bool isTrap = weakMove && extremeBody && weakPressure;

            // ======================================================
            // RR
            // ======================================================
            decimal rr = 0m;

            if (signal.TakeProfits?.Count > 0)
            {
                var tp1 = signal.TakeProfits[0];
                var risk = Math.Abs(signal.EntryPrice - signal.StopLoss);
                var reward = Math.Abs(tp1 - signal.EntryPrice);

                if (risk > 0)
                    rr = reward / risk;
            }

            decimal rrScore =
                rr >= 2.0m ? 1.0m :
                rr >= 1.5m ? 0.8m :
                rr >= 1.0m ? 0.6m :
                rr >= 0.6m ? 0.3m :
                0.1m;

            // ======================================================
            // MANIPULATION (STOP HUNT)
            // ======================================================
            decimal upperWick = last.HighPrice - Math.Max(last.OpenPrice, last.ClosePrice);
            decimal lowerWick = Math.Min(last.OpenPrice, last.ClosePrice) - last.LowPrice;

            bool manip = upperWick > atr14 * 1.8m || lowerWick > atr14 * 1.8m;

            if (manip && weakPressure)
            {
                return new AiDecision(false, "BLOCK", 0m, atrPct, trend, bodyAtr, rr, true, signal.IsSuperSignal, "STOP_HUNT");
            }

            decimal manipScore = manip ? 0.2m : 1.0m;
            decimal superScore = signal.IsSuperSignal ? 1.0m : 0.4m;

            // ======================================================
            // SCORE
            // ======================================================
            const decimal wTrend = 0.25m;
            const decimal wAtr = 0.30m;
            const decimal wBody = 0.25m;
            const decimal wRr = 0.10m;
            const decimal wManip = 0.07m;
            const decimal wSuper = 0.03m;

            decimal atrScore =
                atrPct >= 0.004m ? 0.9m :
                atrPct >= 0.002m ? 1.0m :
                atrPct >= 0.0008m ? 0.7m :
                0.2m;

            decimal score =
                wTrend * trendScore +
                wAtr * atrScore +
                wBody * bodyScore +
                wRr * rrScore +
                wManip * manipScore +
                wSuper * superScore;

            // ======================================================
            // IMPULSE BOOST
            // ======================================================
            if (Math.Abs(pressure) > 0.25m && Math.Abs(acceleration) > atrPct)
            {
                score = Math.Min(1m, score + 0.15m);
            }

            score = Clamp01(score);

            // ======================================================
            // FINAL DECISION
            // ======================================================
            if (isTrap)
            {
                return new AiDecision(false, "BLOCK", 0m, atrPct, trend, bodyAtr, rr, true, signal.IsSuperSignal, "TRAP");
            }

            string grade;
            bool allow = true;
            string reason = "OK";

            if (atrPct < 0.0008m)
            {
                grade = "BLOCK";
                allow = false;
                reason = "LOW_ATR";
            }
            else if (score < 0.25m)
            {
                grade = "BLOCK";
                allow = false;
                reason = "LOW_SCORE";
            }
            else if (score < 0.50m)
            {
                grade = "BORDER";
                reason = "BORDER";
            }
            else if (score < 0.70m)
            {
                grade = "GOOD";
                reason = "GOOD";
            }
            else
            {
                grade = "STRONG";
                reason = "STRONG";
            }

            var decision = new AiDecision(
                allow,
                grade,
                score,
                atrPct,
                trend,
                bodyAtr,
                rr,
                manip,
                signal.IsSuperSignal,
                reason);

            _logger.LogInformation(
                "AI DECISION {Symbol} {TF} allow={Allow} grade={Grade} score={Score:F2} trend={Trend} acc={Acc:P2} pressure={Pressure:P2}",
                symbol,
                timeframe,
                allow,
                grade,
                score,
                trend,
                acceleration,
                pressure);

            return decision;
        }
    }
}
