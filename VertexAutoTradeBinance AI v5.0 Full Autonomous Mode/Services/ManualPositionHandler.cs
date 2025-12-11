using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class ManualPositionHandler
    {
        private readonly TradeSignalMemoryService _memory;

        // =======================================================================
        //   STORAGE FOR PREVIOUS POSITION STATE (qty, entry)
        //   Needed for detecting position close in PositionSupervisor
        // =======================================================================

        private readonly Dictionary<string, (decimal Qty, decimal Entry)> _prevState
            = new Dictionary<string, (decimal Qty, decimal Entry)>();

        /// <summary>
        /// Получить предыдущее количество (для close detector)
        /// </summary>
        public decimal GetPrevQty(string key)
        {
            if (_prevState.TryGetValue(key, out var st))
                return st.Qty;

            return 0;
        }

        /// <summary>
        /// Получить предыдущий entry price
        /// </summary>
        public decimal GetPrevEntry(string key)
        {
            if (_prevState.TryGetValue(key, out var st))
                return st.Entry;

            return 0;
        }

        /// <summary>
        /// Сохранить новое состояние позиции
        /// </summary>
        public void SetPrevState(string key, decimal qty, decimal entry)
        {
            _prevState[key] = (qty, entry);
        }

        public ManualPositionHandler(TradeSignalMemoryService memory)
        {
            _memory = memory;
        }

        /// <summary>
        /// Конвертация Binance позиции → TradeSignal
        /// </summary>
        public TradeSignal? ConvertManualToSignal(BinancePositionDetailsUsdt pos)
        {
            // Binance.Net 11.11.0 → поле PositionAmt
            decimal qty = Math.Abs(pos.Quantity);

            if (qty <= 0)
                return null;

            SignalSide side = pos.Quantity > 0 ? SignalSide.Buy : SignalSide.Sell;

            var signal = new TradeSignal
            {
                Symbol = pos.Symbol,
                Side = side,
                EntryPrice = pos.EntryPrice,
                StopLoss = 0,
                TakeProfits = new(),
                Time = DateTime.UtcNow,
                Timeframe = "Manual",
                Reason = "MANUAL_POSITION",
                IsManual = true,
            };

            _memory.Save(signal);
            return signal;
        }


        /// <summary>
        /// Полная автоматическая проверка ручных позиций
        /// </summary>
        public async Task<TradeSignal?> DetectManualAsync(
            BinanceRestClient client,
            string symbol,
            CancellationToken ct)
        {
            var posRes = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, null, ct);
            if (!posRes.Success || posRes.Data == null)
                return null;

            var pos = posRes.Data.FirstOrDefault(p => Math.Abs(p.Quantity) > 0);
            if (pos == null)
                return null;

            // Memory НЕ блокирует (важно!)
            var last = _memory.GetLastSignal(symbol);
            if (last != null && !last.IsManual)
                return null;

            // Создаём сигнал
            var manual = ConvertManualToSignal(pos);

            if (manual != null)
            {
                Console.WriteLine($"[MANUAL][{symbol}] qty={pos.Quantity} detected — virtual signal created");
                _memory.Save(manual);
            }

            return manual;
        }
        public bool IsNewManualPosition(BinancePositionDetailsUsdt pos, TradeSignal? last)
        {
            if (pos == null || pos.Quantity == 0)
                return false;

            // Если сигнала нет — это новая ручная позиция
            if (last == null)
                return true;

            // Если бот знал старую позицию, но пользователь открыл новую
            if (Math.Abs(last.EntryPrice - pos.EntryPrice) > 0.0001m)
                return true;

            return false;
        }

    }
}
