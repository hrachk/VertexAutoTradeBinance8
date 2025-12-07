// ============================================================================
// ORDER EXECUTOR v6.5 — Smart ENTRY + Correct SL/TP
// - ENTRY: Limit (или Market, если нужно будет добавить режим)
// - SL: STOP-LIMIT, reduceOnly, безопасная дистанция от mark
// - TP: LIMIT reduceOnly (классический профит-ордер)
// - Binance.Net 11.11.0 совместимость
// - PositionSide Long/Short корректный (Hedge-ready)
// - Работает с готовыми SL/TP из TradeSignal (StrategyEngine + AiStopLossOptimizer)
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

            // --- 0) Фильтры и tickSize ---
            var filters = await _symbolInfo.GetFuturesFiltersAsync(signal.Symbol);
            var tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            // =================================================================
            // 1) ОСНОВНОЙ LIMIT ENTRY
            // =================================================================
            var entryOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: signal.Symbol,
                side: side,
                type: FuturesOrderType.Limit,
                quantity: quantity,
                price: RoundPrice(signal.EntryPrice, tick),
                positionSide: posSide,
                workingType: WorkingType.Mark,
                timeInForce: TimeInForce.GoodTillCanceled,
                ct: ct);

            if (!entryOrder.Success || entryOrder.Data == null)
            {
                _logger.LogError("[ORDER][{symbol}] ENTRY LIMIT ERROR: {err}", signal.Symbol, entryOrder.Error);
                return OrderResult.Fail(entryOrder.Error?.Message ?? "LIMIT_ERROR");
            }

            var placed = entryOrder.Data;
            decimal entryPrice = placed.AveragePrice > 0 ? placed.AveragePrice : signal.EntryPrice;
            entryPrice = RoundPrice(entryPrice, tick);

            // Получаем mark для защиты от мгновенного триггера
            var mark = await GetMarkPriceSafeAsync(client, signal.Symbol, entryPrice, ct);

            // =================================================================
            // 2) ИСХОДНЫЕ SL / TP ИЗ SIGNAL (после AiStopLossOptimizer)
            // =================================================================
            decimal slPrice = signal.StopLoss;
            decimal tpPrice = 0m;

            if (signal.TakeProfits != null && signal.TakeProfits.Count > 0)
                tpPrice = signal.TakeProfits[0];
            else if (signal.TakeProfit.HasValue && signal.TakeProfit.Value > 0)
                tpPrice = signal.TakeProfit.Value;

            // --- fallback, если сломался сигнал ---
            if (slPrice <= 0 && signal.Atr.HasValue && signal.Atr.Value > 0)
            {
                var atr = signal.Atr.Value;
                slPrice = signal.Side == SignalSide.Buy
                    ? entryPrice - atr * 1.5m
                    : entryPrice + atr * 1.5m;
            }

            if (tpPrice <= 0 && signal.Atr.HasValue && signal.Atr.Value > 0)
            {
                var atr = signal.Atr.Value;
                tpPrice = signal.Side == SignalSide.Buy
                    ? entryPrice + atr * 2.0m
                    : entryPrice - atr * 2.0m;
            }

            // --- защита: SL/TP должны быть по разные стороны от entry ---
            if (signal.Side == SignalSide.Buy)
            {
                if (slPrice >= entryPrice)
                    slPrice = entryPrice - Math.Abs(entryPrice * 0.003m); // минимум ~0.3% ниже

                if (tpPrice <= entryPrice)
                    tpPrice = entryPrice + Math.Abs(entryPrice * 0.006m); // минимум ~0.6% выше
            }
            else
            {
                if (slPrice <= entryPrice)
                    slPrice = entryPrice + Math.Abs(entryPrice * 0.003m);

                if (tpPrice >= entryPrice)
                    tpPrice = entryPrice - Math.Abs(entryPrice * 0.006m);
            }

            // --- округляем по tickSize ---
            slPrice = RoundPrice(slPrice, tick);
            tpPrice = RoundPrice(tpPrice, tick);

            // --- доп. защита от мгновенного триггера относительно mark ---
            if (mark > 0)
            {
                if (signal.Side == SignalSide.Buy)
                {
                    if (slPrice >= mark)
                        slPrice = RoundPrice(mark - 3 * tick, tick);

                    if (tpPrice <= mark)
                        tpPrice = RoundPrice(mark + 5 * tick, tick);
                }
                else
                {
                    if (slPrice <= mark)
                        slPrice = RoundPrice(mark + 3 * tick, tick);

                    if (tpPrice >= mark)
                        tpPrice = RoundPrice(mark - 5 * tick, tick);
                }
            }

            // limit-цена для STOP-LIMIT SL
            decimal slLimitPrice = signal.Side == SignalSide.Buy
                ? RoundPrice(slPrice - tick, tick)
                : RoundPrice(slPrice + tick, tick);

            var closeSide = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

            _logger.LogInformation(
                "[ORDER][{symbol}] ENTRY OK: entry={Entry}, SL={Sl}, SL_limit={SlLimit}, TP={Tp}, qty={Qty}",
                signal.Symbol, entryPrice, slPrice, slLimitPrice, tpPrice, quantity);

            // =================================================================
            // 3) SL: STOP-LIMIT reduceOnly
            // =================================================================
            var slOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: signal.Symbol,
                side: closeSide,
                type: FuturesOrderType.Stop,
                quantity: quantity,
                price: slLimitPrice,
                stopPrice: slPrice,
                positionSide: posSide,
                reduceOnly: true,
                workingType: WorkingType.Mark,
                timeInForce: TimeInForce.GoodTillCanceled,
                ct: ct);

            if (!slOrder.Success || slOrder.Data == null)
            {
                _logger.LogError("[ORDER][{symbol}] SL CREATE ERROR: {err}", signal.Symbol, slOrder.Error);
                return OrderResult.Fail(slOrder.Error?.Message ?? "SL_ERROR");
            }

            // =================================================================
            // 4) TP: LIMIT reduceOnly
            // =================================================================
            var tpOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: signal.Symbol,
                side: closeSide,
                type: FuturesOrderType.Limit,
                quantity: quantity,
                price: tpPrice,
                positionSide: posSide,
                reduceOnly: true,
                workingType: WorkingType.Mark,
                timeInForce: TimeInForce.GoodTillCanceled,
                ct: ct);

            if (!tpOrder.Success || tpOrder.Data == null)
            {
                _logger.LogError("[ORDER][{symbol}] TP CREATE ERROR: {err}", signal.Symbol, tpOrder.Error);
                return OrderResult.Fail(tpOrder.Error?.Message ?? "TP_ERROR");
            }

            _logger.LogInformation(
                "[ORDER][{symbol}] TP/SL SET: SL={Sl}, TP={Tp}, qty={Qty}",
                signal.Symbol, slPrice, tpPrice, quantity);

            return OrderResult.Successs(entryPrice, quantity, placed.Id);
        }

        // =====================================================================
        // HELPERS
        // =====================================================================
        private static decimal RoundPrice(decimal price, decimal tick)
        {
            if (tick <= 0) return price;
            return Math.Round(price / tick) * tick;
        }

        private static async Task<decimal> GetMarkPriceSafeAsync(
            BinanceRestClient client,
            string symbol,
            decimal fallback,
            CancellationToken ct)
        {
            try
            {
                var r = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(symbol, ct: ct);
                if (r.Success && r.Data != null && r.Data.Price > 0)
                    return r.Data.Price;
            }
            catch
            {
            }

            return fallback > 0 ? fallback : 0m;
        }
    }
}
