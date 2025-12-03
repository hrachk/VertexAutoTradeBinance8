using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class AiStopLossOptimizer
    {
        private readonly ILogger<AiStopLossOptimizer> _logger;
        private readonly TradingOptions _options;

        private decimal MinAtrSlMult => _options.MinAtrSlMult <= 0 ? 1.25m : _options.MinAtrSlMult;

        public AiStopLossOptimizer(
            ILogger<AiStopLossOptimizer> logger,
            IOptions<TradingOptions> options)
        {
            _logger = logger;
            _options = options.Value;
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
        /// Глобальный RR-чек: TP1 ≥ MinRR * SL
        /// </summary>
        private bool CheckMinRiskReward(TradeSignal signal, decimal finalSl)
        {
            var minRr = _options.MinRiskReward <= 0 ? 2.0m : _options.MinRiskReward;

            if (signal.EntryPrice <= 0 || finalSl <= 0)
                return false;

            decimal slDistance = Math.Abs(signal.EntryPrice - finalSl);
            if (slDistance <= 0)
                return false;

            decimal? tp1 = null;

            if (signal.TakeProfits != null && signal.TakeProfits.Count > 0)
                tp1 = signal.TakeProfits[0];
            else if (signal.TakeProfit.HasValue)
                tp1 = signal.TakeProfit.Value;

            if (!tp1.HasValue || tp1.Value <= 0)
                return false;

            decimal tpDist = Math.Abs(tp1.Value - signal.EntryPrice);
            var rr = tpDist / slDistance;

            return rr >= minRr;
        }

        /// <summary>
        /// Полная версия – используется там, где есть и klines, и AiDecision.
        /// Глобально: SL минимум MinAtrSlMult * ATR, RR >= MinRiskReward.
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

            // === 0) Минимальный SL по ATR ===
            decimal minDist = atr14 * MinAtrSlMult;
            decimal dist = Math.Abs(signal.EntryPrice - newSl);

            if (dist < minDist)
            {
                if (signal.Side == SignalSide.Buy)
                    newSl = signal.EntryPrice - minDist;
                else if (signal.Side == SignalSide.Sell)
                    newSl = signal.EntryPrice + minDist;

                dist = minDist;
            }

            // 1) низкий шум + тренд → чуть поджать SL (но не ниже MinAtrSL)
            if ((decision.Trend == "UP" || decision.Trend == "DOWN") &&
                decision.AtrPct < 0.0015m &&         // < 0.15 %
                dist > atr14 * 0.5m)
            {
                decimal tighten = dist * 0.30m;
                decimal candidateDist = dist - tighten;

                if (candidateDist < minDist)
                    candidateDist = minDist;

                if (signal.Side == SignalSide.Buy)
                    newSl = signal.EntryPrice - candidateDist;
                else
                    newSl = signal.EntryPrice + candidateDist;
            }

            // 2) анти-стопхант по хвостам
            decimal upperWick = last.HighPrice - Math.Max(last.OpenPrice, last.ClosePrice);
            decimal lowerWick = Math.Min(last.OpenPrice, last.ClosePrice) - last.LowPrice;

            if (signal.Side == SignalSide.Buy && lowerWick > atr14 * 1.2m)
            {
                var candidate = last.LowPrice - atr14 * 0.2m;
                // кандидат может расширить SL, это не запрещено
                if (candidate < newSl) newSl = candidate;
            }
            else if (signal.Side == SignalSide.Sell && upperWick > atr14 * 1.2m)
            {
                var candidate = last.HighPrice + atr14 * 0.2m;
                if (candidate > newSl) newSl = candidate;
            }

            // ещё раз гарантируем минимальную дистанцию после хвостов
            decimal finalDist = Math.Abs(signal.EntryPrice - newSl);
            if (finalDist < minDist)
            {
                if (signal.Side == SignalSide.Buy)
                    newSl = signal.EntryPrice - minDist;
                else if (signal.Side == SignalSide.Sell)
                    newSl = signal.EntryPrice + minDist;
            }

            if (newSl != oldSl)
            {
                _logger.LogInformation(
                    "AI-SL OPTIMIZER {Symbol}: oldSL={Old:F4}, newSL={New:F4}, trend={Trend}, atr%={AtrPct:P2}",
                    symbol, oldSl, newSl, decision.Trend, decision.AtrPct);
            }

            // RR-чек (логирование, отмену сигнала делает StrategyEngine)
            bool rrOk = CheckMinRiskReward(signal, newSl);
            if (!rrOk)
            {
                _logger.LogWarning(
                    "AI-SL OPTIMIZER {Symbol}: RR < {MinRr}: entry={Entry:F4}, sl={Sl:F4}",
                    symbol,
                    _options.MinRiskReward <= 0 ? 2.0m : _options.MinRiskReward,
                    signal.EntryPrice,
                    newSl);
            }

            return newSl;
        }

        /// <summary>
        /// Упрощённый overload для PositionSupervisor:
        /// Глобально: SL минимум MinAtrSlMult * ATR, без RR (нет TP).
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

            // === Минимальный SL по ATR ===
            decimal minDist = atr14 * MinAtrSlMult;
            decimal dist = Math.Abs(entryPrice - newSl);

            if (dist < minDist)
            {
                if (side == SignalSide.Buy)
                    newSl = entryPrice - minDist;
                else if (side == SignalSide.Sell)
                    newSl = entryPrice + minDist;

                dist = minDist;
            }

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

            // финальная проверка MinAtrSL после хвостов
            decimal finalDist = Math.Abs(entryPrice - newSl);
            if (finalDist < minDist)
            {
                if (side == SignalSide.Buy)
                    newSl = entryPrice - minDist;
                else if (side == SignalSide.Sell)
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
