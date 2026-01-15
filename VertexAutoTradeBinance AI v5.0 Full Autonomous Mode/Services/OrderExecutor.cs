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

            var filters = await _symbolInfo.GetFuturesFiltersAsync(signal.Symbol);
            var tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;
            var step = filters.step <= 0 ? 0.0001m : filters.step;

            // Округление количества
            quantity = Quantize(quantity, step);

            if (quantity < filters.minQty)
            {
                _logger.LogWarning(
                    "[ORDER][{symbol}] qty<{minQty} after quantize → BLOCK",
                    signal.Symbol, filters.minQty);

                return OrderResult.Fail("QTY_TOO_SMALL");
            }
            if (quantity <= 0)
            {
                var reason = signal.RejectReason ?? "RiskRejected";

                _logger.LogWarning(
                    "[ORDER][{symbol}] QTY=0 → BLOCKED by RiskManager | reason={reason}",
                    signal.Symbol, reason);

                // 1) симуляция пропущенной сделки
                //  await _simulator.SimulateMissedTradeAsync(signal, reason);
                var missed = await _simulator.SimulateMissedTradeAsync(signal, reason);

                if (missed != null)
                { 
                    // 🔥 ОБУЧЕНИЕ AI
                    _ai.RecordMarketStateTriggered(
                        reason: $"MISSED:{reason}",
                        symbol: signal.Symbol,
                        timeframe: "MissedTrade",
                        regime: missed.Regime,
                        slope: missed.Slope,
                        volatility: missed.Vol,
                        atr: missed.Atr,
                        confidence: missed.Confidence,
                        skipSnapshot: true
                    );
                    // 2) (опционально, но правильно)
                    _logger.LogInformation(
                        "[ORDER][{symbol}] MissedTrade recorded → reason={reason}",
                        missed.Symbol,
                        missed.Reason
                    );
                }
               

                // 2) ОБЯЗАТЕЛЬНО: создаём запись SignalCreated (иначе нечего обновлять)
                var blockedRec = _executedSignalService.AddSignalCreated(
                    signal,
                    opportunityScore: 0,
                    atr: signal.Atr ?? 0m,
                    volatility: 0m,
                    slope: 0m,
                    qty: 0m,
                    notional: 0m,
                    tags: $"BLOCKED_QTY0|reason={reason}"
                );

                // 3) обновляем статус ЭТОЙ ЖЕ записи
                _executedSignalService.UpdateStatus(
                    symbol: signal.Symbol,
                    time: blockedRec.Time,                 // ✅ тот же time
                    status: TradeExecutionStatus.Blocked,
                    qty: 0,
                    notional: 0
                );

                return OrderResult.Fail(reason);
            }


            var side = signal.Side == SignalSide.Buy ? OrderSide.Buy : OrderSide.Sell;
            var posSide = signal.Side == SignalSide.Buy ? PositionSide.Long : PositionSide.Short;

            decimal entryPrice = Quantize(signal.EntryPrice, tick);

            // ===== current price for execution tuning =====
            decimal lastPrice = 0m;
            try
            {
                // Лучше брать из твоего MarketDataService/WS-cache, но не ломаем — берём REST price точечно.
                var px = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(signal.Symbol, ct: ct);
                if (px.Success && px.Data != null)
                    lastPrice = px.Data.Price;
            }
            catch { /* ignore */ }

            // fallback if unavailable
            if (lastPrice <= 0)
                lastPrice = entryPrice;

            // ===== build aggressive limit to reduce TimeoutNoFill =====
            decimal aggrLimitPrice;
            if (side == OrderSide.Buy)
            {
                // BUY: чтобы гарантировать fill лимитом, цена должна быть >= текущей
                var p = lastPrice * (1m + AGGR_LIMIT_OFFSET_PCT);
                aggrLimitPrice = Quantize(Math.Max(entryPrice, p), tick);
            }
            else
            {
                // SELL: чтобы гарантировать fill лимитом, цена должна быть <= текущей
                var p = lastPrice * (1m - AGGR_LIMIT_OFFSET_PCT);
                aggrLimitPrice = Quantize(Math.Min(entryPrice, p), tick);
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
            if (signal.StopLoss > 0 && signal.TakeProfit.HasValue)
            {
                var risk = Math.Abs(signal.EntryPrice - signal.StopLoss);
                var reward = Math.Abs(signal.TakeProfit.Value - signal.EntryPrice);
                if (risk > 0)
                    rr = reward / risk;
            }

            const decimal MIN_MARKET_RR = 1.8m;
            bool rrOk = rr >= MIN_MARKET_RR;

            // 4) LiquidityGuard (REAL API)  ✅
            //    Никаких IsDanger/IsSymbolBlocked — у тебя есть только Analyze(...)
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
                // fail-safe: если LiquidityGuard упал — не блокируем market-only по ошибке сервиса
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

            var execTime = execRecord.Time;


            // =====================================================================
            // HARD VETO GATES (PRODUCTION)
            // =====================================================================

            // 1) LiquidityGuard HARD BLOCK (no entry unless superSignal)
          
            if (liquidityResult.Block)
            {
                _logger.LogWarning(
                    "[ORDER][{symbol}] HARD BLOCK by LiquidityGuard | reason={reason} details={details}",
                    signal.Symbol, liquidityResult.Reason, liquidityResult.Details);

                var missed = await _simulator.SimulateMissedTradeAsync(
                    signal,
                    $"LiquidityGuardBlock:{liquidityResult.Reason}"
                );

                if (missed != null)
                {
                    _ai.RecordMarketStateTriggered(
                        reason: $"MISSED:LiquidityGuard:{liquidityResult.Reason}",
                        symbol: signal.Symbol,
                        timeframe: "MissedTrade",
                        regime: missed.Regime,
                        slope: missed.Slope,
                        volatility: missed.Vol,
                        atr: missed.Atr,
                        confidence: missed.Confidence,
                        skipSnapshot: true
                    );
                }

                _executedSignalService.UpdateStatus(
                    symbol: signal.Symbol,
                    time: execTime,
                    status: TradeExecutionStatus.Blocked,
                    qty: 0,
                    notional: 0
                );

                return OrderResult.Fail($"LiquidityGuardBlock:{liquidityResult.Reason}");
            }



            // 2) AI Risk Veto (threshold should be config later)
            const decimal AI_RISK_VETO = 1.8m;
            
            if (aiRisk >= AI_RISK_VETO)
            {
                var reason = $"AiRiskVeto:{aiRisk:F2}";

                _logger.LogWarning(
                    "[ORDER][{symbol}] AI RISK VETO | aiRisk={aiRisk:F2} >= {thr}",
                    signal.Symbol, aiRisk, AI_RISK_VETO);

                // 🔥 КЛЮЧЕВОЕ: получаем MissedTradeRecord
                var missed = await _simulator.SimulateMissedTradeAsync(signal, reason);

                // статус — для executed_signals.json
                _executedSignalService.UpdateStatus(
                    symbol: signal.Symbol,
                    time: execTime,
                    status: TradeExecutionStatus.Blocked,
                    qty: 0,
                    notional: 0
                );

                // (опционально, но правильно) — лог подтверждения
                if (missed != null)
                {
                    _logger.LogInformation(
                        "[ORDER][{symbol}] MissedTrade recorded (AI_RISK_VETO)",
                        missed.Symbol);
                }

                return OrderResult.Fail(reason);
            }


            // 3) Symbol Freeze Controller (hook here once service is available)
            // TODO: inject ISymbolFreezeController or AiSelfLearning DecisionGates and hard veto here.
            // if (_symbolFreeze.IsFrozen(signal.Symbol)) { ... return Fail("SymbolFrozen"); }

            var orderPrice = allowMarketEntry ? lastPrice : aggrLimitPrice;
            var orderNotional = quantity * orderPrice;

            if (!allowMarketEntry && filters.minNotional > 0 && orderNotional < filters.minNotional)
            {
                var reason = $"MinNotionalAtOrderPrice:{orderNotional:F4}<{filters.minNotional:F4}";
                _logger.LogWarning("[ORDER][{symbol}] BLOCK {reason} | qty={qty} price={price}",
                    signal.Symbol, reason, quantity, orderPrice);

                _executedSignalService.UpdateStatus(signal.Symbol, execTime, TradeExecutionStatus.Blocked, 0, 0);
                return OrderResult.Fail(reason);
            }

            quantity = NormalizeToStep(quantity, step);

            entryPrice = Quantize(signal.EntryPrice, tick);
            aggrLimitPrice = Quantize(aggrLimitPrice, tick);
            signal.StopLoss = Quantize(signal.StopLoss, tick);

            if (signal.StopLoss > 0)
                signal.StopLoss = Quantize(signal.StopLoss, tick);

            if (signal.TakeProfit.HasValue)
                signal.TakeProfit = Quantize(signal.TakeProfit.Value, tick);

             

            var entryType = allowMarketEntry ? FuturesOrderType.Market : FuturesOrderType.Limit;

            _logger.LogInformation(
                "[ORDER][{symbol}] EntryMode={mode} smart={smart} base={baseReg} impulse={imp} rrOk={rrOk} liqSafe={liqSafe}",
                signal.Symbol,
                allowMarketEntry ? "MARKET" : "LIMIT",
                smart.SmartType,
                smart.BaseRegime,
                hasImpulse,
                rrOk,
                liquiditySafe
            );

            // =====================================================================
            // 1) ENTRY (LIMIT) — БЕЗ reduceOnly
            // =====================================================================
            var entryRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: signal.Symbol,
                side: side,
                //type: allowMarketEntry ? FuturesOrderType.Market : FuturesOrderType.Limit,
                //quantity: quantity,
                //price: allowMarketEntry ? null : entryPrice,
                type: entryType,
                quantity: quantity,
                price: allowMarketEntry ? null : aggrLimitPrice,
                positionSide: posSide,
              //  workingType: WorkingType.Mark,
                timeInForce: allowMarketEntry ? null : TimeInForce.GoodTillCanceled,
                ct: ct);

            if (!entryRes.Success || entryRes.Data == null)
            {
                var code = entryRes.Error?.Code;
                var msg = entryRes.Error?.Message ?? "Unknown";
                var reason = code.HasValue ? $"EntryError:{code}:{msg}" : $"EntryError:{msg}";

                _logger.LogError("[ORDER][{symbol}] ENTRY ERROR | {reason}", signal.Symbol, reason);

                await _simulator.SimulateMissedTradeAsync(signal, reason);

                _executedSignalService.UpdateStatus(signal.Symbol, execTime, TradeExecutionStatus.Blocked, 0, 0);
                return OrderResult.Fail(reason);
            }


            long entryOrderId = entryRes.Data.Id;
            _logger.LogInformation("[ORDER][{symbol}] ENTRY OK: id={id}, type={type}, price={price}, qty={qty} (signalEntry={sig})",
     signal.Symbol,
     entryOrderId,
     allowMarketEntry ? "MARKET" : "LIMIT",
     allowMarketEntry ? lastPrice : aggrLimitPrice,
     quantity,
     entryPrice);


            _executedSignalService.UpdateStatus(
                symbol: signal.Symbol,
                time: execTime,
                status: TradeExecutionStatus.OrderCreated,
                qty: quantity,
                notional: quantity * entryPrice
            );
            _simulator.AppendLifecycleEvent(
              signal,
              "ORDER_CREATED"
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

            // ===== MARKET FALLBACK (LIMIT -> MARKET) to eliminate TimeoutNoFill =====
            if (!wait.HasPosition && !allowMarketEntry && wait.Reason == "TimeoutNoFill")
            {
                // переоценка цены
                decimal nowPx = 0m;
                try
                {
                    var px2 = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(signal.Symbol, ct: ct);
                    if (px2.Success && px2.Data != null)
                        nowPx = px2.Data.Price;
                }
                catch { }

                if (nowPx > 0 && entryPrice > 0)
                {
                    decimal slipPct = side == OrderSide.Buy
                        ? (nowPx - entryPrice) / entryPrice
                        : (entryPrice - nowPx) / entryPrice;

                    // если цена ушла не слишком далеко и ликвидность ок — можно безопасно добрать MARKET
                    if (slipPct <= MARKET_FALLBACK_MAX_SLIP_PCT && liquiditySafe)
                    {
                        _logger.LogWarning(
                            "[ORDER][{symbol}] LIMIT TimeoutNoFill → FALLBACK MARKET (slip={slip:P2}, liqSafe={liq})",
                            signal.Symbol, slipPct, liquiditySafe);

                        // отменяем старый лимит (best-effort)
                        try { await client.UsdFuturesApi.Trading.CancelOrderAsync(signal.Symbol, entryOrderId, ct: ct); } catch { }

                        // ставим MARKET
                        var mktRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                            symbol: signal.Symbol,
                            side: side,
                            type: FuturesOrderType.Market,
                            quantity: quantity,
                            positionSide: posSide,
                            ct: ct);

                        if (mktRes.Success && mktRes.Data != null)
                        {
                            entryOrderId = mktRes.Data.Id;

                            // ждём позицию/факт fill (коротко)
                            var wait2 = await WaitForPositionOrOrderAsync(
                                client, signal, posSide, entryOrderId, nowPx, quantity, ct);

                            if (wait2.HasPosition)
                            {
                                entryPrice = wait2.EntryPrice;
                                quantity = wait2.Qty;

                                _logger.LogInformation(
                                    "[ORDER][{symbol}] MARKET FALLBACK SUCCESS → entry={entry} qty={qty}",
                                    signal.Symbol, entryPrice, quantity);

                                // и дальше код пойдёт как будто обычный успех (ниже не выходим)
                                wait = wait2;
                            }
                        }
                    }
                }
            }

            if (!wait.HasPosition)
            {
                var reason = wait.Reason ?? "EntryNotFilled";

                _logger.LogError(
                    "[ORDER][{symbol}] ENTRY FAIL → missed | reason={reason}",
                    signal.Symbol, reason);

                // 1) Missed trade (log + simulated learning)
                var missed = await _simulator.SimulateMissedTradeAsync(signal, reason);

                // 2) 🔥 AI learning из missed trade
                if (missed != null)
                {
                    _ai.RecordMarketStateTriggered(
                        reason: $"MISSED:{reason}",
                        symbol: signal.Symbol,
                        timeframe: "MissedTrade",
                        regime: missed.Regime,
                        slope: missed.Slope,
                        volatility: missed.Vol,
                        atr: missed.Atr,
                        confidence: missed.Confidence,
                        skipSnapshot: true
                    );
                }

                // 3) 🔥 ОБЯЗАТЕЛЬНО: обновляем executed_signals
                _executedSignalService.UpdateStatus(
                    symbol: signal.Symbol,
                    time: execTime,                 // ❗ НЕ DateTime.UtcNow
                    status: TradeExecutionStatus.Blocked,
                    qty: 0,
                    notional: 0
                );

                // 4) cleanup
                try
                {
                    await client.UsdFuturesApi.Trading.CancelOrderAsync(
                        signal.Symbol,
                        entryOrderId,
                        ct: ct);
                }
                catch { }


                // =========================================================
                // SAFE FALLBACK: LIMIT → MARKET (ONE SHOT, PRODUCTION)
                // =========================================================
                if (!allowMarketEntry && reason == "TimeoutNoFill")
                {
                    _logger.LogWarning(
                        "[ORDER][{symbol}] LIMIT TimeoutNoFill → retry MARKET (safe fallback)",
                        signal.Symbol);

                    var marketRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: signal.Symbol,
                        side: side,
                        type: FuturesOrderType.Market,
                        quantity: quantity,
                        positionSide: posSide,
                        ct: ct);

                    if (marketRes.Success && marketRes.Data != null)
                    {
                        var fillPrice =
                            marketRes.Data.AveragePrice > 0
                                ? marketRes.Data.AveragePrice
                                : signal.EntryPrice;

                        _logger.LogWarning(
                            "[ORDER][{symbol}] MARKET fallback FILLED → accept entry price={price}",
                            signal.Symbol, fillPrice);

                        return OrderResult.Successs(
                            fillPrice,
                            quantity,
                            marketRes.Data.Id);
                    }
                }

                return OrderResult.Fail(reason); 
            }



            // Если мы здесь — позиция реально открыта
            entryPrice = wait.EntryPrice;
            quantity = wait.Qty;

            _logger.LogInformation("[ORDER][{symbol}] POSITION OPENED at {price}, qty={qty}",
                signal.Symbol, entryPrice, quantity);


            _executedSignalService.UpdateStatus(
                symbol: signal.Symbol,
                time: execTime,
                status: TradeExecutionStatus.PositionOpened,
                qty: quantity,
                notional: quantity * entryPrice,
                filledEntry: entryPrice                     // ✅ ФАКТИЧЕСКИЙ ВХОД
            );

            _simulator.AppendLifecycleEvent(
      signal,
      "POSITION_OPENED"
  );


            // =====================================================================
            // 3) COMPUTE SL / TP (NO PLACEMENT HERE)
            // Responsibility: PositionSupervisorService v8.1 (NORMAL → ALGO RAW)
            // =====================================================================

            decimal atr = signal.Atr ?? 0m;
            decimal sl = signal.StopLoss;

            // TP FIX
            decimal tp = signal.TakeProfit ?? 0;
            if (tp <= 0 && signal.TakeProfits != null && signal.TakeProfits.Count > 0)
                tp = signal.TakeProfits[0];

            if (atr > 0)
            {
                sl = Quantize(sl, tick);
                if (tp > 0)
                    tp = Quantize(tp, tick);
            }

            _executedSignalService.UpdateProtectionComputed(
            symbol: signal.Symbol,
            time: execTime,
            stopLoss: sl,
            takeProfit: tp,
            atr: atr,
            tags: $"ProtectionComputed|Regime={smart.BaseRegime}|Liquidity=OK"
        );

            _logger.LogWarning(
                "[ORDER][{symbol}] PROTECTION COMPUTED ONLY → SL={sl}, TP={tp}. Supervisor will place orders (NORMAL/ALGO).",
                signal.Symbol, sl, tp
            ); 

            // ❗ НИЧЕГО НЕ СТАВИМ ЗДЕСЬ
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
                            var qty = executedQty > 0 ? executedQty : requestedQty;
                            var entry = avgPrice > 0 ? avgPrice : fallbackEntry;

                            _logger.LogInformation(
                                "[ORDER][{symbol}] Order FILLED before position visible → accept fill. qty={qty} entry={entry}",
                                signal.Symbol, qty, entry);

                            return (true, entry, qty, "OrderFilled");
                        }

                        // If order is dead and nothing filled -> fail early
                        if (status is OrderStatus.Canceled or OrderStatus.Rejected or OrderStatus.Expired)
                        {
                            if (executedQty <= 0)
                                return (false, 0m, 0m, "OrderCanceled");

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
