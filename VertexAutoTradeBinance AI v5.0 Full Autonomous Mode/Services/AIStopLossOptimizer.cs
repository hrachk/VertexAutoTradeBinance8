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
                    return 1.0m;   // чистый сильный тренд — SL ближе
                if (lowVol)
                    return 1.2m;   // нормальный тренд
                if (highVol)
                    return 1.5m;   // тренд, но рывки — SL шире
                return 1.3m;
            }
            else
            {
                // Range / Squeeze / непонятный режим — шире SL
                if (ultraLowVol)
                    return 1.4m;
                if (highVol)
                    return 1.8m;

                return 1.6m;
            }
        }

        /// <summary>
        /// Полная версия – используется там, где есть и klines, и AiDecision.
        /// </summary>
        public decimal OptimizeSl(
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

            decimal oldSl = signal.StopLoss;
            decimal newSl = oldSl;

            decimal dist = Math.Abs(signal.EntryPrice - oldSl);

            // ==========================
            // 1) низкий шум + тренд → чуть поджать SL
            // ==========================
            if ((decision.Trend == "UP" || decision.Trend == "DOWN") &&
                decision.AtrPct < 0.0015m &&         // < 0.15 %
                dist > atr14 * 0.5m)
            {
                decimal tighten = dist * 0.30m;
                if (signal.Side == SignalSide.Buy)
                    newSl = signal.EntryPrice - (dist - tighten);
                else
                    newSl = signal.EntryPrice + (dist - tighten);
            }

            // ==========================
            // 2) анти-стопхант по хвостам
            // ==========================
            decimal upperWick = last.HighPrice - Math.Max(last.OpenPrice, last.ClosePrice);
            decimal lowerWick = Math.Min(last.OpenPrice, last.ClosePrice) - last.LowPrice;

            if (signal.Side == SignalSide.Buy && lowerWick > atr14 * 1.2m)
            {
                var candidate = last.LowPrice - atr14 * 0.2m;
                if (candidate < newSl) newSl = candidate;
            }
            else if (signal.Side == SignalSide.Sell && upperWick > atr14 * 1.2m)
            {
                var candidate = last.HighPrice + atr14 * 0.2m;
                if (candidate > newSl) newSl = candidate;
            }

            // ==========================
            // 3) ДИНАМИЧЕСКИЙ SL по ATR (минимальная дистанция)
            // ==========================
            decimal dynMult = GetDynamicSlAtrMult(decision.Trend, decision.AtrPct);
            decimal minDist = atr14 * dynMult;

            decimal currentDist = Math.Abs(signal.EntryPrice - newSl);
            if (currentDist < minDist)
            {
                // расширяем SL до нужной глубины
                if (signal.Side == SignalSide.Buy)
                    newSl = signal.EntryPrice - minDist;
                else
                    newSl = signal.EntryPrice + minDist;
            }

            if (newSl != oldSl)
            {
                _logger.LogInformation(
                    "AI-SL OPTIMIZER {Symbol}: oldSL={Old:F4}, newSL={New:F4}, trend={Trend}, atr%={AtrPct:P2}, dynMult={Mult:F2}",
                    symbol, oldSl, newSl, decision.Trend, decision.AtrPct, dynMult);
            }

            return newSl;
        }

        /// <summary>
        /// Упрощённый overload для PositionSupervisor:
        /// работает только по klines + entry/sl/side, без AiDecision.
        /// </summary>
        public decimal OptimizeSl(
            string symbol,
            IReadOnlyList<BinanceFuturesUsdtKline>? klines,
            decimal entryPrice,
            decimal stopLoss,
            SignalSide side)
        {
            if (klines == null || klines.Count < 10)
                return stopLoss;

            int lastIndex = klines.Count - 1;
            var last = klines[lastIndex];

            decimal atr14 = Atr(klines, 14, lastIndex);
            if (atr14 <= 0m)
                return stopLoss;

            decimal oldSl = stopLoss;
            decimal newSl = oldSl;

            // Анти-манипуляция: двигаем SL за хвост, если явно выбивали
            decimal upperWick = last.HighPrice - Math.Max(last.OpenPrice, last.ClosePrice);
            decimal lowerWick = Math.Min(last.OpenPrice, last.ClosePrice) - last.LowPrice;

            if (side == SignalSide.Buy && lowerWick > atr14 * 1.2m)
            {
                var candidate = last.LowPrice - atr14 * 0.2m;
                if (candidate < newSl) newSl = candidate;
            }
            else if (side == SignalSide.Sell && upperWick > atr14 * 1.2m)
            {
                var candidate = last.HighPrice + atr14 * 0.2m;
                if (candidate > newSl) newSl = candidate;
            }

            // Без AiDecision — просто гарантируем минимальную глубину 1.2–1.6 ATR
            decimal minMult = 1.4m;
            decimal minDist = atr14 * minMult;
            decimal currentDist = Math.Abs(entryPrice - newSl);

            if (currentDist < minDist)
            {
                if (side == SignalSide.Buy)
                    newSl = entryPrice - minDist;
                else
                    newSl = entryPrice + minDist;
            }

            if (newSl != oldSl)
            {
                _logger.LogInformation(
                    "AI-SL SIMPLE {Symbol}: oldSL={Old:F4}, newSL={New:F4}, atr={Atr:F4}",
                    symbol, oldSl, newSl, atr14);
            }

            return newSl;
        }
    }
}
