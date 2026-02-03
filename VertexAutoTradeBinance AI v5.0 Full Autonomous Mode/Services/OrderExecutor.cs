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
        private readonly RiskManager _risk;

        // ===== ENTRY EXECUTION TUNING (PRODUCTION) =====
       
        private const decimal AGGR_LIMIT_OFFSET_PCT = 0.0006m;  // 0.06% агрессивный лимит (тюнится)
        private const decimal MARKET_FALLBACK_MAX_SLIP_PCT = 0.0015m; // 0.15% макс. слип для fallback-market
        private bool? _isHedgeMode;

        public OrderExecutor(
            ILogger<OrderExecutor> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            SimulatedTradeService simulator,
            ExecutedSignalService executedSignalService,
            MarketDataService marketData,
            AiMarketRegimeService marketRegimeService,
            SmartRegimeService smartRegime,
            LiquidityGuardService liquidityGuard, AiSelfLearningService ai,RiskManager risk)
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
            _risk = risk;
        }
 
        private async Task<bool> IsHedgeModeAsync(BinanceRestClient client, CancellationToken ct)
        {
            if (_isHedgeMode.HasValue)
                return _isHedgeMode.Value;

            try
            {
                var res = await client.UsdFuturesApi.Account.GetPositionModeAsync(ct: ct);
                if (res.Success)
                {
                    _isHedgeMode = res.Data.IsHedgeMode; // true = Hedge (dual-side)
                    return res.Data.IsHedgeMode;
                }
            }
            catch { }

            // FAIL-SAFE: assume One-Way (safer)
            _isHedgeMode = false;
            return false;
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
            var isHedge = await IsHedgeModeAsync(client, ct);

            _logger.LogInformation(
    "[ACCOUNT] Futures position mode: {mode}",
    isHedge ? "HEDGE" : "ONE-WAY"
);
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

                await _simulator.AppendLifecycleEventAsync(
                    signal,
                    stage: "PREFILTER_REJECT",
                    reason: reason,
                    attemptNotional: 0m,
                    requiredMinNotional: 0m);

                var rec = _executedSignalService.AddSignalCreated(
                    signal,
                    opportunityScore: 0,
                    atr: signal.Atr ?? 0m,
                    volatility: 0m,
                    slope: 0m,
                    qty: 0m,
                    notional: 0m,
                    tags: reason);

                _executedSignalService.UpdateStatus(
                    signal.Symbol,
                    rec.Time,
                    TradeExecutionStatus.Blocked,
                    0,
                    0);

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

                var px = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(signal.Symbol, ct: cts.Token);
                if (px.Success && px.Data != null && px.Data.Price > 0)
                    lastPrice = px.Data.Price;
            }
            catch { /* fallback */ }

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

            decimal minRr =
                smart.EntryProfile == "CT" ? 1.2m :
                smart.EntryProfile == "EXP" ? 1.8m :
                1.5m;

            bool rrOk = rr >= minRr;

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
                    superSignal: signal.IsSuperSignal);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LiquidityGuard] Analyze failed → FAIL-SAFE ALLOW ({symbol})", signal.Symbol);

                liquidityResult = new LiquidityGuardResult(
                    Block: false,
                    Reason: LiquidityGuardReason.None,
                    IsExtreme: false,
                    Details: "AnalyzeFailed",
                    UtcTime: DateTime.UtcNow);
            }

            bool liquiditySafe = !liquidityResult.Block;

            // =============================================================
            // MARKET ENTRY PERMISSION (STRUCTURE-BASED, NO CONFIDENCE GATE)
            // =============================================================
            bool allowMarketEntry = false;

            if (smart.EntryProfile == "EXP")
            {
                allowMarketEntry =
                    isSmartStrongTrend &&
                    rrOk &&
                    liquiditySafe;
            }
            else if (smart.EntryProfile == "CT")
            {
                allowMarketEntry =
                    rrOk &&
                    liquiditySafe &&
                    !liquidityResult.IsExtreme;
            }
            else
            {
                allowMarketEntry =
                    isSmartStrongTrend &&
                    rrOk &&
                    liquiditySafe;
            }


            // =============================================================
            // APPLY SOFT LIQUIDITY SIZE MULTIPLIER (EARLY EXPANSION FIX)
            // =============================================================
            var sizeMul = signal.SizeMultiplier <= 0 ? 1.0m : signal.SizeMultiplier;

            // hard clamp — защита от глупостей
            sizeMul = Math.Clamp(sizeMul, 0.25m, 1.0m);

            quantity *= sizeMul;

            // =============================================================
            // EXECUTED SIGNAL CREATED
            // =============================================================
            decimal executionBias = Math.Clamp(smart.RiskBias * liquidityResult.Score, 0.4m, 1.0m);
            decimal adjustedQty = Math.Floor((quantity * executionBias) / step) * step;

            if (adjustedQty < filters.minQty)
            {
                await _simulator.SimulateMissedTradeAsync(
                    signal, 
                    "ADJUSTED_QTY_TOO_SMALL",
                    note: $"baseQty={quantity}; bias={executionBias:F2}; riskBias={smart.RiskBias:F2}; liqScore={liquidityResult.Score:F2}",
                    freeBalance: _risk.LastBalanceUsdt,
                    attemptNotional: quantity * entryPrice,
                    requiredMinNotional: filters.minQty * entryPrice);

                return OrderResult.Fail("ADJUSTED_QTY_TOO_SMALL");
            }

            quantity = adjustedQty;
            decimal notional = quantity * entryPrice;

            var execRec = _executedSignalService.AddSignalCreated(
                signal,
                opportunityScore: (int)(smart.Confidence * 100), // оставил для UI/аналитики, но НЕ как гейт
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
                    $"LiquidityGuard:{liquidityResult.Reason}",
                    note: $"details={liquidityResult.Details}; extreme={liquidityResult.IsExtreme}; profile={smart.EntryProfile}; liqScore={liquidityResult.Score:F2}",
                    attemptNotional: notional,
                    requiredMinNotional: 0m);

                return OrderResult.Fail($"LiquidityGuard:{liquidityResult.Reason}");
            }

            // =============================================================
            // ENTRY ORDER — BINANCE 2026 (STRUCTURE + NO-CHASE LIMIT + CONTROLLED JOIN)
            // =============================================================
            decimal priceDriftPct =
                entryPrice > 0 ? Math.Abs(lastPrice - entryPrice) / entryPrice : 0m;

            // A) MARKET only for: breakout / tight reclaim / emergency super-signal
            bool breakoutMarket =
                allowMarketEntry &&
                hasImpulse &&
                isSmartStrongTrend &&
                !liquidityResult.IsExtreme;

            bool reclaimMarket =
                allowMarketEntry &&
                !hasImpulse &&
                smart.EntryProfile == "CT" &&
                !liquidityResult.IsExtreme &&
                priceDriftPct <= 0.0010m; // tight reclaim only

            bool emergencyMarket =
                allowMarketEntry &&
                signal.IsSuperSignal &&
                !liquidityResult.IsExtreme &&
                priceDriftPct <= 0.0025m; // emergency only when not runaway

            bool useMarket = breakoutMarket || reclaimMarket || emergencyMarket;

            FuturesOrderType entryType = useMarket ? FuturesOrderType.Market : FuturesOrderType.Limit;

            // B) LIMIT pricing
            // PASSIVE anchor: do not worsen price (default)
            decimal passiveLimit =
                side == OrderSide.Buy
                    ? Math.Min(entryPrice, lastPrice)
                    : Math.Max(entryPrice, lastPrice);

            // CONTROLLED JOIN: catch “silent trend” without impulse
            // uses ONLY existing signals: strong trend + slope + drift + liq safe
            decimal tickRel = lastPrice > 0 ? (tick / lastPrice) : 0m;
            bool spreadOk = tickRel <= 0.00050m; // rough proxy; conservative

            bool silentTrendJoin =
                entryType == FuturesOrderType.Limit &&
                allowMarketEntry &&
                !hasImpulse &&
                isSmartStrongTrend &&
                rrOk &&
                liquiditySafe &&
                !liquidityResult.IsExtreme &&
                spreadOk &&
                baseReg.TrendSlopePercent >= 0.25m &&   // “тихий” наклон
                priceDriftPct >= 0.0025m &&             // цена уже поехала
                priceDriftPct <= 0.0150m;               // но не runaway

            // join pct: use your constant (0.06%) but cap hard for safety
            decimal joinPct = silentTrendJoin ? Math.Min(AGGR_LIMIT_OFFSET_PCT, 0.0010m) : 0m;

            decimal joinLimit =
                side == OrderSide.Buy
                    ? lastPrice * (1m + joinPct)
                    : lastPrice * (1m - joinPct);

            decimal finalLimitRaw = silentTrendJoin ? joinLimit : passiveLimit;
            decimal aggrLimitPrice = Quantize(finalLimitRaw, tick);

            decimal? orderPrice = entryType == FuturesOrderType.Market ? null : aggrLimitPrice;
            TimeInForce? tif = entryType == FuturesOrderType.Market ? null : TimeInForce.GoodTillCanceled;

            _logger.LogInformation(
                "[ORDER][{symbol}] ENTRY={type} mode={mode} entry={entry} last={last} drift={drift:P2} slope={slope:F3}% impulse={imp} liqExt={ext} liqScore={score:F2}",
                signal.Symbol,
                entryType,
                entryType == FuturesOrderType.Market ? "MARKET" : (silentTrendJoin ? "JOIN_LIMIT" : "PASSIVE_LIMIT"),
                entryPrice,
                lastPrice,
                priceDriftPct,
                baseReg.TrendSlopePercent,
                hasImpulse,
                liquidityResult.IsExtreme,
                liquidityResult.Score);

            // =============================================================
            // FIX: APPLY CORRECT QTY RULE PER ORDER TYPE (MARKET vs LIMIT)
            // =============================================================
            var qtyRule = entryType == FuturesOrderType.Market
                ? SymbolInfoService.QtyRule.Market
                : SymbolInfoService.QtyRule.Limit;

            var fQty = await _symbolInfo.GetFuturesFiltersAsync(signal.Symbol, qtyRule);

            quantity = Math.Floor(quantity / fQty.step) * fQty.step;

            if (quantity <= 0m || quantity < fQty.minQty)
            {
                await _simulator.SimulateMissedTradeAsync(
                    signal,
                    "QTY_TOO_SMALL_AFTER_RULE",
                    note: $"entryType={entryType}; qtyRule={qtyRule}; qty={quantity}; minQty={fQty.minQty}; step={fQty.step}",
                    attemptNotional: 0m,
                    requiredMinNotional: 0m);

                _executedSignalService.UpdateStatus(signal.Symbol, execTime, TradeExecutionStatus.Blocked, 0, 0);
                return OrderResult.Fail("QTY_TOO_SMALL_AFTER_RULE");
            }

            decimal markForNotional = lastPrice > 0 ? lastPrice : entryPrice;

            decimal notionalAtCreate = orderPrice.HasValue
                ? quantity * orderPrice.Value
                : quantity * markForNotional;

            // HARD GUARDS
            if (entryType == FuturesOrderType.Market && orderPrice != null)
                return OrderResult.Fail("InvalidMarketOrderWithPrice");

            if (entryType == FuturesOrderType.Limit && orderPrice == null)
                return OrderResult.Fail("InvalidLimitOrderWithoutPrice");

            // =============================================================
            // PLACE ENTRY
            // =============================================================

            var entryRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
              symbol: signal.Symbol,
              side: side,
              type: entryType,
              quantity: quantity,
              price: orderPrice,
              positionSide: isHedge ? posSide : null,   // ✅ ВАЖНО
              timeInForce: tif,
              reduceOnly: null,
              ct: ct);

            if (!entryRes.Success || entryRes.Data == null)
            {
                var note = entryRes.Error != null
                    ? $"code={entryRes.Error.Code}; msg={entryRes.Error.Message}"
                    : "no_error_object";

                await _simulator.SimulateMissedTradeAsync(
                    signal,
                    "EntryError",
                    note: note,
                    attemptNotional: notionalAtCreate > 0 ? notionalAtCreate : notional,
                    requiredMinNotional: 0m);

                _executedSignalService.UpdateStatus(signal.Symbol, execTime, TradeExecutionStatus.Blocked, 0, 0);
                return OrderResult.Fail("ENTRY_FAILED");
            }

            long entryOrderId = entryRes.Data.Id;

            _executedSignalService.UpdateStatus(
                signal.Symbol,
                execTime,
                TradeExecutionStatus.OrderCreated,
                quantity,
                notionalAtCreate);

            await _simulator.AppendLifecycleEventAsync(signal, "ORDER_CREATED",freeBalance:_risk.LastBalanceUsdt);

            // =============================================================
            // WAIT FILL
            // =============================================================
            bool marketFallbackUsed = false;

            var wait = await WaitForPositionOrOrderAsync(
                client,
                signal,
                posSide,
                entryOrderId,
                entryPrice,
                quantity,
                ct);

            if (!wait.HasPosition &&
                entryType == FuturesOrderType.Limit &&
                wait.Reason == "TimeoutNoFill" &&
                !marketFallbackUsed)
            {
                marketFallbackUsed = true;

                try { await client.UsdFuturesApi.Trading.CancelOrderAsync(signal.Symbol, entryOrderId, ct: ct); }
                catch { }

                // guard runaway before fallback-market
                decimal markNow = lastPrice;
                try
                {
                    var pxNow = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(signal.Symbol, ct: ct);
                    if (pxNow.Success && pxNow.Data?.Price > 0)
                        markNow = pxNow.Data.Price;
                }
                catch { }

                decimal atrSlipCap =
              (signal.Atr ?? 0m) > 0 && entryPrice > 0
              ? Math.Min((signal.Atr.Value / entryPrice) * 1.2m, MARKET_FALLBACK_MAX_SLIP_PCT)
              : MARKET_FALLBACK_MAX_SLIP_PCT; 

                decimal driftNowPct =
                    entryPrice > 0 ? Math.Abs(markNow - entryPrice) / entryPrice : 0m;
                //==============================================================================================================================
                // =============================================================
                // LATE-CANCEL GUARD — STRUCTURE RECHECK (PRO)
                // =============================================================

                // 1) Re-read recent price (cheap)
                // after pxNow read
                decimal lastNow = markNow; // confirmed fresh price

                // 2) Simple structure break: price crossed against trend by > X ATR
                bool structureBroken = false;

                if (signal.Side == SignalSide.Buy)
                {
                    // long: price fell below entry by > 0.6 ATR
                    structureBroken =
                        (signal.Atr ?? 0m) > 0 &&
                        entryPrice > 0 &&
                        lastNow < entryPrice - (signal.Atr.Value * 0.6m);
                }
                else
                {
                    // short: price rose above entry by > 0.6 ATR
                    structureBroken =
                        (signal.Atr ?? 0m) > 0 &&
                        entryPrice > 0 &&
                        lastNow > entryPrice + (signal.Atr.Value * 0.6m);
                }

                if (structureBroken)
                {
                    _logger.LogWarning(
                        "[LATE-CANCEL][{symbol}] Structure broken during wait → cancel entry. last={last} entry={entry} atr={atr}",
                        signal.Symbol,
                        lastNow,
                        entryPrice,
                        signal.Atr ?? 0m);

                    await _simulator.SimulateMissedTradeAsync(
                        signal,
                        "LateCancel:StructureBroken",
                        note: $"last={lastNow}; entry={entryPrice}; atr={(signal.Atr ?? 0m)}",
                        attemptNotional: notionalAtCreate,
                        requiredMinNotional: 0m);

                    _executedSignalService.UpdateStatus(
                        signal.Symbol,
                        execTime,
                        TradeExecutionStatus.Blocked,
                        0,
                        0);

                    return OrderResult.Fail("LATE_CANCEL_STRUCTURE_BROKEN");
                }
//=========================================================================================================NEW LATE


                bool allowFallbackMarket =
                 !liquidityResult.IsExtreme ||
                 (signal.IsSuperSignal && rrOk);

                if (!allowFallbackMarket || driftNowPct > atrSlipCap)
                {
                    await _simulator.SimulateMissedTradeAsync(
                        signal,
                        "Я тебя только что спас от херового market-влёта",//FALLBACK_MKT_BLOCKED_RUNAWAY_OR_EXTREME
                        note: $"drift={driftNowPct:P2}; max={atrSlipCap:P2}; extreme={liquidityResult.IsExtreme}",
                        attemptNotional: notionalAtCreate,
                        requiredMinNotional: 0m);

                    _executedSignalService.UpdateStatus(signal.Symbol, execTime, TradeExecutionStatus.Blocked, 0, 0);
                    return OrderResult.Fail("FALLBACK_MKT_BLOCKED");
                }

                var fMkt = await _symbolInfo.GetFuturesFiltersAsync(signal.Symbol, SymbolInfoService.QtyRule.Market);
                var mktQty = Math.Floor(quantity / fMkt.step) * fMkt.step;

                if (mktQty < fMkt.minQty)
                    return OrderResult.Fail("FALLBACK_MKT_QTY_TOO_SMALL");

              
                var mktRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: signal.Symbol,
                    side: side,
                    type: FuturesOrderType.Market,
                    quantity: mktQty,
                    positionSide: isHedge ? posSide : null,
                    ct: ct);

                if (mktRes.Success && mktRes.Data != null)
                {
                    entryOrderId = mktRes.Data.Id;

                    wait = await WaitForPositionOrOrderAsync(
                        client,
                        signal,
                        posSide,
                        entryOrderId,
                        lastPrice,
                        mktQty,
                        ct);
                }
            }

            if (!wait.HasPosition)
            {
                var reason = marketFallbackUsed
                    ? $"FallbackMarketFailed:{wait.Reason}"
                    : wait.Reason ?? "NotFilled";

                await _simulator.SimulateMissedTradeAsync(
                    signal,
                    reason,
                    note: $"marketFallbackUsed={marketFallbackUsed}; entryType={entryType}; orderPrice={(orderPrice?.ToString() ?? "null")}; lastPrice={lastPrice}; entryPrice={entryPrice}; waitReason={wait.Reason}",
                    attemptNotional: orderPrice.HasValue ? quantity * orderPrice.Value : notional,
                    requiredMinNotional: 0m);

                _executedSignalService.UpdateStatus(
                    signal.Symbol,
                    execTime,
                    TradeExecutionStatus.Blocked,
                    qty: 0,
                    notional: 0);

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
                signal.Symbol,
                execTime,
                TradeExecutionStatus.PositionOpened,
                quantity,
                quantity * entryPrice,
                filledEntry: entryPrice);

            await _simulator.AppendLifecycleEventAsync(signal, "POSITION_OPENED");

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
