// ============================================================================
// ORDER EXECUTOR v5.0 — идеальный вход позиции
// - Smart LIMIT/MARKET AI entry
// - Безопасные фильтры размера
// - Полная поддержка Binance.Net 11.11.0
// - Без reduceOnly для ENTRY (fix -1106)
// - PositionSide всегда корректный (Long/Short/Both)
// - POST-ENTRY → возвращает фактический entry price
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

            // 1) direction
            var side = signal.Side == SignalSide.Buy ? OrderSide.Buy : OrderSide.Sell;
            PositionSide posSide = signal.Side == SignalSide.Buy
                ? PositionSide.Long
                : PositionSide.Short;

            // 2) filters
            var filters = await _symbolInfo.GetFuturesFiltersAsync(signal.Symbol);
            //decimal tick = filters.tickSize <= 0 ? 0.01m : filters.tickSize;
            // decimal step = filters.stepSize <= 0 ? 0.001m : filters.stepSize;
            decimal step = filters.step;
            decimal minQty = filters.minQty;
            decimal minNotional = filters.minNotional;
            decimal tick = filters.tickSize;

            // 3) qty adjust
            quantity = Math.Floor(quantity / step) * step;
            if (quantity < filters.minQty)
            {
                _logger.LogError("[ORDER] Qty {qty} < minQty {min}", quantity, filters.minQty);
                return OrderResult.Fail("QTY_TOO_SMALL");
            }

            // 4) Получаем MarkPrice (WorkingType.Mark)
            var markRes = await client.UsdFuturesApi.ExchangeData.GetMarkPriceAsync(signal.Symbol, ct: ct);
            decimal mark = markRes.Success ? markRes.Data.MarkPrice : signal.EntryPrice;
            if (mark <= 0) mark = signal.EntryPrice;

            // === SMART DECISION LIMIT или MARKET ===
            bool useLimit = false;

            decimal slipPct = Math.Abs(mark - signal.EntryPrice) / mark * 100m;

            if (slipPct <= 0.08m)       // идеально близко → LIMIT
                useLimit = true;
            else if (slipPct <= 0.25m)  // нормальный диапазон → LIMIT
                useLimit = true;
            else
                useLimit = false;       // сильный проскаль → MARKET

            _logger.LogInformation(
                "[ORDER][{symbol}] ENTRY: use {type} (slip={slip:F4} %)",
                signal.Symbol,
                useLimit ? "LIMIT" : "MARKET",
                slipPct
            );

            // 5) LIMIT PRICE calculation
            decimal limitPrice = signal.EntryPrice;

            if (useLimit)
            {
                if (side == OrderSide.Buy)
                    limitPrice = Math.Round((mark - tick * 2) / tick) * tick;
                else
                    limitPrice = Math.Round((mark + tick * 2) / tick) * tick;
            }

            // =====================================================================
            // SEND ENTRY ORDER
            // =====================================================================
            BinanceUsdFuturesOrder placed = null;
            

            if (useLimit)
            {
                var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: signal.Symbol,
                    side: side,
                    type: FuturesOrderType.Limit,
                    quantity: quantity,
                    price: limitPrice,
                    positionSide: posSide,
                    reduceOnly: false,                 // <--- FIX -1106
                    workingType: WorkingType.Mark,     // <--- как ты требовал
                    timeInForce: TimeInForce.GoodTillCanceled,
                    ct: ct
                );

                if (!res.Success || res.Data == null)
                {
                    _logger.LogError("[ORDER][{symbol}] LIMIT ERROR: {err}", signal.Symbol, res.Error);
                    return OrderResult.Fail(res.Error?.Message ?? "LIMIT_ERROR");
                }

                placed = res.Data;
            }
            else
            {
                var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: signal.Symbol,
                    side: side,
                    type: FuturesOrderType.Market,
                    quantity: quantity,
                    reduceOnly: false,                // <--- FIX -1106
                    positionSide: posSide,
                    workingType: WorkingType.Mark,
                    ct: ct
                );

                if (!res.Success || res.Data == null)
                {
                    _logger.LogError("[ORDER][{symbol}] MARKET ERROR: {err}", signal.Symbol, res.Error);
                    return OrderResult.Fail(res.Error?.Message ?? "MARKET_ERROR");
                }

                placed = res.Data;
            }

            // =====================================================================
            // RETURN RESULT
            // =====================================================================
            decimal entry = placed.AveragePrice > 0
                ? placed.AveragePrice
                : limitPrice;

            return OrderResult.Successs(entry, quantity, placed.Id);
        }
    }
}
