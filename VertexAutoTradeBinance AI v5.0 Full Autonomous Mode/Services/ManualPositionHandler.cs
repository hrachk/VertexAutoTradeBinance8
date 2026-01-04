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
    /// <summary>
    /// ManualPositionHandler (PATCHED)
    ///
    /// Fixes:
    /// - Idempotency guard: manual signal is created ONLY ONCE per (symbol + side + abs(qty) + entry)
    /// - Prev-state updated for close detector use-cases
    /// - Memory anti-loop: if last signal is manual, do not recreate it every tick
    ///
    /// Notes:
    /// - Does NOT change PositionSupervisorService logic.
    /// - Does NOT block AI signals globally; only prevents manual spam.
    /// </summary>
    public class ManualPositionHandler
    {
        private readonly TradeSignalMemoryService _memory;

        // =======================================================================
        // STORAGE FOR PREVIOUS POSITION STATE (qty, entry)
        // Needed for detecting position close in PositionSupervisor
        // =======================================================================

        private readonly Dictionary<string, (decimal Qty, decimal Entry)> _prevState
            = new Dictionary<string, (decimal Qty, decimal Entry)>();

        private readonly Dictionary<string, DateTime> _lastStop = new();

        // =======================================================================
        // MANUAL IDEMPOTENCY GUARD
        // One manual "virtual signal" per unique position fingerprint.
        // =======================================================================

        private readonly Dictionary<string, DateTime> _manualHandled = new();

        // Tolerances to avoid float noise from exchange values
        private const decimal EntryEps = 0.0001m;
        private const decimal QtyEps = 0.0001m;

        public ManualPositionHandler(TradeSignalMemoryService memory)
        {
            _memory = memory;
        }

        public void RegisterStop(string symbol)
        {
            _lastStop[symbol] = DateTime.UtcNow;
        }

        public DateTime? GetLastStop(string symbol)
        {
            return _lastStop.TryGetValue(symbol, out var t) ? t : null;
        }

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

        /// <summary>
        /// Конвертация Binance позиции → TradeSignal
        /// </summary>
        public TradeSignal? ConvertManualToSignal(BinancePositionDetailsUsdt pos)
        {
            if (pos == null)
                return null;

            // Binance.Net 11.11.0 → поле Quantity
            decimal absQty = Math.Abs(pos.Quantity);
            if (absQty <= 0)
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

            // Keep original behavior (memory) but do it once in DetectManualAsync
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
            if (client == null)
                return null;

            var posRes = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, null, ct);
            if (!posRes.Success || posRes.Data == null)
                return null;

            var pos = posRes.Data.FirstOrDefault(p => Math.Abs(p.Quantity) > 0);
            if (pos == null)
                return null;

            // Update prev-state for close detector consumers (key is whatever caller uses; here we use symbol)
            // If your supervisor uses a different key format, keep its own key, but this still helps.
            SetPrevState(symbol, pos.Quantity, pos.EntryPrice);

            // If we already have a manual as the last signal, do not recreate it every tick
            var last = _memory.GetLastSignal(symbol);
            if (last != null && last.IsManual)
                return null;

            // Idempotency fingerprint for the currently open manual position
            // PositionSide may exist in BinancePositionDetailsUsdt; if not, we still have sign via Quantity.
            var side = pos.Quantity > 0 ? SignalSide.Buy : SignalSide.Sell;
            var absQty = Math.Abs(pos.Quantity);

            // normalize to reduce jitter
            var qtyNorm = Math.Round(absQty, 4);
            var entryNorm = Math.Round(pos.EntryPrice, 6);

            var fingerprint = $"{symbol}:{side}:{qtyNorm}:{entryNorm}";

            if (_manualHandled.ContainsKey(fingerprint))
                return null;

            // Additional safety: if last manual exists but differs only by tiny eps, treat as same
            // (covers cases where last manual wasn't "last" due to other signals being saved)
            if (last != null && last.IsManual)
            {
                var sameSide = last.Side == side;
                var sameQty = Math.Abs(Math.Abs(last.EntryPrice) - entryNorm) <= EntryEps;
                // NOTE: qty isn't stored in TradeSignal by default; if you have it, add here.
                if (sameSide && sameQty)
                    return null;
            }

            // Create signal ONCE
            var manual = ConvertManualToSignal(pos);
            if (manual == null)
                return null;

            // Save only once
            _manualHandled[fingerprint] = DateTime.UtcNow;

            Console.WriteLine($"[MANUAL][{symbol}] qty={pos.Quantity} detected — virtual signal created");

            _memory.Save(manual);

            return manual;
        }

        public bool IsNewManualPosition(BinancePositionDetailsUsdt pos, TradeSignal? last)
        {
            if (pos == null || pos.Quantity == 0)
                return false;

            // Если сигнала нет — это новая ручная позиция
            if (last == null)
                return true;

            // Если последний сигнал не manual — считаем, что это ручная позиция только если entry другой
            if (!last.IsManual)
                return Math.Abs(last.EntryPrice - pos.EntryPrice) > EntryEps;

            // Если бот знал старую manual позицию, но пользователь открыл новую (entry другой)
            if (Math.Abs(last.EntryPrice - pos.EntryPrice) > EntryEps)
                return true;

            return false;
        }
    }
}
