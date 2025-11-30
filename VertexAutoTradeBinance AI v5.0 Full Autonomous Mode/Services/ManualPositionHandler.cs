using System;
using System.Threading.Tasks;
using VertexAutoTradeBinance8.Models;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;

namespace VertexAutoTradeBinance8.Services
{
    public class ManualPositionHandler
    {
        private readonly TradeSignalMemoryService _memory;

        public ManualPositionHandler(TradeSignalMemoryService memory)
        {
            _memory = memory;
        }

        public TradeSignal? ConvertManualToSignal(BinancePositionDetailsUsdt position)
        {
            if (position == null || position.Quantity == 0)
                return null;

            var side = position.Quantity > 0 ? SignalSide.Buy : SignalSide.Sell;

            var signal = new TradeSignal
            {
                Symbol = position.Symbol,
                Side = side,
                EntryPrice = position.EntryPrice,
                StopLoss = 0,                // SL потом посчитает AiStopLossOptimizer
                TakeProfits = new(),
                Time = DateTime.UtcNow,
                Timeframe = "Manual",
                Atr = null,
                Reason = "MANUAL_POSITION",
                IsManual = true,             // 🔥 ключевой флаг
                AiQuality = null             // пока не одобрено AI
            };

            // Сохраняем, но дальше AI сможет отфильтровать manual
            _memory.Save(signal);

            return signal;
        }
    }
}
