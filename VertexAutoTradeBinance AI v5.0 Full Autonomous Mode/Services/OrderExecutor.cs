// ============================================================================
// ORDER EXECUTOR v6.6 — QUANT-GRADE ENTRY MODULE
// - ENTRY: Limit / Market
// - WAIT-FILL: до открытия позиции
// - SL: STOP-LIMIT  (reduceOnly=true)
// - TP: LIMIT        (reduceOnly=true)
// - Корректные tickSize / stepSize округления
// - Интеграция с AiStopLossOptimizer dynamic RR
// - Абсолютная совместимость с Supervisor v6.1
// ============================================================================

using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class OrderExecutor
    {
        private readonly ILogger<OrderExecutor> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly SymbolInfoService _symbolInfo;
        private readonly SimulatedTradeService _simulator;
        public OrderExecutor(
            ILogger<OrderExecutor> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            SimulatedTradeService simulator)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
            _simulator = simulator;
        }

        // =====================================================================
        // MAIN ENTRY
        // =====================================================================
        public async Task<OrderResult> ExecuteAsync(
            TradeSignal signal,
            decimal quantity,
            CancellationToken ct = default)
        {
            using var client = _factory.CreateRestClient();

            var filters = await _symbolInfo.GetFuturesFiltersAsync(signal.Symbol);
            var tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;
            var step = filters.step <= 0 ? 0.0001m : filters.step;

            // Округление количества
            quantity = Math.Floor(quantity / step) * step;
            if (quantity <= 0)
            {
                // Пропущенная сделка, вызываем симуляцию
                await _simulator.SimulateMissedTradeAsync(signal, "QuantityTooSmall");
                return OrderResult.Fail("Quantity too small");
            }
              

            var side = signal.Side == SignalSide.Buy ? OrderSide.Buy : OrderSide.Sell;
            var posSide = signal.Side == SignalSide.Buy ? PositionSide.Long : PositionSide.Short;

            decimal entryPrice = Round(signal.EntryPrice, tick);

            // =====================================================================
            // 1) ENTRY (LIMIT) — НИКАКИХ reduceOnly
            // =====================================================================
            var entryRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: signal.Symbol,
                side: side,
                type: FuturesOrderType.Limit,
                quantity: quantity,
                price: entryPrice,
                positionSide: posSide,
                workingType: WorkingType.Mark,
                timeInForce: TimeInForce.GoodTillCanceled,
                ct: ct);

            if (!entryRes.Success || entryRes.Data == null)
            {
                // В случае ошибки ордера, вызываем симуляцию
                await _simulator.SimulateMissedTradeAsync(signal, "EntryError");

                _logger.LogError("[ORDER][{symbol}] ENTRY ERROR: {err}",
                    signal.Symbol, entryRes.Error);
                return OrderResult.Fail(entryRes.Error?.Message ?? "ENTRY_ERROR");
            }

            long entryOrderId = entryRes.Data.Id;
            _logger.LogInformation("[ORDER][{symbol}] ENTRY OK: id={id}, price={price}, qty={qty}",
                signal.Symbol, entryOrderId, entryPrice, quantity);

            // =====================================================================
            // 2) WAIT-FILL — ждем, пока позиция реально откроется
            // =====================================================================
            decimal filledEntry = await WaitForFillAsync(client, signal.Symbol, entryOrderId, entryPrice, ct);

            if (filledEntry <= 0)
            { 
                // В случае неисполнения, вызываем симуляцию
                await _simulator.SimulateMissedTradeAsync(signal, "EntryNotFilled");

                _logger.LogError("[ORDER][{symbol}] ENTRY NOT FILLED — CANCELING", signal.Symbol);
                await client.UsdFuturesApi.Trading.CancelOrderAsync(signal.Symbol, entryOrderId, ct: ct);
                return OrderResult.Fail("ENTRY_NOT_FILLED");
            }

            entryPrice = filledEntry;
            _logger.LogInformation("[ORDER][{symbol}] ENTRY FILLED AT {price}", signal.Symbol, entryPrice);

            // =====================================================================
            // 3) COMPUTE DYNAMIC SL/TP (ATR, trend, volatility)
            // =====================================================================
            decimal atr = signal.Atr ?? 0;
            decimal sl = signal.StopLoss;
            decimal tp = signal.TakeProfit ?? 0;

            if (atr > 0)
            {
                // Dynamic SL from trade signal (AiStopLossOptimizer already updated)
                sl = Round(sl, tick);

                // Dynamic TP (RR based)
                if (tp > 0)
                    tp = Round(tp, tick);
            }

            _logger.LogInformation(
                "[ORDER][{symbol}] ENTRY FILLED → SL={sl}, TP={tp}, qty={qty}",
                signal.Symbol, sl, tp, quantity);

            // =====================================================================
            // 4) CREATE SL (STOP-LIMIT)
            // =====================================================================
            var slLimit = posSide == PositionSide.Long ? sl - tick : sl + tick;
            slLimit = Round(slLimit, tick);

            var slOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: signal.Symbol,
                side: side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy,
                type: FuturesOrderType.Stop,
                quantity: quantity,
                price: slLimit,
                stopPrice: sl,
                reduceOnly: true,
                positionSide: posSide,
                timeInForce: TimeInForce.GoodTillCanceled,
                ct: ct);

            if (!slOrder.Success)
            {
                _logger.LogError("[ORDER][{symbol}] SL CREATE ERROR: {err}", signal.Symbol, slOrder.Error);
                return OrderResult.Fail("SL_CREATE_ERROR");
            }

            _logger.LogInformation("[ORDER][{symbol}] SL OK: stop={sl}, limit={limit}",
                signal.Symbol, sl, slLimit);

            // =====================================================================
            // 5) CREATE TP (LIMIT)
            // =====================================================================
            var tpOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: signal.Symbol,
                side: side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy,
                type: FuturesOrderType.TakeProfit,
                quantity: quantity,
                price: tp,
                stopPrice: null,
                reduceOnly: true,
                positionSide: posSide,
                timeInForce: TimeInForce.GoodTillCanceled,
                ct: ct);

            if (!tpOrder.Success)
            {
                _logger.LogError("[ORDER][{symbol}] TP CREATE ERROR: {err}", signal.Symbol, tpOrder.Error);
                return OrderResult.Fail("TP_CREATE_ERROR");
            }

            _logger.LogInformation("[ORDER][{symbol}] TP OK: price={tp}", signal.Symbol, tp);

            return OrderResult.Successs(entryPrice, quantity, entryOrderId);
        }

        // =====================================================================
        // WAIT FOR ENTRY FILL
        // =====================================================================
        private async Task<decimal> WaitForFillAsync(
            BinanceRestClient client,
            string symbol,
            long orderId,
            decimal fallbackPrice,
            CancellationToken ct)
        {
            for (int i = 0; i < 60; i++) // 15 секунд максимум
            {
                ct.ThrowIfCancellationRequested();

                var r = await client.UsdFuturesApi.Trading.GetOrderAsync(
                    symbol, orderId, ct: ct);

                if (r.Success && r.Data != null)
                {
                    if (r.Data.Status == OrderStatus.Filled)
                        return r.Data.AveragePrice > 0 ? r.Data.AveragePrice : fallbackPrice;
                }

                await Task.Delay(250, ct);
            }

            return -1;
        }

        // =====================================================================
        // ROUND UTIL
        // =====================================================================
        private static decimal Round(decimal value, decimal tick)
        {
            return Math.Round(value / tick) * tick;
        }
    }
}
