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

            // -----------------------------------------------------------------
            // 1) EXCHANGE FILTERS
            // -----------------------------------------------------------------
            var filters = await _symbolInfo.GetFuturesFiltersAsync(signal.Symbol);

            decimal step = filters.step <= 0 ? 0.001m : filters.step;
            decimal tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            // шаг
            quantity = Math.Floor(quantity / step) * step;
            if (quantity < filters.minQty)
            {
                _logger.LogError("[ORDER] Qty {qty} < minQty {min}", quantity, filters.minQty);
                ConsoleReportFormatter.EntryFailedHard(_logger, signal.Symbol,
                    $"QTY {quantity} < minQty {filters.minQty}");
                return OrderResult.Fail("QTY_TOO_SMALL");
            }

            // 🔥 НОВОЕ: защита по minNotional, чтобы не ловить -4164
            decimal notional = quantity * signal.EntryPrice;
            if (filters.minNotional > 0 && notional < filters.minNotional)
            {
                decimal needQty = filters.minNotional / signal.EntryPrice;
                needQty = Math.Ceiling(needQty / step) * step;

                _logger.LogWarning(
                    "[ORDER] Notional {notional:F2} < minNotional {minNotional} → bump qty {oldQty} → {newQty}",
                    notional,
                    filters.minNotional,
                    quantity,
                    needQty);

                quantity = needQty;
            }

            // -----------------------------------------------------------------
            // 2) MARK PRICE / SLIPPAGE
            // -----------------------------------------------------------------
            var markRes = await client.UsdFuturesApi.ExchangeData.GetMarkPriceAsync(signal.Symbol, ct: ct);
            decimal mark = (markRes.Success && markRes.Data != null)
                ? markRes.Data.MarkPrice
                : signal.EntryPrice;

            if (mark <= 0)
                mark = signal.EntryPrice;

            decimal slipPct = mark > 0
                ? Math.Abs(mark - signal.EntryPrice) / mark * 100m
                : 0m;

            bool useLimit = slipPct <= 0.25m; // как в v5.1 — LIMIT, если не сильно уехали

            _logger.LogInformation(
                "[ORDER][{symbol}] ENTRY: use {type} (slip={slip:F4} %)",
                signal.Symbol,
                useLimit ? "LIMIT" : "MARKET",
                slipPct);

            // -----------------------------------------------------------------
            // 3) LIMIT PRICE
            // -----------------------------------------------------------------
            decimal limitPrice = signal.EntryPrice;

            if (useLimit)
            {
                // Чуть лучше поджимаем к текущему mark, не заходя за рынок
                if (side == OrderSide.Buy)
                {
                    var raw = mark - tick * 2;
                    limitPrice = Math.Round(raw / tick) * tick;
                    if (limitPrice <= 0)
                        limitPrice = signal.EntryPrice; // fallback
                }
                else
                {
                    var raw = mark + tick * 2;
                    limitPrice = Math.Round(raw / tick) * tick;
                    if (limitPrice <= 0)
                        limitPrice = signal.EntryPrice;
                }
            }

            // Красивый отчёт по подготовке входа (SL сейчас только из сигнала)
            ConsoleReportFormatter.EntryPrep(
                _logger,
                signal.Symbol,
                side == OrderSide.Buy ? "LONG" : "SHORT",
                signal.EntryPrice,
                signal.StopLoss,
                signal.StopLoss,
                quantity,
                step,
                tick);

            BinanceUsdFuturesOrder? placed = null;

            try
            {
                // =================================================================
                // 4) SEND ENTRY (без reduceOnly)
                // =================================================================
                if (useLimit)
                {
                    var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: signal.Symbol,
                        side: side,
                        type: FuturesOrderType.Limit,
                        quantity: quantity,
                        price: limitPrice,
                        positionSide: posSide,
                        workingType: WorkingType.Mark,
                        timeInForce: TimeInForce.GoodTillCanceled,
                        ct: ct);

                    if (!res.Success || res.Data == null)
                    {
                        _logger.LogError("[ORDER][{symbol}] LIMIT ERROR: {err}", signal.Symbol, res.Error);
                        ConsoleReportFormatter.EntryFailedHard(
                            _logger,
                            signal.Symbol,
                            res.Error?.Message ?? "LIMIT_ERROR");
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
                        positionSide: posSide,
                        workingType: WorkingType.Mark,
                        ct: ct);

                    if (!res.Success || res.Data == null)
                    {
                        _logger.LogError("[ORDER][{symbol}] MARKET ERROR: {err}", signal.Symbol, res.Error);
                        ConsoleReportFormatter.EntryFailedHard(
                            _logger,
                            signal.Symbol,
                            res.Error?.Message ?? "MARKET_ERROR");
                        return OrderResult.Fail(res.Error?.Message ?? "MARKET_ERROR");
                    }

                    placed = res.Data;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ORDER][{symbol}] EXCEPTION on entry", signal.Symbol);
                ConsoleReportFormatter.EntryFailedHard(_logger, signal.Symbol, ex.Message);
                return OrderResult.Fail(ex.Message);
            }

            // -----------------------------------------------------------------
            // 5) RESULT
            // -----------------------------------------------------------------
            decimal entry = placed!.AveragePrice > 0
                ? placed.AveragePrice
                : limitPrice;

            ConsoleReportFormatter.EntrySuccess(
                _logger,
                signal.Symbol,
                quantity,
                entry,
                attempt: 1);

            return OrderResult.Successs(entry, quantity, placed.Id);
        }
    }
}
