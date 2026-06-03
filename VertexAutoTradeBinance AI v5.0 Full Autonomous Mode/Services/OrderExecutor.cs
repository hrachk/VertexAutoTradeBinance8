// ============================================================================
// - Если ордер Filled/позиция появилась → возвращаем SUCCESS,
//   а SL/TP СТАВИТ PositionSupervisorService v8.1 (NORMAL → ALGO RAW)
// - OrderExecutor НЕ ставит SL/TP вообще.

// ============================================================================

using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
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
        private readonly LiquidityGuardService _liquidityGuard;

        public OrderExecutor(
            ILogger<OrderExecutor> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            SimulatedTradeService simulator,
            ExecutedSignalService executedSignalService,
            MarketDataService marketData,
            AiMarketRegimeService marketRegimeService,
            SmartRegimeService smartRegime,
            LiquidityGuardService liquidityGuard)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
            _simulator = simulator;
            _executedSignalService = executedSignalService;
            _marketData = marketData;
            _marketRegimeService = marketRegimeService;
            _smartRegime = smartRegime;
            _liquidityGuard = liquidityGuard;
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

            // =====================================================================
            // 0.1) ЛИМИТ НА КОЛ-ВО ОТКРЫТЫХ ПОЗИЦИЙ (АККАУНТ-ВАЙД)
            // =====================================================================
            var posResBefore = await client.UsdFuturesApi.Trading.GetPositionsAsync(null, ct: ct);
            if (posResBefore.Success && posResBefore.Data != null)
            {
                var openPositionsCount = posResBefore.Data.Count(p => p.PositionAmt != 0m);
                if (openPositionsCount >= 2)
                {
                    _logger.LogWarning(
                        "[ORDER][{symbol}] POSITION LIMIT REACHED: openPositions={cnt} → skip new entry",
                        signal.Symbol, openPositionsCount);

                    await _simulator.SimulateMissedTradeAsync(signal, "TooManyOpenPositions");
                    return OrderResult.Fail("Too many open positions (>=2)");
                }
            }

            // =============================================================
            // 0) Regime / SmartRegime → UI / analytics
            // =============================================================
            var klines = await _marketData.GetKlines(signal.Symbol, KlineInterval.FiveMinutes, 200);
            var baseReg = _marketRegimeService.DetectRegime(signal.Symbol, KlineInterval.FiveMinutes, klines);
            var smart = _smartRegime.Evaluate(signal.Symbol, KlineInterval.FiveMinutes, klines);

            // =====================================================================
            // MARKET ENTRY HARD FILTER (FINAL)  (ONLY ADD / FIX, NO LOGIC BREAK)
            // =====================================================================

            // 1) SmartStrongTrend
            bool isSmartStrongTrend =
                smart.BaseRegime == MarketRegime.StrongUpTrend ||
                smart.BaseRegime == MarketRegime.StrongDownTrend;

            // 2) Impulse
            bool hasImpulse = IsImpulse(klines, signal.Atr ?? 0m);

            // 3) RR CHECK
            decimal rr = 0m;
            if (signal.StopLoss > 0 && signal.TakeProfits?.Any() == true)
            {
                var risk = Math.Abs(signal.EntryPrice - signal.StopLoss);
                var reward = signal.TakeProfits.Max(tp => Math.Abs(tp - signal.EntryPrice));
                if (risk > 0) rr = reward / risk;
            }
            const decimal MIN_MARKET_RR = 1.8m;
            bool rrOk = rr >= MIN_MARKET_RR;

            // 4) LiquidityGuard (REAL API)
            LiquidityGuardResult liquidityResult;
            try
            {
                liquidityResult = _liquidityGuard.Analyze(
                    signal.Symbol,
                    KlineInterval.FiveMinutes,
                    klines,
                    signal.Side,
                    superSignal: false
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LiquidityGuard] Analyze failed → fail-safe allow");
                liquidityResult = new LiquidityGuardResult(false, LiquidityGuardReason.None, "AnalyzeFailed");
            }

            bool liquiditySafe = !liquidityResult.Block;

            // === FINAL DECISION ===
            bool allowMarketEntry =
                isSmartStrongTrend &&
                smart.AllowAggressiveTrendEntries &&
                hasImpulse &&
                rrOk &&
                liquiditySafe;

            if (allowMarketEntry)
            {
                _logger.LogWarning(
                    "[ORDER][{symbol}] MARKET ENTRY ENABLED | SmartStrongTrend + Impulse | RR={rr:F2} | Liquidity=OK",
                    signal.Symbol, rr
                );
            }
            else
            {
                _logger.LogInformation(
                    "[ORDER][{symbol}] MARKET ENTRY BLOCKED | trend={trend} impulse={imp} rr={rr:F2} liquidityBlock={block} reason={reason}",
                    signal.Symbol,
                    smart.BaseRegime,
                    hasImpulse,
                    rr,
                    liquidityResult.Block,
                    liquidityResult.Reason
                );
            }

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
            // 1) ENTRY (LIMIT / MARKET) — БЕЗ reduceOnly
            // =====================================================================
            var entryRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: signal.Symbol,
                side: side,
                type: allowMarketEntry ? FuturesOrderType.Market : FuturesOrderType.Limit,
                quantity: quantity,
                price: allowMarketEntry ? null : entryPrice,
                positionSide: posSide,
                timeInForce: allowMarketEntry ? null : TimeInForce.GoodTillCanceled,
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
            // 2) WAIT-POSITION/ORDER — dual-track (ORDER + POSITION)
            // =====================================================================
            var wait = await WaitForPositionOrOrderAsync(
                client,
                signal,
                posSide,
                entryOrderId,
                entryPrice,
                quantity,
                ct);

            if (!wait.HasPosition)
            {
                _logger.LogError(
                    "[ORDER][{symbol}] ENTRY FAIL — {reason}",
                    signal.Symbol, wait.Reason);

                await _simulator.SimulateMissedTradeAsync(signal, wait.Reason ?? "EntryNotFilled");

                try
                {
                    await client.UsdFuturesApi.Trading.CancelOrderAsync(signal.Symbol, entryOrderId, ct: ct);
                }
                catch { }

                return OrderResult.Fail(wait.Reason ?? "ENTRY_NOT_FILLED");
            }

            // Если мы здесь — позиция реально открыта
            entryPrice = wait.EntryPrice;
            quantity = wait.Qty;

            // =====================================================================
            // 2.1) ПОВТОРНАЯ ПРОВЕРКА ЛИМИТА ПОЗИЦИЙ (ПОСЛЕ ОТКРЫТИЯ)
            // =====================================================================
            var posResAfter = await client.UsdFuturesApi.Trading.GetPositionsAsync(null, ct: ct);
            if (posResAfter.Success && posResAfter.Data != null)
            {
                var openPositionsCount = posResAfter.Data.Count(p => p.PositionAmt != 0m);
                if (openPositionsCount > 2)
                {
                    _logger.LogWarning(
                        "[ORDER][{symbol}] POSITION LIMIT BREACHED AFTER OPEN: openPositions={cnt}",
                        signal.Symbol, openPositionsCount);
                    // здесь можно, по желанию, сразу закрыть эту новую позицию,
                    // но пока только логируем
                }
            }

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
            await _simulator.SimulateMissedTradeAsync(signal, wait.Reason ?? "Opened");
            // =====================================================================
            // 2.5) IMMEDIATE HARD TP (один TP сразу после открытия позиции)
            // =====================================================================
            decimal tpPrice = signal.TakeProfit ?? 0m;
            if (tpPrice <= 0 && signal.TakeProfits != null && signal.TakeProfits.Count > 0)
                tpPrice = signal.TakeProfits[0];

            if (tpPrice > 0)
            {
                tpPrice = Round(tpPrice, tick);
                var tpSide = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

                var tpRes = await client.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
                    symbol: signal.Symbol,
                    side: tpSide,
                    type: ConditionalOrderType.TakeProfitMarket,
                    quantity: quantity,
                    price: null,
                    positionSide: posSide,
                    timeInForce: TimeInForce.GoodTillCanceled,
                    reduceOnly: true,
                    clientOrderId: null,
                    triggerPrice: tpPrice,
                    activationPrice: null,
                    callbackRate: null,
                    workingType: WorkingType.Mark,
                    closePosition: null,
                    priceProtect: null,
                    priceMatch: null,
                    selfTradePreventionMode: null,
                    goodTillDate: null,
                    receiveWindow: null,
                    ct: ct
                );

                if (!tpRes.Success)
                {
                    _logger.LogError(
                        "[ORDER][{symbol}] TP PLACE ERROR: price={tp}, err={err}",
                        signal.Symbol, tpPrice, tpRes.Error
                    );
                }
                else
                {
                    _logger.LogInformation(
                        "[ORDER][{symbol}] TP PLACED: tp={tp}, algoId={id}",
                        signal.Symbol, tpPrice, tpRes.Data.Id
                    );
                }
            }

            // =====================================================================
            // 3) COMPUTE SL / TP (ONLY FOR ANALYTICS / SUPERVISOR)
            // =====================================================================
            decimal atr = signal.Atr ?? 0m;
            decimal sl = signal.StopLoss;

            decimal tp = signal.TakeProfit ?? 0;
            if (tp <= 0 && signal.TakeProfits != null && signal.TakeProfits.Count > 0)
                tp = signal.TakeProfits[0];

            if (atr > 0)
            {
                sl = Round(sl, tick);
                if (tp > 0)
                    tp = Round(tp, tick);
            }

            _logger.LogWarning(
                "[ORDER][{symbol}] PROTECTION: SL={sl}, TP={tp}. TP-ордер уже поставлен сразу после открытия позиции.",
                signal.Symbol, sl, tp
            );

            return OrderResult.Successs(entryPrice, quantity, entryOrderId);
        }


        // =====================================================================
        // WAIT FOR POSITION or ORDER FILL (dual-track)
        // =====================================================================
        private async Task<(bool HasPosition, decimal EntryPrice, decimal Qty, string Reason)> WaitForPositionOrOrderAsync(
            BinanceRestClient client,
            TradeSignal signal,
            PositionSide posSide,
            long entryOrderId,
            decimal fallbackEntry,
            decimal requestedQty,
            CancellationToken ct)
        {

            const int maxLoops = 60;           // 60 * 500ms ~ 30s
            const int delayMs = 500;
            const decimal maxSlipPct = 0.004m; // 0.4% допуск до "улетела цена"

            decimal lastExecuted = 0m;
            bool runawayLogged = false; // чтобы не спамить в логах

            for (int i = 0; i < maxLoops; i++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    // ---- 1) Читаем ордер ----
                    var ordRes = await client.UsdFuturesApi.Trading.GetOrderAsync(signal.Symbol, entryOrderId, ct: ct);
                    OrderStatus? status = null;
                    decimal executedQty = 0m;
                    decimal avgPrice = fallbackEntry;

                    if (ordRes.Success && ordRes.Data != null)
                    {
                        status = ordRes.Data.Status;
                        executedQty = ordRes.Data.QuantityFilled;

                        avgPrice = ordRes.Data.AveragePrice > 0
                            ? ordRes.Data.AveragePrice
                            : fallbackEntry;

                        if (executedQty > 0 && executedQty != lastExecuted)
                        {
                            lastExecuted = executedQty;
                            _logger.LogInformation(
                                "[ORDER][{symbol}] Partial fill: {exec}/{total}",
                                signal.Symbol, executedQty, ordRes.Data.Quantity);
                        }

                        if (status == OrderStatus.Canceled ||
                            status == OrderStatus.Rejected ||
                            status == OrderStatus.Expired)
                        {
                            _logger.LogWarning(
                                "[ORDER][{symbol}] Order cancelled/rejected/expired with exec={exec}",
                                signal.Symbol, executedQty);

                            // Если вообще ничего не залили → считаем пропущенной
                            if (executedQty <= 0)
                                return (false, 0m, 0m, "OrderCanceled");

                            // Если была частичная заливка → переходим к позиции
                        }
                    }

                    // ---- 2) Читаем позицию ----
                    var posRes = await client.UsdFuturesApi.Account.GetPositionInformationAsync(signal.Symbol, null, ct);
                    if (posRes.Success && posRes.Data != null)
                    {
                        var pos = posRes.Data
                            .FirstOrDefault(p =>
                                p.Symbol == signal.Symbol &&
                                p.PositionSide == posSide &&
                                p.Quantity != 0m);

                        if (pos != null)
                        {
                            var qty = Math.Abs(pos.Quantity);
                            var entry = pos.EntryPrice > 0 ? pos.EntryPrice : avgPrice;

                            _logger.LogInformation(
                                "[ORDER][{symbol}] Position detected: side={side}, qty={qty}, entry={entry}",
                                signal.Symbol, posSide, qty, entry);

                            return (true, entry, qty, "PositionOpened");
                        }
                    }

                    // ---- 3) Проверка "цена улетела" — НЕ ФАТАЛ, НЕ CANCEL ----
                    if (lastExecuted <= 0)
                    {
                        try
                        {
                            var priceRes = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(signal.Symbol, ct: ct);
                            if (priceRes.Success && priceRes.Data != null && priceRes.Data.Price > 0)
                            {
                                var mark = priceRes.Data.Price;
                                decimal diffPct;

                                if (posSide == PositionSide.Long)
                                {
                                    diffPct = (mark - fallbackEntry) / fallbackEntry;
                                    if (diffPct >= maxSlipPct && !runawayLogged)
                                    {
                                        runawayLogged = true;
                                        _logger.LogWarning(
                                            "[ORDER][{symbol}] PRICE RUN AWAY (LONG) → keep LIMIT alive: entry={e}, mark={m}, diff={d:P2}",
                                            signal.Symbol, fallbackEntry, mark, diffPct);

                                        // НЕ отменяем ордер, просто ждём дальше
                                    }
                                }
                                else // Short
                                {
                                    diffPct = (fallbackEntry - mark) / fallbackEntry;
                                    if (diffPct >= maxSlipPct && !runawayLogged)
                                    {
                                        runawayLogged = true;
                                        _logger.LogWarning(
                                            "[ORDER][{symbol}] PRICE RUN AWAY (SHORT) → keep LIMIT alive: entry={e}, mark={m}, diff={d:P2}",
                                            signal.Symbol, fallbackEntry, mark, diffPct);
                                    }
                                }
                            }
                        }
                        catch (Exception exPrice)
                        {
                            _logger.LogWarning(exPrice, "[ORDER][{symbol}] Error reading mark price", signal.Symbol);
                        }
                    }

                    await Task.Delay(delayMs, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ORDER][{symbol}] Error in WaitForPositionOrOrder loop", signal.Symbol);
                    await Task.Delay(delayMs, ct);
                }
            }

            // ---- 4) После цикла ещё раз проверяем ордер + позицию ----
            try
            {
                var ordRes = await client.UsdFuturesApi.Trading.GetOrderAsync(signal.Symbol, entryOrderId, ct: ct);
                OrderStatus? status = null;
                decimal executedQty = 0m;
                decimal avgPrice = fallbackEntry;

                if (ordRes.Success && ordRes.Data != null)
                {
                    status = ordRes.Data.Status;
                    executedQty = ordRes.Data.QuantityFilled;

                    avgPrice = ordRes.Data.AveragePrice > 0
                        ? ordRes.Data.AveragePrice
                        : fallbackEntry;

                    _logger.LogWarning(
                        "[ORDER][{symbol}] After wait: status={st}, exec={exec}",
                        signal.Symbol, status, executedQty);
                }

                var posRes = await client.UsdFuturesApi.Account.GetPositionInformationAsync(signal.Symbol, null, ct);
                if (posRes.Success && posRes.Data != null)
                {
                    var pos = posRes.Data
                        .FirstOrDefault(p =>
                            p.Symbol == signal.Symbol &&
                            p.PositionSide == posSide &&
                            p.Quantity != 0m);

                    if (pos != null)
                    {
                        var qty = Math.Abs(pos.Quantity);
                        var entry = pos.EntryPrice > 0 ? pos.EntryPrice : avgPrice;

                        _logger.LogInformation(
                            "[ORDER][{symbol}] Position detected AFTER wait: side={side}, qty={qty}, entry={entry}",
                            signal.Symbol, posSide, qty, entry);

                        return (true, entry, qty, "PositionOpenedAfterWait");
                    }
                }

                // Если сюда дошли → позиции нет
                try
                {
                    await client.UsdFuturesApi.Trading.CancelOrderAsync(signal.Symbol, entryOrderId, ct: ct);
                }
                catch { }

                if (executedQty > 0)
                {
                    _logger.LogError(
                        "[ORDER][{symbol}] EXECUTED QTY > 0, но позиция не обнаружена. entry={e}, exec={exec}",
                        signal.Symbol, avgPrice, executedQty);
                    return (false, 0m, 0m, "OrderExecutedButNoPosition");
                }

                return (false, 0m, 0m, "TimeoutNoFill");
            }
            catch (Exception exFinal)
            {
                _logger.LogError(exFinal, "[ORDER][{symbol}] Fatal in WaitForPositionOrOrderAsync", signal.Symbol);
                return (false, 0m, 0m, "WaitFatalError");
            }
        }


        // =====================================================================
        // ROUND UTIL
        // =====================================================================
        private static decimal Round(decimal value, decimal tick)
        {
            return Math.Round(value / tick) * tick;
        }

        private static bool IsImpulse(
        IReadOnlyList<BinanceFuturesUsdtKline> klines,
        decimal atr,
        decimal minBodyAtr = 0.8m) // можно тюнить
        {
            if (klines == null || klines.Count < 2 || atr <= 0)
                return false;

            var last = klines[^1];

            var body = Math.Abs(last.ClosePrice - last.OpenPrice);

            return body >= atr * minBodyAtr;
        }
    }
}
