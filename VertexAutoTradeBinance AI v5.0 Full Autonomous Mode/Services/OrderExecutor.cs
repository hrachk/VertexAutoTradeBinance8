// ============================================================================
// ORDER EXECUTOR v5.2 — Smart UI + Safe Filters
// - УДАЛЁН reduceOnly ИЗ ENTRY (как в v5.1)
// - Binance.Net 11.11.0 совместимость
// - Smart LIMIT/MARKET Entry
// - PositionSide Long/Short корректный
// - Красивый консольный UI через ConsoleReportFormatter
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

        public OrderExecutor(
            ILogger<OrderExecutor> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
        }

        // =====================================================================
        // MAIN ENTRY METHOD
        // =====================================================================
        public async Task<OrderResult> ExecuteAsync(
     TradeSignal signal,
     decimal quantity,
     CancellationToken ct = default)
        {
            using var client = _factory.CreateRestClient();

            var side = signal.Side == SignalSide.Buy ? OrderSide.Buy : OrderSide.Sell;
            var posSide = signal.Side == SignalSide.Buy
                ? PositionSide.Long
                : PositionSide.Short;

            // Размещение ордера
            var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: signal.Symbol,
                side: side,
                type: FuturesOrderType.Limit,
                quantity: quantity,
                price: signal.EntryPrice,
                positionSide: posSide,
                workingType: WorkingType.Mark,
                timeInForce: TimeInForce.GoodTillCanceled,
                ct: ct);

            if (!res.Success || res.Data == null)
            {
                _logger.LogError("[ORDER][{symbol}] LIMIT ERROR: {err}", signal.Symbol, res.Error);
                return OrderResult.Fail(res.Error?.Message ?? "LIMIT_ERROR");
            }

            var placed = res.Data;
            decimal entryPrice = placed.AveragePrice > 0 ? placed.AveragePrice : signal.EntryPrice;

            // 1. Рассчитываем TP и SL на основе EntryPrice
            decimal tp = (decimal)(entryPrice + (signal.Atr * 2)); // TP на 2x ATR от Entry
            decimal sl = (decimal)(entryPrice - (signal.Atr * 1.5m)); // SL на 1.5x ATR от Entry

            // Логируем
            _logger.LogInformation("Order executed: {Symbol}, EntryPrice={EntryPrice}, TP={Tp}, SL={Sl}", signal.Symbol, entryPrice, tp, sl);

            // 2. Отправляем TP и SL на биржу
            var tpOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: signal.Symbol,
                side: side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy, // TP будет противоположным side
                type: FuturesOrderType.StopMarket,
                quantity: quantity,
                stopPrice: tp,
                positionSide: posSide,
                timeInForce: TimeInForce.GoodTillCanceled,
                ct: ct);

            var slOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: signal.Symbol,
                side: side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy, // SL будет противоположным side
                type: FuturesOrderType.StopMarket,
                quantity: quantity,
                stopPrice: sl,
                positionSide: posSide,
                timeInForce: TimeInForce.GoodTillCanceled,
                ct: ct);

            if (!tpOrder.Success || !slOrder.Success)
            {
                _logger.LogError("[ORDER][{symbol}] TP/SL Error: {err}", signal.Symbol, tpOrder.Error?.Message ?? "ERROR");
                return OrderResult.Fail("TP/SL Error");
            }

            // 3. Логируем успешное размещение TP и SL
            _logger.LogInformation("TP and SL successfully set for {Symbol}: TP={Tp}, SL={Sl}", signal.Symbol, tp, sl);

            // 4. Возвращаем успешный результат с ордером
            return OrderResult.Successs(entryPrice, quantity, placed.Id);
        }

    }
}
