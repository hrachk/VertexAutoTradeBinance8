using System;
using System.Collections.Generic;
using System.Linq;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Logging;
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
            int lastIndex = klines.Count - 1;
            var last = klines[lastIndex];

            // === ATR / ATR% ===
            var atr14 = Atr(klines, 14, lastIndex);
            if (atr14 <= 0m)
            {
                return new AiDecision(
                    Allow: false,
                    Grade: "BLOCK",
                    Score: 0m,
                    AtrPct: 0m,
                    Trend: "FLAT",
                    BodyAtr: 0m,
                    Rr: 0m,
                    Manipulation: false,
                    SuperSignal: signal.IsSuperSignal,
                    Reason: "NO_ATR");
            }

            var atrPct = atr14 / last.ClosePrice; // в долях

            // === Trend / EMA21 / EMA50 ===
            var ema21 = Ema(klines, 21, lastIndex);
            var ema50 = Ema(klines, 50, lastIndex);

            string trend;
            decimal trendScore;

            if (ema21 > ema50 * 1.001m && last.ClosePrice > ema21)
            {
                trend = "UP";
                trendScore = 1.0m;
            }
            else if (ema21 < ema50 * 0.999m && last.ClosePrice < ema21)
            {
                trend = "DOWN";
                trendScore = 1.0m;
            }
            else
            {
                trend = "FLAT";
                trendScore = 0.3m;
            }

            // === Body / ATR ===
            decimal body = Math.Abs(last.ClosePrice - last.OpenPrice);
            decimal bodyAtr = body / atr14; // 0..?

            decimal bodyScore;
            if (bodyAtr >= 1.5m) bodyScore = 1.0m;
            else if (bodyAtr >= 1.0m) bodyScore = 0.8m;
            else if (bodyAtr >= 0.6m) bodyScore = 0.5m;
            else bodyScore = 0.2m;

            // === Risk/Reward ===
            decimal rr = 0m;
            if (signal.TakeProfits != null && signal.TakeProfits.Count > 0)
            {
                var tp1 = signal.TakeProfits[0];
                var risk = Math.Abs(signal.EntryPrice - signal.StopLoss);
                var reward = Math.Abs(tp1 - signal.EntryPrice);
                if (risk > 0m)
                    rr = reward / risk;
            }

            decimal rrScore;
            if (rr >= 2.0m) rrScore = 1.0m;
            else if (rr >= 1.5m) rrScore = 0.8m;
            else if (rr >= 1.0m) rrScore = 0.6m;
            else if (rr >= 0.6m) rrScore = 0.3m;
            else rrScore = 0.1m;

            // === Manipulation (простая оценка по хвостам) ===
            decimal upperWick = last.HighPrice - Math.Max(last.OpenPrice, last.ClosePrice);
            decimal lowerWick = Math.Min(last.OpenPrice, last.ClosePrice) - last.LowPrice;
            bool manip = upperWick > atr14 * 1.8m || lowerWick > atr14 * 1.8m;

            decimal manipScore = manip ? 0.2m : 1.0m; // если манипуляция сильная – режем score

            // === SuperSignal ===
            decimal superScore = signal.IsSuperSignal ? 1.0m : 0.4m;

            // --------------------------------------------------
            // HyperSensitivity weights  (более чувствительные)
            // --------------------------------------------------
            const decimal wTrend = 0.25m;
            const decimal wAtr = 0.30m;
            const decimal wBody = 0.25m;
            const decimal wRr = 0.10m;
            const decimal wManip = 0.07m;
            const decimal wSuper = 0.03m;

            // ATR score: чем выше ATR%, тем лучше, но без экстремума.
            decimal atrScore;
            if (atrPct >= 0.004m) atrScore = 0.9m;          // 0.4%+
            else if (atrPct >= 0.002m) atrScore = 1.0m;     // 0.2–0.4%
            else if (atrPct >= 0.0008m) atrScore = 0.7m;    // 0.08–0.2%
            else atrScore = 0.2m;                           // слишком мало движения

            // manipScore: 1.0 без манипуляции, 0.2 если заметна
            decimal score =
                wTrend * trendScore +
                wAtr * atrScore +
                wBody * bodyScore +
                wRr * rrScore +
                wManip * manipScore +
                wSuper * superScore;

            score = Clamp01(score);

            // --------------------------------------------------
            // Градации чувствительной версии
            // --------------------------------------------------
            string grade;
            bool allow = true;
            string reason = "OK";

            if (atrPct < 0.0008m) // ATR% < 0.08% → рынок спит
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
                reason = "BORDERLINE";
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
                Allow: allow,
                Grade: grade,
                Score: score,
                AtrPct: atrPct,
                Trend: trend,
                BodyAtr: bodyAtr,
                Rr: rr,
                Manipulation: manip,
                SuperSignal: signal.IsSuperSignal,
                Reason: reason);

            _logger.LogInformation(
                "AI v4.5 DECISION {Symbol} TF={TF} allow={Allow}, grade={Grade}, score={Score:F2}, atrPct={AtrPct:P2}, trend={Trend}, bodyATR={BodyAtr:F2}, rr={Rr:F2}, manip={Manip}, super={Super}, reason={Reason}",
                symbol, timeframe, allow, grade, score, atrPct, trend, bodyAtr, rr, manip, signal.IsSuperSignal, reason);

            return decision;
        }
    }
}
