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
            // RISK FIX 2026-08: floors подняты — убираем SL из зоны noise на crypto futures
            bool strongTrend = trend == "UP" || trend == "DOWN";
            bool ultraLowVol = atrPct < 0.0010m;    // <0.10%
            bool lowVol = atrPct < 0.0020m;    // <0.20%
            bool highVol = atrPct > 0.0040m;    // >0.40%

            if (strongTrend)
            {
                if (ultraLowVol)
                    return 1.2m;   // было 1.0 — даже в тренде не лезем в шум
                if (lowVol)
                    return 1.35m;  // было 1.2
                if (highVol)
                    return 1.7m;   // было 1.5
                return 1.45m;      // было 1.3
            }
            else
            {
                // Range / Squeeze — ещё шире
                if (ultraLowVol)
                    return 1.5m;   // было 1.4
                if (highVol)
                    return 2.0m;   // было 1.8

                return 1.7m;       // было 1.6
            }
        }

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



            // =======================
            // Динамическая настройка SL
            // =======================
            decimal dynMult = GetDynamicSlAtrMult(decision.Trend, decision.AtrPct);
            decimal minDist = atr14 * dynMult;
            decimal currentDist = Math.Abs(signal.EntryPrice - newSl);
            if (currentDist < minDist)
            {
                // Расширяем SL до нужной глубины
                if (signal.Side == SignalSide.Buy)
                    newSl = signal.EntryPrice - minDist;
                else
                    newSl = signal.EntryPrice + minDist;
            }

            // =======================
            // HARD FLOOR: никогда не отдаём SL ближе 1.0 ATR (crypto noise floor)
            // =======================
            decimal hardFloor = atr14 * 1.0m;
            decimal distNow = Math.Abs(signal.EntryPrice - newSl);
            if (distNow < hardFloor && atr14 > 0)
            {
                if (signal.Side == SignalSide.Buy)
                    newSl = signal.EntryPrice - hardFloor;
                else
                    newSl = signal.EntryPrice + hardFloor;
            }

            // =======================
            // Динамическая настройка TP (под расширенный SL, RR ≈ 1.8–2.2)
            // =======================
            decimal slDist = Math.Abs(signal.EntryPrice - newSl);
            decimal tpDist = Math.Max(atr14 * 2.0m, slDist * 1.8m); // не хуже ~1.8R
            decimal tp = signal.Side == SignalSide.Buy
                ? signal.EntryPrice + tpDist
                : signal.EntryPrice - tpDist;

            // Записываем TP в сигнал
            signal.TakeProfit = tp;
            if (signal.TakeProfits == null)
                signal.TakeProfits = new List<decimal>();
            if (signal.TakeProfits.Count == 0)
                signal.TakeProfits.Add(tp);
            else
                signal.TakeProfits[0] = tp;

            signal.StopLoss = newSl;

            _logger.LogInformation(
                "AI-SL/TP Updated: Symbol={Symbol}, oldSL={Old:F4}, newSL={New:F4}, TP={Tp:F4}, atr={Atr:F4}, trend={Trend}, dynMult={Mult:F2}, slDistAtr={Dist:F2}",
                symbol, oldSl, newSl, tp, atr14, decision.Trend, dynMult, atr14 > 0 ? slDist / atr14 : 0m);

            return newSl;
        }

    }
}
