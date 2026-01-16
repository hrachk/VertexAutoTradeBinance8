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
        private readonly AiSelfLearningService _ai;

        // ===== ENTRY EXECUTION TUNING (PRODUCTION) =====
        private const int ENTRY_WAIT_SECONDS = 18;              // было 30s (60*500ms) в Wait...; здесь логика для fallback
        private const decimal AGGR_LIMIT_OFFSET_PCT = 0.0006m;  // 0.06% агрессивный лимит (тюнится)
        private const decimal MARKET_FALLBACK_MAX_SLIP_PCT = 0.0015m; // 0.15% макс. слип для fallback-market


        public OrderExecutor(
            ILogger<OrderExecutor> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            SimulatedTradeService simulator,
            ExecutedSignalService executedSignalService,
            MarketDataService marketData,
            AiMarketRegimeService marketRegimeService,
            SmartRegimeService smartRegime,
            LiquidityGuardService liquidityGuard, AiSelfLearningService ai)
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
            _ai = ai;
        }

        private static decimal NormalizeToStep(decimal value, decimal step)
        {
            if (step <= 0) return value;
            return Math.Floor(value / step) * step;
        }
 
        private static int DecimalsFromStep(decimal step)
        {
            var s = step.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var i = s.IndexOf('.');
            return i < 0 ? 0 : s.Length - i - 1;
        }

        private static decimal Quantize(decimal value, decimal step)
        {
            if (step <= 0) return value;

            var decimals = DecimalsFromStep(step);
            var rounded = Math.Floor(value / step) * step;

            // 🔒 КЛЮЧ: финальное округление ПО КОЛ-ВУ ЗНАКОВ
            return Math.Round(rounded, decimals, MidpointRounding.ToZero);
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

            // =============================================================
            // SYMBOL FILTERS
            // =============================================================
            var filters = await _symbolInfo.GetFuturesFiltersAsync(signal.Symbol);
            var tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;
            var step = filters.step <= 0 ? 0.0001m : filters.step;

            quantity = Quantize(quantity, step);

            if (quantity <= 0 || quantity < filters.minQty)
            {
                var reason = "QTY_TOO_SMALL";

                await _simulator.SimulateMissedTradeAsync(signal, reason);

                var rec = _executedSignalService.AddSignalCreated(
                    signal,
                    opportunityScore: 0,
                    atr: signal.Atr ?? 0m,
                    volatility: 0m,
                    slope: 0m,
                    qty: 0m,
                    notional: 0m,
                    tags: reason
                );

                _executedSignalService.UpdateStatus(
                    signal.Symbol,
                    rec.Time,
                    TradeExecutionStatus.Blocked,
                    0,
                    0
                );

                return OrderResult.Fail(reason);
            }


            var side = signal.Side == SignalSide.Buy ? OrderSide.Buy : OrderSide.Sell;
            var posSide = signal.Side == SignalSide.Buy ? PositionSide.Long : PositionSide.Short;

            decimal entryPrice = Quantize(signal.EntryPrice, tick);

            // =============================================================
            // LAST PRICE (SAFE, RATE-LIMIT AWARE)
            // =============================================================
            decimal lastPrice = entryPrice;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(800));

                var px = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(
                    signal.Symbol, ct: cts.Token);

                if (px.Success && px.Data != null && px.Data.Price > 0)
                    lastPrice = px.Data.Price;
            }
            catch { /* fallback to entryPrice */ }

            // =============================================================
            // AGGRESSIVE LIMIT PRICE
            // =============================================================
            decimal aggrLimitPrice;
            if (side == OrderSide.Buy)
                aggrLimitPrice = Quantize(Math.Max(entryPrice, lastPrice * 1.001m), tick);
            else
                aggrLimitPrice = Quantize(Math.Min(entryPrice, lastPrice * 0.999m), tick);

            // =============================================================
            // REGIME / SMART REGIME
            // =============================================================
            var klines = await _marketData.GetKlines(signal.Symbol, KlineInterval.FiveMinutes, 200);
            var baseReg = _marketRegimeService.DetectRegime(signal.Symbol, KlineInterval.FiveMinutes, klines);
            var smart = _smartRegime.Evaluate(signal.Symbol, KlineInterval.FiveMinutes, klines);

            bool isSmartStrongTrend =
                smart.BaseRegime == MarketRegime.StrongUpTrend ||
                smart.BaseRegime == MarketRegime.StrongDownTrend;

            bool hasImpulse = IsImpulse(klines, signal.Atr ?? 0m);

            // =============================================================
            // RR CHECK (MAX TP)
            // =============================================================
            decimal rr = 0m;
            if (signal.StopLoss > 0 && signal.TakeProfits?.Any() == true)
            {
                var risk = Math.Abs(signal.EntryPrice - signal.StopLoss);
                var reward = signal.TakeProfits.Max(tp => Math.Abs(tp - signal.EntryPrice));
                if (risk > 0) rr = reward / risk;
            }

            bool rrOk = rr >= 1.8m;

            // =============================================================
            // LIQUIDITY GUARD (FAIL-SAFE)
            // =============================================================
            LiquidityGuardResult liquidityResult;

            try
            {
                liquidityResult = _liquidityGuard.Analyze(
                    signal.Symbol,
                    KlineInterval.FiveMinutes,
                    klines,
                    signal.Side,
                    superSignal: signal.IsSuperSignal
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[LiquidityGuard] Analyze failed → FAIL-SAFE ALLOW ({symbol})",
                    signal.Symbol
                );

                liquidityResult = new LiquidityGuardResult(
                    Block: false,
                    Reason: LiquidityGuardReason.None,
                    IsExtreme: false,
                    Details: "AnalyzeFailed",
                    UtcTime: DateTime.UtcNow
                );
            }

            bool liquiditySafe = !liquidityResult.Block;



            // =============================================================
            // MARKET ENTRY DECISION
            // =============================================================
            bool allowMarketEntry =
                isSmartStrongTrend &&
                smart.AllowAggressiveTrendEntries &&
                hasImpulse &&
                rrOk &&
                liquiditySafe;

            // 🔥 HIGH CONFIDENCE OVERRIDE
            if (!allowMarketEntry && smart.Confidence >= 0.82m && liquiditySafe)
            {
                allowMarketEntry = true;
                _logger.LogWarning(
                    "[ORDER][{symbol}] MARKET OVERRIDE by HIGH CONFIDENCE conf={conf}",
                    signal.Symbol, smart.Confidence);
            }

            // =============================================================
            // EXECUTED SIGNAL CREATED
            // =============================================================
            decimal notional = quantity * entryPrice;
            var execRec = _executedSignalService.AddSignalCreated(
                signal,
                opportunityScore: (int)(smart.Confidence * 100),
                atr: signal.Atr ?? 0m,
                volatility: baseReg.VolatilityPercent,
                slope: baseReg.TrendSlopePercent,
                qty: quantity,
                notional: notional,
                tags: $"Regime={smart.BaseRegime}");

            var execTime = execRec.Time;

            // =============================================================
            // HARD BLOCK: LIQUIDITY
            // =============================================================
            if (liquidityResult.Block)
            {
                await _simulator.SimulateMissedTradeAsync(
                    signal,
                    $"LiquidityGuard:{liquidityResult.Reason}");

                _executedSignalService.UpdateStatus(
                    signal.Symbol, execTime,
                    TradeExecutionStatus.Blocked, 0, 0);

                return OrderResult.Fail($"LiquidityGuard:{liquidityResult.Reason}");
            }

            // =============================================================
            // ENTRY ORDER
            // =============================================================

            var entryType = allowMarketEntry
     ? FuturesOrderType.Market
     : FuturesOrderType.Limit;

            decimal? orderPrice = allowMarketEntry
                ? null
                : aggrLimitPrice;

            TimeInForce? tif = allowMarketEntry
                ? null
                : TimeInForce.GoodTillCanceled;

            // 🔒 HARD GUARDS (оставить навсегда)
            if (entryType == FuturesOrderType.Market && orderPrice != null)
                return OrderResult.Fail("InvalidMarketOrderWithPrice");

            if (entryType == FuturesOrderType.Limit && orderPrice == null)
                return OrderResult.Fail("InvalidLimitOrderWithoutPrice");

            var entryRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: signal.Symbol,
                side: side,
                type: entryType,
                quantity: quantity,
                price: orderPrice,
                positionSide: posSide,
                timeInForce: tif,
                reduceOnly: false,
                workingType: WorkingType.Mark,
                ct: ct
            );


            if (!entryRes.Success || entryRes.Data == null)
            {
                await _simulator.SimulateMissedTradeAsync(signal, "EntryError");
                _executedSignalService.UpdateStatus(
                    signal.Symbol, execTime,
                    TradeExecutionStatus.Blocked, 0, 0);

                return OrderResult.Fail("ENTRY_FAILED");
            }

            long entryOrderId = entryRes.Data.Id;

            decimal notionalAtCreate = orderPrice.HasValue
    ? quantity * orderPrice.Value
    : 0m; // MARKET — notional неизвестен до fill

            _executedSignalService.UpdateStatus(
     signal.Symbol,
     execTime,
     TradeExecutionStatus.OrderCreated,
     quantity,
     orderPrice.HasValue
         ? quantity * orderPrice.Value
         : 0m
 );


             _simulator.AppendLifecycleEventAsync(signal, "ORDER_CREATED");

            // =============================================================
            // WAIT FILL
            // =============================================================
            bool marketFallbackUsed = false;

            var wait = await WaitForPositionOrOrderAsync(
                client, signal, posSide,
                entryOrderId, entryPrice,
                quantity, ct);

            if (!wait.HasPosition && !allowMarketEntry && wait.Reason == "TimeoutNoFill" && !marketFallbackUsed)
            {
                marketFallbackUsed = true;

                try { await client.UsdFuturesApi.Trading.CancelOrderAsync(signal.Symbol, entryOrderId, ct: ct); }
                catch { }

                var mktRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    signal.Symbol,
                    side,
                    FuturesOrderType.Market,
                    quantity,
                    positionSide: posSide,
                    ct: ct);

                if (mktRes.Success && mktRes.Data != null)
                {
                    entryOrderId = mktRes.Data.Id;
                    wait = await WaitForPositionOrOrderAsync(
                        client, signal, posSide,
                        entryOrderId, lastPrice,
                        quantity, ct);
                }
            }



            if (!wait.HasPosition)
            {
                var reason = marketFallbackUsed
                    ? $"FallbackMarketFailed:{wait.Reason}"
                    : wait.Reason ?? "NotFilled";

                await _simulator.SimulateMissedTradeAsync(signal, reason);

                _executedSignalService.UpdateStatus(
                    signal.Symbol,
                    execTime,
                    TradeExecutionStatus.Blocked,
                    qty: 0,
                    notional: 0
                );

                return OrderResult.Fail(reason);
            }
 

            // =============================================================
            // FINAL SUCCESS
            // =============================================================
            entryPrice = wait.EntryPrice;
            quantity = wait.Qty;

            if (entryPrice <= 0 || quantity <= 0)
                return OrderResult.Fail("InvalidFinalState");

            _executedSignalService.UpdateStatus(
                signal.Symbol, execTime,
                TradeExecutionStatus.PositionOpened,
                quantity, quantity * entryPrice,
                filledEntry: entryPrice);
            _simulator.AppendLifecycleEventAsync(signal, "POSITION_OPENED");

            return OrderResult.Successs(entryPrice, quantity, entryOrderId);
        }


        // =====================================================================
        // WAIT FOR POSITION or ORDER FILL (dual-track)
        // =====================================================================
        // =====================================================================
        // WAIT FOR POSITION or ORDER FILL (dual-track) — PRODUCTION FIXED
        // - Accept FILLED by order even if position visibility lags
        // - If executedQty > 0 at end -> SUCCESS (exchange lag), not FAIL
        // - Read positions WITHOUT symbol-filter (Binance bug-safe), then filter locally
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
            const int maxLoops = 60;            // 60 * 500ms ~ 30s
            const int delayMs = 500;
            const decimal maxSlipPct = 0.004m;  // 0.4% runaway log threshold

            decimal lastExecuted = 0m;
            bool runawayLogged = false;

            for (int i = 0; i < maxLoops; i++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    // ---- 1) Read order ----
                    OrderStatus? status = null;
                    decimal executedQty = 0m;
                    decimal avgPrice = fallbackEntry;

                    var ordRes = await client.UsdFuturesApi.Trading.GetOrderAsync(signal.Symbol, entryOrderId, ct: ct);
                    if (ordRes.Success && ordRes.Data != null)
                    {
                        status = ordRes.Data.Status;
                        executedQty = ordRes.Data.QuantityFilled;

                        avgPrice = ordRes.Data.AveragePrice > 0
                            ? ordRes.Data.AveragePrice
                            : fallbackEntry;

                        // progress log
                        if (executedQty > 0 && executedQty != lastExecuted)
                        {
                            lastExecuted = executedQty;
                            _logger.LogInformation(
                                "[ORDER][{symbol}] Partial fill: {exec}/{total} status={st}",
                                signal.Symbol, executedQty, ordRes.Data.Quantity, status);
                        }

                        // ✅ PRO FIX: if FILLED — accept immediately, position can lag
                        if (status == OrderStatus.Filled)
                        {
                            var qty =
     executedQty > 0
         ? executedQty
         : (requestedQty > 0
             ? requestedQty
             : 0m);

                            var entry =
                                avgPrice > 0
                                    ? avgPrice
                                    : fallbackEntry;
                           
                            _logger.LogInformation(
                                "[ORDER][{symbol}] Order FILLED before position visible → accept fill. qty={qty} entry={entry}",
                                signal.Symbol, qty, entry);

                            return (true, entry, qty, "OrderFilled");
                        }

                        // If order is dead and nothing filled -> fail early
                        if (status is OrderStatus.Canceled or OrderStatus.Rejected or OrderStatus.Expired)
                        {
                            if (executedQty <= 0)
                                return (false, 0m, 0m, status switch
                                {
                                    OrderStatus.Canceled => "OrderCanceled",
                                    OrderStatus.Rejected => "OrderRejected",
                                    OrderStatus.Expired => "OrderExpired",
                                    _ => "OrderDead"
                                });

                            // executedQty > 0: do NOT fail here, keep checking position for a bit
                            _logger.LogWarning(
                                "[ORDER][{symbol}] Order {st} with exec={exec} → waiting position lag",
                                signal.Symbol, status, executedQty);
                        }
                    }

                    // ---- 2) Read position (Binance bug-safe: NO symbol filter) ----
                    var posRes = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                    if (posRes.Success && posRes.Data != null)
                    {
                        var pos = posRes.Data.FirstOrDefault(p =>
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

                    // ---- 3) Runaway log (non-fatal) ----
                    if (lastExecuted <= 0 && (i % 10 == 0))
                    {
                        try
                        {
                            var priceRes = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(signal.Symbol, ct: ct);
                            if (priceRes.Success && priceRes.Data != null && priceRes.Data.Price > 0 && fallbackEntry > 0)
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
                                    }
                                }
                                else
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
                            _logger.LogWarning(exPrice, "[ORDER][{symbol}] Error reading price", signal.Symbol);
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

            // ---- 4) Final check: order + position ----
            try
            {
                OrderStatus? status = null;
                decimal executedQty = 0m;
                decimal avgPrice = fallbackEntry;

                var ordRes = await client.UsdFuturesApi.Trading.GetOrderAsync(signal.Symbol, entryOrderId, ct: ct);
                if (ordRes.Success && ordRes.Data != null)
                {
                    status = ordRes.Data.Status;
                    executedQty = ordRes.Data.QuantityFilled;

                    avgPrice = ordRes.Data.AveragePrice > 0
                        ? ordRes.Data.AveragePrice
                        : fallbackEntry;

                    _logger.LogWarning(
                        "[ORDER][{symbol}] After wait: status={st}, exec={exec}, avg={avg}",
                        signal.Symbol, status, executedQty, avgPrice);
                }

                var posRes = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                if (posRes.Success && posRes.Data != null)
                {
                    var pos = posRes.Data.FirstOrDefault(p =>
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

                // Try cancel order (best-effort)
                try
                {
                    await client.UsdFuturesApi.Trading.CancelOrderAsync(signal.Symbol, entryOrderId, ct: ct);
                }
                catch { }

                // ✅ PRO FIX: if any execution happened -> accept fill (position may lag)
                if (executedQty > 0m)
                {
                    var entry = avgPrice > 0 ? avgPrice : fallbackEntry;

                    _logger.LogWarning(
                        "[ORDER][{symbol}] ExecQty>0 but position not visible yet → accept fill (exchange lag). exec={exec} entry={entry}",
                        signal.Symbol, executedQty, entry);

                    return (true, entry, executedQty, "OrderExecutedAwaitPosLag");
                }

                // Real no-fill case
                return (false, 0m, 0m, "TimeoutNoFill");
            }
            catch (Exception exFinal)
            {
                _logger.LogError(exFinal, "[ORDER][{symbol}] Fatal in WaitForPositionOrOrderAsync", signal.Symbol);
                return (false, 0m, 0m, "WaitFatalError");
            }
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
