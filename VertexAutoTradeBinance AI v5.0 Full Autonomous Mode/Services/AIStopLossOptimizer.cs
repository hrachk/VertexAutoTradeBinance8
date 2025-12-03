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

            // 1) низкий шум + тренд → чуть поджать SL
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

            // 2) анти-стопхант по хвостам
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

            // 3) ГЛОБАЛЬНЫЙ МИНИМУМ SL ПО ATR ДЛЯ 15m (и аналогичных)
            //    Timeframe записан в TradeSignal.Timeframe как строка enum'а KlineInterval.
            if (!string.IsNullOrWhiteSpace(signal.Timeframe))
            {
                var tf = signal.Timeframe;

                // Для FifteenMinutes делаем минимум 1.2 ATR (глобально, не только для LINK)
                if (tf.Contains("FifteenMinutes", StringComparison.OrdinalIgnoreCase))
                {
                    decimal minAtrMult = 1.2m; // 1.2–1.5 ATR: стартуем с 1.2, чтобы не душить
                    decimal minDist = atr14 * minAtrMult;

                    decimal currDist = Math.Abs(signal.EntryPrice - newSl);
                    if (minDist > 0m && currDist < minDist)
                    {
                        if (signal.Side == SignalSide.Buy)
                            newSl = signal.EntryPrice - minDist;
                        else if (signal.Side == SignalSide.Sell)
                            newSl = signal.EntryPrice + minDist;
                    }
                }
            }

            if (newSl != oldSl)
            {
                _logger.LogInformation(
                    "AI-SL OPTIMIZER {Symbol}: oldSL={Old:F4}, newSL={New:F4}, trend={Trend}, atr={Atr:F4}, atr%={AtrPct:P2}",
                    symbol, oldSl, newSl, decision.Trend, atr14, decision.AtrPct);
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
