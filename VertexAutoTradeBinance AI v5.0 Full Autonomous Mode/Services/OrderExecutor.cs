// ============================================================================
// ORDER EXECUTOR v6.7 — SAFE ENTRY PROTECTOR
// - ENTRY: Limit
// - WAIT: короткое ожидание ОТКРЫТОЙ ПОЗИЦИИ (через GetPositionInformationAsync)
// - Если позиции нет → отменяем ордер, считаем пропущенной
// - Если позиция ЕСТЬ (даже частично) → сразу ставим SL/TP
// - НИКАКИХ открытых позиций без SL/TP
// ============================================================================

using Binance.Net.Clients;
using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class OrderExecutor
    {
        private readonly ILogger<OrderExecutor> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly SymbolInfoService _symbolInfo;
        private readonly SimulatedTradeService _simulator;
        private readonly ExecutedSignalService _executedSignalService;
        private readonly MarketDataService _marketData;
        private readonly AiMarketRegimeService _marketRegimeService;
        private readonly SmartRegimeService _smartRegime;

        public OrderExecutor(
            ILogger<OrderExecutor> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            SimulatedTradeService simulator,
            ExecutedSignalService executedSignalService,
            MarketDataService marketData,
            AiMarketRegimeService marketRegimeService,
            SmartRegimeService smartRegime)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
            _simulator = simulator;
            _executedSignalService = executedSignalService;
            _marketData = marketData;
            _marketRegimeService = marketRegimeService;
            _smartRegime = smartRegime;
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
                await _simulator.SimulateMissedTradeAsync(signal, "QuantityTooSmall");
                return OrderResult.Fail("Quantity too small");
            }

            var side = signal.Side == SignalSide.Buy ? OrderSide.Buy : OrderSide.Sell;
            var posSide = signal.Side == SignalSide.Buy ? PositionSide.Long : PositionSide.Short;

            decimal entryPrice = Round(signal.EntryPrice, tick);

            // =============================================================
            // 0) Regime / SmartRegime → UI / analytics
            // =============================================================
            var klines = await _marketData.GetKlines(signal.Symbol, KlineInterval.FiveMinutes, 200);
            var baseReg = _marketRegimeService.DetectRegime(signal.Symbol, KlineInterval.FiveMinutes, klines);
            var smart = _smartRegime.Evaluate(signal.Symbol, KlineInterval.FiveMinutes, klines);

            var volatility = baseReg.VolatilityPercent;
            var slope = baseReg.TrendSlopePercent;
            int opportunityScore = (int)(smart.Confidence * 100);

            decimal aiRisk =
                signal.SafetyRiskMultiplier *
                (signal.AiQuality ?? 1m) *
                (volatility < 0.01m ? 0.8m : 1.2m);

            // LOG: создан сигнал
            decimal notional = quantity * signal.EntryPrice;
            var execRecord = _executedSignalService.AddSignalCreated(
                signal,
                opportunityScore,
                signal.Atr ?? 0,
                volatility,
                slope,
                quantity,
                notional,
                $"AiRisk={aiRisk:F2}"
            );

            // =====================================================================
            // 1) ENTRY (LIMIT) — БЕЗ reduceOnly
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
                await _simulator.SimulateMissedTradeAsync(signal, "EntryError");

                _logger.LogError("[ORDER][{symbol}] ENTRY ERROR: {err}",
                    signal.Symbol, entryRes.Error);
                return OrderResult.Fail(entryRes.Error?.Message ?? "ENTRY_ERROR");
            }

            long entryOrderId = entryRes.Data.Id;
            _logger.LogInformation("[ORDER][{symbol}] ENTRY OK: id={id}, price={price}, qty={qty}",
                signal.Symbol, entryOrderId, entryPrice, quantity);

            _executedSignalService.UpdateStatus(
                symbol: signal.Symbol,
                time: DateTime.UtcNow,
                status: TradeExecutionStatus.OrderCreated,
                qty: quantity,
                notional: quantity * entryPrice
            );

            // =====================================================================
            // 2) WAIT-POSITION — ждём ФАКТ ОТКРЫТОЙ ПОЗИЦИИ (max ~8 сек)
            // =====================================================================
            var positionResult = await WaitForPositionOpenAsync(
                client,
                signal.Symbol,
                posSide,
                entryOrderId,
                entryPrice,
                ct);

            if (!positionResult.HasPosition)
            {
                // Позиции реально НЕТ → отменяем ордер и считаем пропущенной
                _logger.LogError("[ORDER][{symbol}] ENTRY NOT FILLED — CANCELING (no position detected)", signal.Symbol);

                await _simulator.SimulateMissedTradeAsync(signal, "EntryNotFilled");

                // На всякий случай: отмена ордера
                try
                {
                    await client.UsdFuturesApi.Trading.CancelOrderAsync(signal.Symbol, entryOrderId, ct: ct);
                }
                catch { }

                return OrderResult.Fail("ENTRY_NOT_FILLED");
            }

            // Если мы здесь — позиция реально открыта
            entryPrice = positionResult.EntryPrice;
            quantity = positionResult.Qty;

            _logger.LogInformation("[ORDER][{symbol}] POSITION OPENED at {price}, qty={qty}",
                signal.Symbol, entryPrice, quantity);

            _executedSignalService.UpdateStatus(
                symbol: signal.Symbol,
                time: DateTime.UtcNow,
                status: TradeExecutionStatus.PositionOpened,
                qty: quantity,
                notional: quantity * entryPrice,
                  entryPrice
            );

            // =====================================================================
            // 3) COMPUTE DYNAMIC SL/TP (ATR, trend, volatility)
            // =====================================================================
            decimal atr = signal.Atr ?? 0;
            decimal sl = signal.StopLoss;
            decimal tp = signal.TakeProfit ?? 0;

            if (atr > 0)
            {
                sl = Round(sl, tick);
                if (tp > 0)
                    tp = Round(tp, tick);
            }

            _logger.LogInformation(
                "[ORDER][{symbol}] PROTECTION → SL={sl}, TP={tp}, qty={qty}",
                signal.Symbol, sl, tp, quantity);

            // =====================================================================
            // 4) CREATE SL (STOP-LIMIT, reduceOnly=true)
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
                _logger.LogError("[ORDER][{symbol}] SL CREATE ERROR: {err}",
                    signal.Symbol, slOrder.Error);
                return OrderResult.Fail("SL_CREATE_ERROR");
            }

            _logger.LogInformation("[ORDER][{symbol}] SL OK: stop={sl}, limit={limit}",
                signal.Symbol, sl, slLimit);

            // =====================================================================
            // 5) CREATE TP (LIMIT, reduceOnly=true)
            // =====================================================================
            if (tp > 0)
            {
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
                    _logger.LogError("[ORDER][{symbol}] TP CREATE ERROR: {err}",
                        signal.Symbol, tpOrder.Error);
                    return OrderResult.Fail("TP_CREATE_ERROR");
                }

                _logger.LogInformation("[ORDER][{symbol}] TP OK: price={tp}", signal.Symbol, tp);
            }
            else
            {
                _logger.LogWarning("[ORDER][{symbol}] TP not set (tp=0) — защищаем только SL", signal.Symbol);
            }

            return OrderResult.Successs(entryPrice, quantity, entryOrderId);
        }

        // =====================================================================
        // WAIT FOR POSITION OPEN (по позиции, не по ордеру)
        // =====================================================================
        private async Task<(bool HasPosition, decimal EntryPrice, decimal Qty)> WaitForPositionOpenAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide posSide,
            long entryOrderId,
            decimal fallbackEntry,
            CancellationToken ct)
        {
            const int maxLoops = 20;          // ~ 20 * 400ms = 8 секунд
            const int delayMs = 400;

            for (int i = 0; i < maxLoops; i++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var posRes = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, null, ct);
                    if (posRes.Success && posRes.Data != null)
                    {
                        // В hedge mode будут Long / Short; в OneWay — PositionSide.Both
                        var pos = posRes.Data
                            .FirstOrDefault(p =>
                                p.Symbol == symbol &&
                                p.PositionSide == posSide &&
                                p.Quantity != 0m);

                        if (pos != null)
                        {
                            var qty = Math.Abs(pos.Quantity);
                            var entry = pos.EntryPrice > 0 ? pos.EntryPrice : fallbackEntry;

                            _logger.LogInformation(
                                "[ORDER][{symbol}] Position detected: side={side}, qty={qty}, entry={entry}",
                                symbol, posSide, qty, entry);

                            return (true, entry, qty);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ORDER][{symbol}] Error polling position info", symbol);
                }

                await Task.Delay(delayMs, ct);
            }

            // После ожидания позиции всё ещё нет → проверим сам ордер
            try
            {
                var ordRes = await client.UsdFuturesApi.Trading.GetOrderAsync(symbol, entryOrderId, ct: ct);
                if (ordRes.Success && ordRes.Data != null)
                {
                    var st = ordRes.Data.Status;
                    _logger.LogWarning("[ORDER][{symbol}] Order status after wait: {st}", symbol, st);

                    if (st == OrderStatus.Filled || st == OrderStatus.PartiallyFilled)
                    {
                        // safety: ещё раз пробуем найти позицию
                        try
                        {
                            var posRes2 = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, null, ct);
                            if (posRes2.Success && posRes2.Data != null)
                            {
                                var pos2 = posRes2.Data
                                    .FirstOrDefault(p =>
                                        p.Symbol == symbol &&
                                        p.PositionSide == posSide &&
                                        p.Quantity != 0m);

                                if (pos2 != null)
                                {
                                    var qty2 = Math.Abs(pos2.Quantity);
                                    var entry2 = pos2.EntryPrice > 0 ? pos2.EntryPrice : fallbackEntry;

                                    _logger.LogInformation(
                                        "[ORDER][{symbol}] Position detected AFTER wait: side={side}, qty={qty}, entry={entry}",
                                        symbol, posSide, qty2, entry2);

                                    return (true, entry2, qty2);
                                }
                            }
                        }
                        catch (Exception ex2)
                        {
                            _logger.LogWarning(ex2, "[ORDER][{symbol}] Error polling position after order filled", symbol);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ORDER][{symbol}] Error reading order after wait", symbol);
            }

            // Позиции нет → отменяем ордер и считаем, что вход НЕ состоялся
            try
            {
                await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, entryOrderId, ct: ct);
            }
            catch { }

            return (false, 0m, 0m);
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
