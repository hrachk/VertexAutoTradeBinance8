// ============================================================================
// - Если ордер Filled/позиция появилась → возвращаем SUCCESS,
//   а SL/TP СТАВИТ PositionSupervisorService v8.1 (NORMAL → ALGO RAW)
// - OrderExecutor НЕ ставит SL/TP вообще.

// ============================================================================

using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Configuration;
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
        private readonly MarketDataFacade _marketDataFacade;
        private readonly AiLiquidityClusterService _liquidityClusterService;


        // ===== ENTRY EXECUTION TUNING (PRODUCTION) =====

        private const decimal AGGR_LIMIT_OFFSET_PCT = 0.0006m;  // 0.06% агрессивный лимит (тюнится)
        private const decimal MARKET_FALLBACK_MAX_SLIP_PCT = 0.0015m; // 0.15% макс. слип для fallback-market
        private bool? _isHedgeMode;
        private const int MAX_ENTRIES_PER_SYMBOL = 2;
        private const decimal MAX_LIMIT_STALE_DRIFT = 0.0047m; // 0.45%
        private readonly IOptionsMonitor<TradingSettings> _tradingSettings;
        private readonly IOptionsMonitor<TradingOptions> _tradingOptions;

        public class EntryTracker
        {
            private readonly ConcurrentDictionary<string, int> _entries = new();

            public int GetEntries(string symbol, PositionSide side)
            {
                return _entries.GetOrAdd(Key(symbol, side), 0);
            }

            public void RegisterEntry(string symbol, PositionSide side)
            {
                _entries.AddOrUpdate(
                    Key(symbol, side),
                    1,
                    (_, v) => v + 1);
            }

            public void Reset(string symbol, PositionSide side)
            {
                _entries.TryRemove(Key(symbol, side), out _);
            }

            public bool TryReserveSlot(string symbol, PositionSide side, int max)
            {
                var key = Key(symbol, side);

                return _entries.AddOrUpdate(
                    key,
                    1,
                    (_, v) =>
                    {
                        if (v >= max) return v;
                        return v + 1;
                    }) <= max;
            }

            private string Key(string s, PositionSide p) => $"{s}_{p}";
        }

        public class CooldownGuard
        {
            private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();

            public bool IsActive(string key)
            {
                if (_cooldowns.TryGetValue(key, out var ts))
                    return DateTime.UtcNow < ts;

                return false;
            }

            public void Activate(string key, TimeSpan duration)
            {
                _cooldowns[key] = DateTime.UtcNow.Add(duration);
            }
        }
        private readonly EntryTracker _entryTracker;
        private readonly CooldownGuard _cooldown;

        public OrderExecutor(
            ILogger<OrderExecutor> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            SimulatedTradeService simulator,
            ExecutedSignalService executedSignalService,
            MarketDataService marketData,
            AiMarketRegimeService marketRegimeService,
            SmartRegimeService smartRegime,
            LiquidityGuardService liquidityGuard, AiSelfLearningService ai, RiskManager risk,
            MarketDataFacade marketDataFacade,
            IOptionsMonitor<TradingSettings> tradingSettings, 
            AiLiquidityClusterService liquidityClusterService,
            EntryTracker entryTracker,
            CooldownGuard cooldown, IOptionsMonitor<TradingOptions> tradingOptions)
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
            _marketDataFacade = marketDataFacade;
            _tradingSettings = tradingSettings;
            _liquidityClusterService = liquidityClusterService;
            _entryTracker = entryTracker;
            _cooldown = cooldown;
            _tradingOptions = tradingOptions;
        }

        public async Task<bool> ConfirmEntryOn1m(
        string symbol,
        SignalSide side,
        CancellationToken ct)
        {
            var klines1m = await _marketDataFacade.GetKlinesAsync(symbol, KlineInterval.OneMinute, 50);

            var last = klines1m[^1];
            var prev = klines1m[^2];

            // пример простой логики (потом усложним)
            if (side == SignalSide.Buy)
                return last.ClosePrice > prev.HighPrice; // micro breakout

            if (side == SignalSide.Sell)
                return last.ClosePrice < prev.LowPrice;

            return false;
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
       CancellationToken ct = default, decimal leverage = 1m)
        {
            using var client = _factory.CreateRestClient();

            bool isToxicSymbol =
                _tradingSettings.CurrentValue.ToxicSymbols
                .Contains(signal.Symbol);

            var isHedge = await IsHedgeModeAsync(client, ct);

            _logger.LogInformation("[ACCOUNT] Futures position mode: {mode}", isHedge ? "HEDGE" : "ONE-WAY");

            var side = signal.Side == SignalSide.Buy ? OrderSide.Buy : OrderSide.Sell;
            var posSide = signal.Side == SignalSide.Buy ? PositionSide.Long : PositionSide.Short;

            var key = $"{signal.Symbol}_{posSide}";

            // =============================================================
            // ENTRY CONTROL (NEW — STRICT)
            // =============================================================

            if (_cooldown.IsActive(key))
            {
                _logger.LogWarning(
                    "[ENTRY BLOCKED][{symbol}] cooldown active",
                    signal.Symbol);

                return OrderResult.Fail("COOLDOWN_ACTIVE");
            }

            int existingEntries = _entryTracker.GetEntries(signal.Symbol, posSide);

            if (existingEntries >= MAX_ENTRIES_PER_SYMBOL)
            {
                _logger.LogWarning(
                    "[ENTRY BLOCKED][{symbol}] max entries reached ({entries})",
                    signal.Symbol,
                    existingEntries);

                return OrderResult.Fail("MAX_ENTRIES_REACHED");
            }

            // =============================================================
            // POSITION SNAPSHOT (только для averaging, НЕ для entry count)
            // =============================================================

            BinancePositionDetailsUsdt? sameSidePosition = null;

            var posCheck = await client.UsdFuturesApi.Account
                .GetPositionInformationAsync(signal.Symbol, ct: ct);

            if (posCheck.Success && posCheck.Data != null)
            {
                sameSidePosition = posCheck.Data.FirstOrDefault(p =>
                    Math.Abs(p.Quantity) > 0.00000001m &&
                    (
                        (signal.Side == SignalSide.Buy &&
                            (p.PositionSide == PositionSide.Long || p.PositionSide == PositionSide.Both)) ||

                        (signal.Side == SignalSide.Sell &&
                            (p.PositionSide == PositionSide.Short || p.PositionSide == PositionSide.Both))
                    ));
            }

            // =============================================================
            // AVERAGING CONTROL (FIXED)
            // =============================================================

            decimal lastPrice = _marketDataFacade.GetLastPrice(signal.Symbol);
            var filters = await _symbolInfo.GetFuturesFiltersAsync(signal.Symbol);
            var tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            if (existingEntries == 1 && sameSidePosition != null)
            {
                decimal avgEntry = sameSidePosition.EntryPrice;

                decimal delta = (signal.Atr ?? lastPrice * 0.001m);
                delta = Math.Max(delta, tick * 2);

                bool badAveraging =
                    (signal.Side == SignalSide.Buy && lastPrice >= avgEntry + delta) ||
                    (signal.Side == SignalSide.Sell && lastPrice <= avgEntry - delta);

                if (badAveraging)
                {
                    _logger.LogInformation(
                        "[AVERAGING BLOCKED][{symbol}] price not favorable avg={avg} last={last}",
                        signal.Symbol,
                        avgEntry,
                        lastPrice);

                    return OrderResult.Fail("AVERAGING_BAD_PRICE");
                }
            }

            // =============================================================
            // SYMBOL FILTERS
            // =============================================================

            var step = filters.step <= 0 ? 0.0001m : filters.step;
            var minQty = filters.minQty;
            var minNotional = filters.minNotional;

            quantity = Math.Floor(quantity / step) * step;
            quantity = Math.Max(quantity, minQty);

            decimal notional = quantity * signal.EntryPrice;

            // =============================================================
            // VALIDATIONS
            // =============================================================

            if (quantity < minQty || notional < minNotional)
            {
                decimal requiredNotional = Math.Max(minNotional, minQty * signal.EntryPrice);

                if (_risk.LastBalanceUsdt * leverage >= requiredNotional)
                {
                    quantity = Math.Max(quantity, minQty);

                    // 🔥 ДОБАВЬ ЭТО
                    if (quantity * signal.EntryPrice < minNotional)
                    {
                        quantity = Math.Ceiling(minNotional / signal.EntryPrice / step) * step;
                    }

                    notional = quantity * signal.EntryPrice;

                    if (notional < minNotional)
                    {
                        var reason = $"QTY_TOO_SMALL_AFTER_BUMP | qty={quantity} notional={notional} minNotional={minNotional}";
                        await _simulator.AppendLifecycleEventAsync(
                            signal,
                            stage: "PREFILTER_REJECT",
                            reason: reason,
                            attemptNotional: notional,
                            requiredMinNotional: minNotional);

                        return OrderResult.Fail(reason);
                    }

                    _logger.LogInformation(
                        "[EXEC][{symbol}] Quantity bumped up to minQty",
                        signal.Symbol);
                }
                else
                {
                    var reason = $"QTY_TOO_SMALL | qty={quantity} minQty={minQty} notional={notional} minNotional={minNotional}";
                    await _simulator.AppendLifecycleEventAsync(
                        signal,
                        stage: "PREFILTER_REJECT",
                        reason: reason,
                        attemptNotional: notional,
                        requiredMinNotional: minNotional);

                    return OrderResult.Fail(reason);
                }
            }

            if (notional < minNotional)
            {
                var reason = $"MIN_NOTIONAL | notional={notional} minNotional={minNotional}";

                await _simulator.AppendLifecycleEventAsync(
                    signal,
                    stage: "PREFILTER_REJECT",
                    reason: reason,
                    attemptNotional: notional,
                    requiredMinNotional: minNotional);

                return OrderResult.Fail(reason);
            }

            decimal entryPrice = Quantize(signal.EntryPrice, tick);


            // =============================================================
            // MULTI TF MARKET STRUCTURE
            // =============================================================

            // signal TF (1m)
            var signalKlines = await _marketDataFacade.GetKlinesAsync(
                signal.Symbol,
                KlineInterval.OneMinute,
                200);

            // trend TF (5m)
            var trendKlines = await _marketDataFacade.GetKlinesAsync(
                signal.Symbol,
                KlineInterval.FiveMinutes,
                200);

            // regime считается по 5m
            var baseReg = _marketRegimeService.DetectRegime(
                signal.Symbol,
                KlineInterval.FiveMinutes,
                trendKlines);

            // smart regime тоже по 5m
            var smart = _smartRegime.Evaluate(
                signal.Symbol,
                KlineInterval.FiveMinutes,
                trendKlines);

            // =============================================================
            // TOXIC SYMBOL GUARD (RIVER / similar manipulation assets)
            // =============================================================

            if (isToxicSymbol)
            {
                var last = signalKlines.Last();
                var prev = signalKlines[^2];

                decimal move =
     prev.ClosePrice > 0
         ? (last.ClosePrice - signalKlines[^3].ClosePrice) / signalKlines[^3].ClosePrice
         : 0m;

                bool bigDump = move <= -0.030m; // было -5%
                bool bigPump = move >= 0.035m;
                // 🚫 BLOCK SHORT IF 2 RED CANDLES IN A ROW (classic trap)
                if (signal.Side == SignalSide.Sell && signalKlines.Count >= 3)
                {
                    var c1 = signalKlines[^1];
                    var c2 = signalKlines[^2];

                    if (c1.ClosePrice < c1.OpenPrice &&
                        c2.ClosePrice < c2.OpenPrice)
                    {
                        _logger.LogWarning(
                            "[TOXIC BLOCK][{symbol}] double red short trap",
                            signal.Symbol);

                        return OrderResult.Fail("DOUBLE_RED_TRAP");
                    }
                }

                if (signal.Side == SignalSide.Buy && signalKlines.Count >= 3)
                {
                    var c1 = signalKlines[^1];
                    var c2 = signalKlines[^2];

                    if (c1.ClosePrice > c1.OpenPrice &&
                        c2.ClosePrice > c2.OpenPrice)
                    {
                        _logger.LogWarning(
                            "[TOXIC BLOCK][{symbol}] double green long trap",
                            signal.Symbol);

                        return OrderResult.Fail("DOUBLE_GREEN_TRAP");
                    }
                }
                // ------------------------------------------
                // SHORT TRAP PROTECTION
                // ------------------------------------------

                if (signal.Side == SignalSide.Sell && bigDump)
                {
                    _logger.LogWarning(
                        "[TOXIC BLOCK][{symbol}] short after dump trap",
                        signal.Symbol);

                    await _simulator.SimulateMissedTradeAsync(
                        signal,
                        "ToxicShortTrap",
                        note: $"move={move:P2}",
                        attemptNotional: quantity * signal.EntryPrice,
                        requiredMinNotional: 0);

                    return OrderResult.Fail("TOXIC_SHORT_TRAP");
                }

                // ------------------------------------------
                // LONG FOMO PROTECTION
                // ------------------------------------------

                if (signal.Side == SignalSide.Buy && bigPump)
                {
                    _logger.LogWarning(
                        "[TOXIC BLOCK][{symbol}] long after pump trap",
                        signal.Symbol);

                    await _simulator.SimulateMissedTradeAsync(
                        signal,
                        "ToxicLongTrap",
                        note: $"move={move:P2}",
                        attemptNotional: quantity * signal.EntryPrice,
                        requiredMinNotional: 0);

                    return OrderResult.Fail("TOXIC_LONG_TRAP");
                }

                // ------------------------------------------
                // leverage reduction
                // ------------------------------------------

                leverage = Math.Min(leverage, 2m);

                _logger.LogInformation(
                    "[TOXIC MODE][{symbol}] leverage limited to {lev}",
                    signal.Symbol,
                    leverage);
            }

            bool isSmartStrongTrend =
                smart.BaseRegime == MarketRegime.StrongUpTrend ||
                smart.BaseRegime == MarketRegime.StrongDownTrend;

            bool hasImpulse = IsImpulse(signalKlines, signal.Atr ?? 0m);

            // =====================================================
            // POST IMPULSE TRAPS (CRITICAL FIX)
            // =====================================================

            // SHORT TRAP
            bool postImpulseTrap =
                hasImpulse &&
                signal.Side == SignalSide.Sell &&
                signalKlines[^1].ClosePrice < signalKlines[^1].OpenPrice;

            if (postImpulseTrap)
            {
                _logger.LogWarning(
                    "[TRAP BLOCK][{symbol}] short after impulse sweep",
                    signal.Symbol);

                return OrderResult.Fail("POST_IMPULSE_TRAP");
            }

            var lastCandle = signalKlines[^1];

            decimal body = Math.Abs(lastCandle.ClosePrice - lastCandle.OpenPrice);
            decimal range = lastCandle.HighPrice - lastCandle.LowPrice;

            bool isStrongGreen = range > 0 && (body / range) > 0.7m;

            bool postImpulseLongTrap =
                hasImpulse &&
                signal.Side == SignalSide.Buy &&
                isStrongGreen;

            if (postImpulseLongTrap)
            {
                _logger.LogWarning(
                    "[TRAP BLOCK][{symbol}] long after impulse pump",
                    signal.Symbol);

                return OrderResult.Fail("POST_IMPULSE_LONG_TRAP");
            }

            // =====================================================
            // ADAPTIVE MICRO PULLBACK ENTRY (PRO VERSION)
            // =====================================================

            if ((signal.Atr ?? 0m) > 0 && entryPrice > 0)
            {
                // =====================================================
                // ADAPTIVE MICRO PULLBACK ENTRY (CALIBRATED)
                // =====================================================

                decimal atr = signal.Atr.Value;

                decimal microPullback;

                if (smart.EntryProfile == "CT")
                    microPullback = atr * 0.40m;
                else if (hasImpulse)
                    microPullback = atr * 0.23m;
                else if (isSmartStrongTrend)
                    microPullback = atr * 0.27m;
                else
                    microPullback = atr * 0.31m;

                // safety clamp
                decimal maxPullback = entryPrice * 0.0045m; // 0.45%
                microPullback = Math.Min(microPullback, maxPullback);

                decimal finalEntryPrice = entryPrice;

                if (signal.Side == SignalSide.Buy)
                    finalEntryPrice -= microPullback;
                else
                    finalEntryPrice += microPullback;

                finalEntryPrice = Quantize(finalEntryPrice, tick); // Quantize только один раз

                entryPrice = finalEntryPrice;

                // защита от отрицательных цен
                if (entryPrice <= 0)
                    entryPrice = signal.EntryPrice;

                // quantize к tick
                entryPrice = Quantize(entryPrice, tick);

                _logger.LogDebug(
                    "[PULLBACK][{symbol}] side={side} pullback={pb} ATR={atr}",
                    signal.Symbol,
                    signal.Side,
                    microPullback,
                    atr);
            }
            // =============================================================
            // LAST PRICE (SAFE, RATE-LIMIT AWARE)
            // =============================================================
          
            if (lastPrice <= 0)
            {
                lastPrice = entryPrice; // fallback safety
            }
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
            // LIQUIDITY GUARD (FAIL-SAFE)
            // =============================================================
            LiquidityGuardResult liquidityResult;
            // =============================================================
            // LIQUIDITY CLUSTER (EXECUTION-TIME REFINEMENT)
            // =============================================================
            try
            {
                var adjusted = await _liquidityClusterService
                    .FilterAndAdjustAsync(signal, ct);

                if (adjusted == null)
                {
                    _logger.LogWarning("[EXEC][{symbol}] cluster rejected at execution", signal.Symbol);
                    return OrderResult.Fail("LIQ_CLUSTER_EXEC_BLOCK");
                }

                signal.CopyFrom(adjusted);

            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EXEC][{symbol}] cluster fail-safe pass", signal.Symbol);
            }

            try
            {
                liquidityResult =  _liquidityGuard.Analyze(
                    signal.Symbol,
                    KlineInterval.OneMinute,
                    signalKlines,
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

           
            if (hasImpulse && !signal.IsSuperSignal)
            {
                _logger.LogWarning(
                  "[IMPULSE BLOCK][{symbol}] waiting pullback instead of chasing",
                  signal.Symbol);
                allowMarketEntry = false;
            }

            // =============================================================
            // APPLY SOFT LIQUIDITY SIZE MULTIPLIER (EARLY EXPANSION FIX)
            // =============================================================
            var sizeMul = signal.SizeMultiplier <= 0 ? 1.0m : signal.SizeMultiplier;

            // hard clamp — защита от глупостей
            sizeMul = Math.Clamp(sizeMul, 0.25m, 1.0m);

            // сначала multiply
            quantity *= sizeMul;

            // затем округление
            quantity = Math.Floor(quantity / step) * step;
            // затем проверка minQty / minNotional один раз
            quantity = Math.Max(quantity, minQty);
              notional = quantity * entryPrice;

            if (notional < minNotional)
            {
                quantity = Math.Max(quantity, minNotional / entryPrice);
                notional = quantity * entryPrice;
            }
            // =============================================================
            // EXECUTED SIGNAL CREATED
            // =============================================================
            decimal executionBias = Math.Clamp(smart.RiskBias * liquidityResult.Score, 0.4m, 1.0m);
            decimal adjustedQty = Math.Floor((quantity * executionBias) / step) * step;

            if (adjustedQty < filters.minQty)
            {
                decimal potentialNotional = filters.minQty * entryPrice;

                // Если qty чуть меньше minQty (>=85%), поднимаем до minQty
                if (adjustedQty >= filters.minQty * 0.85m)
                {
                    adjustedQty = filters.minQty;
                }
                // Иначе — если баланс позволяет, поднимаем для мелких альтов
                else if (_risk.LastBalanceUsdt * leverage >= potentialNotional)
                {
                    adjustedQty = filters.minQty;
                    _logger.LogInformation(
                        "[EXEC][{symbol}] AdjustedQty bumped up to minQty={minQty} due to small alt",
                        signal.Symbol, filters.minQty);
                }
                else
                {
                    // Всё равно не хватает → отказ
                    await _simulator.SimulateMissedTradeAsync(
                        signal,
                        "ADJUSTED_QTY_TOO_SMALL",
                        note: $"baseQty={quantity}; bias={executionBias:F2}; riskBias={smart.RiskBias:F2}; liqScore={liquidityResult.Score:F2}",
                        freeBalance: _risk.LastBalanceUsdt,
                        attemptNotional: quantity * entryPrice,
                        requiredMinNotional: filters.minQty * entryPrice);

                    return OrderResult.Fail("ADJUSTED_QTY_TOO_SMALL");
                }
            }

            quantity = adjustedQty;
            notional = quantity * entryPrice;

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
                    requiredMinNotional: filters.minQty * entryPrice);

                return OrderResult.Fail($"LiquidityGuard:{liquidityResult.Reason}");
            }
            // =====================================================
            // FINAL MICRO TIMING (1M CONFIRMATION)
            // =====================================================
            if (!await ConfirmEntryOn1m(signal.Symbol, signal.Side, ct))
            {
                _logger.LogInformation(
                    "[1M BLOCK][{symbol}] bad micro timing",
                    signal.Symbol);

                return OrderResult.Fail("BAD_1M_TIMING");
            }

            // =============================================================
            // AI SELF-LEARNING GUARD (PnL-based kill-switch)
            // =============================================================
            var recentPnl = _ai.GetRecentPnL(signal.Symbol, signal.Side, 10);

            if (recentPnl < -0.03m)
            {
                _logger.LogWarning(
                    "[AI BLOCK][{symbol}] Negative recent PnL {pnl}",
                    signal.Symbol,
                    recentPnl);

                await _simulator.SimulateMissedTradeAsync(
                    signal,
                    "AI_PNL_BLOCK",
                    note: $"recentPnL={recentPnl:P2}",
                    attemptNotional: quantity * entryPrice,
                    requiredMinNotional: 0m);

                return OrderResult.Fail("AI_PNL_BLOCK");
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

            // =====================================================
            // DYNAMIC SPREAD GUARD (pro bots protection)
            // =====================================================

            decimal spreadPct = 0m;
            bool imbalanceBlock = false;
            try
            {
                var book = await client.UsdFuturesApi.ExchangeData.GetOrderBookAsync(signal.Symbol, 10, ct);

                if (book.Success &&
                    book.Data != null &&
                    book.Data.Bids.Any() &&
                    book.Data.Asks.Any())
                {
                    var bestBid = book.Data.Bids.First().Price;
                    var bestAsk = book.Data.Asks.First().Price;

                    //if (bestBid > 0 && bestAsk > 0)
                    //    spreadPct = (bestAsk - bestBid) / bestBid;
                    if (bestBid > 0 && bestAsk > 0)
                    {
                        var mid = (bestBid + bestAsk) / 2m;
                        spreadPct = mid > 0 ? (bestAsk - bestBid) / mid : 0m;
                    }
                    // =====================================================
                    // ORDER BOOK IMBALANCE FILTER
                    // =====================================================

                    decimal bidVol = book.Data.Bids.Sum(b => b.Quantity);
                    decimal askVol = book.Data.Asks.Sum(a => a.Quantity);

                    decimal imbalance = 0.5m;

                    if (bidVol + askVol > 0)
                        imbalance = bidVol / (bidVol + askVol);

                    // detect fake imbalance (walls flipping)
                    bool fakeImbalance =
                        signal.Side == SignalSide.Sell && imbalance > 0.58m && hasImpulse ||
                        signal.Side == SignalSide.Buy && imbalance < 0.42m && hasImpulse;

                    if (fakeImbalance)
                    {
                        _logger.LogWarning(
                            "[FAKE IMBALANCE][{symbol}] possible trap",
                            signal.Symbol);

                        return OrderResult.Fail("FAKE_IMBALANCE_TRAP");
                    }



                    decimal buyThreshold = isToxicSymbol ? 0.45m : 0.40m;
                    decimal sellThreshold = isToxicSymbol ? 0.55m : 0.60m;

                    if (signal.Side == SignalSide.Buy && imbalance < buyThreshold)
                        imbalanceBlock = true;

                    if (signal.Side == SignalSide.Sell && imbalance > sellThreshold)
                        imbalanceBlock = true;
                   

                    if (signal.Side == SignalSide.Sell && imbalance > 0.60m)
                        imbalanceBlock = true;

                    if (!signal.IsSuperSignal)
                    {
                        if (imbalanceBlock)
                        {
                            _logger.LogWarning(
                           "[IMBALANCE BLOCK][{symbol}] side={side} imbalance={imbalance:F2}",
                           signal.Symbol,
                           signal.Side,
                           imbalance);

                            await _simulator.SimulateMissedTradeAsync(
                                signal,
                                "OrderBookImbalance",
                                note: $"imbalance={imbalance:F2}",
                                attemptNotional: notional,
                                requiredMinNotional: 0m);

                            return OrderResult.Fail("ORDERBOOK_IMBALANCE");
                        }
                           
                    }
                }
            }
            catch { }

            string failReason = null;
            // адаптивный лимит spread
            decimal maxSpread =
                (signal.Atr ?? 0m) > 0 && lastPrice > 0
                    ? Math.Clamp((signal.Atr.Value / lastPrice) * 0.20m, 0.0004m, 0.0020m)
                    : 0.0010m;

            if (imbalanceBlock && !signal.IsSuperSignal)
                failReason = "ORDERBOOK_IMBALANCE";
            else if (!signal.IsSuperSignal && spreadPct > maxSpread)
                failReason = "SPREAD_TOO_WIDE";
            else if (liquidityResult.Block)
                failReason = $"LiquidityGuard:{liquidityResult.Reason}";

            if (failReason != null)
            {
                await _simulator.SimulateMissedTradeAsync(
                    signal,
                    failReason,
                    attemptNotional: notional,
                    requiredMinNotional: filters.minQty * entryPrice
                );
                return OrderResult.Fail(failReason);
            }

          

            // блокируем вход
            if (!signal.IsSuperSignal && spreadPct > maxSpread)
            {
                _logger.LogWarning(
                    "[SPREAD BLOCK][{symbol}] spread={spread:P3} > max={max:P3}",
                    signal.Symbol,
                    spreadPct,
                    maxSpread);

                await _simulator.SimulateMissedTradeAsync(
                    signal,
                    "SpreadTooWide",
                    note: $"spread={spreadPct:P3}; max={maxSpread:P3}",
                    attemptNotional: notional,
                    requiredMinNotional: 0m);

                return OrderResult.Fail("SPREAD_TOO_WIDE");
            }

            // =====================================================
            // B) LIMIT PRICING
            // =====================================================

            // PASSIVE price must stay outside market
            decimal passiveLimit;

            if (side == OrderSide.Buy)
            {
                passiveLimit = lastPrice - tick;
            }
            else
            {
                passiveLimit = lastPrice + tick;
            }

            // =====================================================
            // CONTROLLED JOIN: catch “silent trend” safely
            // =====================================================

            // spread proxy через tick
           
            bool spreadOk = spreadPct <= maxSpread;

            bool silentTrendJoin =
     entryType == FuturesOrderType.Limit &&
     allowMarketEntry &&
     !hasImpulse &&
     isSmartStrongTrend &&
     rrOk &&
     liquiditySafe &&
     !liquidityResult.IsExtreme &&
     spreadPct <= maxSpread &&
     baseReg.TrendSlopePercent >= 0.25m &&
     priceDriftPct >= 0.0025m &&
    priceDriftPct <= 0.008m;

            // =====================================================
            // ADAPTIVE SAFE DRIFT
            // =====================================================

            decimal baseDriftPct = AGGR_LIMIT_OFFSET_PCT; // ~0.06%
            decimal superDriftPct = 0.0015m;               // 0.15%
            decimal hardCapPct = 0.0018m;                  // 0.18%

            decimal atrPct = 0m;

            if ((signal.Atr ?? 0m) > 0 && lastPrice > 0)
                atrPct = signal.Atr.Value / lastPrice;

            decimal liquidityFactor = Math.Clamp(liquidityResult.Score, 0.4m, 1.2m);

            decimal adaptiveDrift =
                Math.Clamp(
                    atrPct * 0.35m * liquidityFactor,
                    baseDriftPct,
                    hardCapPct);

            decimal safeDriftPct =
                signal.IsSuperSignal
                    ? Math.Min(adaptiveDrift * 1.3m, hardCapPct)
                    : adaptiveDrift;

            // =====================================================
            // JOIN LIMIT CALCULATION
            // =====================================================

            decimal joinLimitRaw =
                side == OrderSide.Buy
                    ? lastPrice * (1m + safeDriftPct)
                    : lastPrice * (1m - safeDriftPct);

            // anti-chase guard
            decimal maxJoinDrift;

            if (isToxicSymbol)
                maxJoinDrift = signal.IsSuperSignal ? 0.0025m : 0.0012m;
            else
                maxJoinDrift = signal.IsSuperSignal ? 0.004m : 0.0025m;

            if (entryPrice > 0)
            {
                if (side == OrderSide.Buy)
                    joinLimitRaw = Math.Min(joinLimitRaw, entryPrice * (1m + maxJoinDrift));
                else
                    joinLimitRaw = Math.Max(joinLimitRaw, entryPrice * (1m - maxJoinDrift));
            }

            // =====================================================
            // FINAL LIMIT SELECTION
            // =====================================================

            decimal finalLimitRaw =
                (silentTrendJoin || signal.IsSuperSignal)
                    ? joinLimitRaw
                    : passiveLimit;

            // safety guard vs zero / negative price
            if (finalLimitRaw <= 0)
                finalLimitRaw = lastPrice;

            // =====================================================
            // TICK QUANTIZATION (ONLY ONCE)
            // =====================================================

            decimal aggrLimitPrice = Quantize(finalLimitRaw, tick);

            // =====================================================
            // FINAL ORDER PARAMS
            // =====================================================
            decimal? orderPrice = entryType == FuturesOrderType.Market ? null : aggrLimitPrice;
            TimeInForce? tif = entryType == FuturesOrderType.Market ? null : TimeInForce.GoodTillCanceled;

      

            // =====================================================
            // LOG
            // =====================================================

            _logger.LogInformation(
                "[ORDER][{symbol}] ENTRY={type} mode={mode} orderPrice={orderPrice} last={last} " +
                "driftPct={driftPct:P3} priceDrift={priceDrift:P2} slope={slope:F3}% " +
                "impulse={imp} silentJoin={silent} super={super} liqExt={ext} liqScore={score:F2}",
                signal.Symbol,
                entryType,
                entryType == FuturesOrderType.Market
                    ? "MARKET"
                    : (silentTrendJoin ? "JOIN_LIMIT" : "PASSIVE_LIMIT"),
                orderPrice,
                lastPrice,
                safeDriftPct,
                priceDriftPct,
                baseReg.TrendSlopePercent,
                hasImpulse,
                silentTrendJoin,
                signal.IsSuperSignal,
                liquidityResult.IsExtreme,
                liquidityResult.Score
            );


            // =============================================================
            // APPLY CORRECT QTY RULE PER ORDER TYPE (MARKET vs LIMIT)
            // =============================================================

            var qtyRule = entryType == FuturesOrderType.Market
                ? SymbolInfoService.QtyRule.Market
                : SymbolInfoService.QtyRule.Limit;

            var fQty = await _symbolInfo.GetFuturesFiltersAsync(signal.Symbol, qtyRule);

            // safety step
            var stepRule = fQty.step > 0 ? fQty.step : step;

            // price used for notional calculation
            decimal notionalPrice =
                orderPrice ??
                (lastPrice > 0 ? lastPrice : entryPrice);

            if (notionalPrice <= 0)
                return OrderResult.Fail("INVALID_PRICE_FOR_NOTIONAL");

            // normalize quantity
            quantity = Math.Floor(quantity / stepRule) * stepRule;
            quantity = Math.Max(quantity, fQty.minQty);

            decimal finalNotional = quantity * notionalPrice;

            // =============================================================
            // MIN NOTIONAL GUARD
            // =============================================================

            if (finalNotional < fQty.minNotional)
            {
                if (_risk.LastBalanceUsdt * leverage >= fQty.minNotional)
                {
                    quantity = fQty.minNotional / notionalPrice;

                    // округление вверх до шага
                    quantity = Math.Ceiling(quantity / stepRule) * stepRule;

                    quantity = Math.Max(quantity, fQty.minQty);

                    finalNotional = quantity * notionalPrice;
                }
                else
                {
                    await _simulator.SimulateMissedTradeAsync(
                        signal,
                        "QTY_TOO_SMALL_AFTER_RULE",
                        note: $"entryType={entryType}; qtyRule={qtyRule}; qty={quantity}; minQty={fQty.minQty}; step={stepRule}",
                        attemptNotional: finalNotional,
                        requiredMinNotional: fQty.minNotional);

                    _executedSignalService.UpdateStatus(
                        signal.Symbol,
                        execTime,
                        TradeExecutionStatus.Blocked,
                        0,
                        0);

                    return OrderResult.Fail("QTY_TOO_SMALL_AFTER_RULE");
                }
            }

            // =============================================================
            // FINAL NOTIONAL AT ORDER CREATE
            // =============================================================

            decimal markForNotional =
                lastPrice > 0 ? lastPrice : entryPrice;

            decimal notionalAtCreate =
                orderPrice.HasValue
                    ? quantity * orderPrice.Value
                    : quantity * markForNotional;

            // =============================================================
            // FINAL ORDER PARAMS
            // =============================================================

            orderPrice = entryType == FuturesOrderType.Market
                ? null
                : aggrLimitPrice;

            tif = entryType == FuturesOrderType.Market
                ? null
                : TimeInForce.GoodTillCanceled;

            // =============================================================
            // HARD GUARDS
            // =============================================================

            if (entryType == FuturesOrderType.Market && orderPrice != null)
                return OrderResult.Fail("InvalidMarketOrderWithPrice");

            if (entryType == FuturesOrderType.Limit && orderPrice == null)
                return OrderResult.Fail("InvalidLimitOrderWithoutPrice");

            // =============================================================
            // PLACE ENTRY
            // =============================================================
            if (!_tradingOptions.CurrentValue.EnableExecution)
            {
                _logger.LogWarning(
                    "[DRY-RUN][{symbol}] Execution disabled → signal processed but NO order sent",
                    signal.Symbol);

                await _simulator.SimulateMissedTradeAsync(
                    signal,
                    "DRY_RUN_EXECUTION_DISABLED",
                    note: $"entryType={entryType}; qty={quantity}; price={(orderPrice?.ToString() ?? "MKT")}",
                    attemptNotional: notionalAtCreate,
                    requiredMinNotional: 0m);

                _executedSignalService.UpdateStatus(
                    signal.Symbol,
                    execTime,
                    TradeExecutionStatus.Blocked,
                    0,
                    0);

                return OrderResult.Fail("EXECUTION_DISABLED");
            }
            var entryRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: signal.Symbol,
                side: side,
                type: entryType,
                quantity: quantity,
                price: orderPrice,
                positionSide: isHedge ? posSide : null,
                timeInForce: tif,
                selfTradePreventionMode: SelfTradePreventionMode.ExpireMaker,
                reduceOnly: null,
                ct: ct);

            // =============================================================
            // SAFE RETRY (LIMIT ONLY)
            // =============================================================

            if (!entryRes.Success && entryType == FuturesOrderType.Limit)
            {
                var errCode = entryRes.Error?.Code;

                bool retryable =
                    errCode == -2010 ||   // order rejected
                    errCode == -2021 ||   // immediate trigger
                    errCode == -4164;     // price filter

                if (retryable)
                {
                    try
                    {
                        var priceResp = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(signal.Symbol, ct: ct);

                        if (priceResp.Success && priceResp.Data?.Price > 0)
                        {
                            var last = priceResp.Data.Price;

                            var retryPrice = side == OrderSide.Buy
                                ? last - tick
                                : last + tick;

                            if (retryPrice <= 0)
                                retryPrice = last;

                            retryPrice = Quantize(retryPrice, tick);

                            // anti-chase protection
                            decimal maxDrift = 0.002m; // 0.2%

                            if (entryPrice > 0)
                            {
                                if (side == OrderSide.Buy)
                                    retryPrice = Math.Min(retryPrice, entryPrice * (1m + maxDrift));
                                else
                                    retryPrice = Math.Max(retryPrice, entryPrice * (1m - maxDrift));
                            }

                            _logger.LogWarning(
                                "[ORDER][{symbol}] Retry LIMIT after reject. newPrice={price}",
                                signal.Symbol,
                                retryPrice);

                            entryRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                                symbol: signal.Symbol,
                                side: side,
                                type: FuturesOrderType.Limit,
                                quantity: quantity,
                                price: retryPrice,
                                positionSide: isHedge ? posSide : null,
                                timeInForce: TimeInForce.GoodTillCanceled,
                                selfTradePreventionMode: SelfTradePreventionMode.ExpireMaker,
                                ct: ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[ORDER][{symbol}] Retry attempt failed", signal.Symbol);
                    }
                }
            }
            if (!entryRes.Success || entryRes.Data == null)
            {
                var note = entryRes.Error != null
                    ? $"code={entryRes.Error.Code}; msg={entryRes.Error.Message}"
                    : "no_error_object";

                await _simulator.SimulateMissedTradeAsync(
                    signal,
                    "EntryError",
                    note: note,
                    attemptNotional: Math.Max(notionalAtCreate, notional),
                    requiredMinNotional: 0m);

                _executedSignalService.UpdateStatus(
                    signal.Symbol,
                    execTime,
                    TradeExecutionStatus.OrderPartiallyFilled,
                    0,
                    0);

                return OrderResult.Fail("ENTRY_FAILED");
            }

            long entryOrderId = entryRes.Data.Id;

            _executedSignalService.UpdateStatus(
                signal.Symbol,
                execTime,
                TradeExecutionStatus.OrderCreated,
                quantity,
                notionalAtCreate);

            await _simulator.AppendLifecycleEventAsync(signal, "ORDER_CREATED", freeBalance: _risk.LastBalanceUsdt);

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

            //============new guard for limit order 
            // =====================================================
            // STALE LIMIT ORDER GUARD
            // =====================================================

            if (!wait.HasPosition && entryType == FuturesOrderType.Limit)
            { // проверяем, не пришла ли частичная позиция
                if (wait.Qty > 0)
                {
                    _logger.LogInformation("[LIMIT_PARTIAL_FILL] qty={0}", wait.Qty);
                    return OrderResult.Successs(wait.EntryPrice, wait.Qty, entryOrderId);
                }

                try
                {
                    var px = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(signal.Symbol, ct: ct);

                    if (px.Success && px.Data?.Price > 0)
                    {
                        var now = px.Data.Price;

                        decimal drift =
                            entryPrice > 0
                            ? Math.Abs(now - entryPrice) / entryPrice
                            : 0m;

                        if (drift > MAX_LIMIT_STALE_DRIFT)
                        {
                            _logger.LogWarning(
                                "[STALE LIMIT][{symbol}] cancel stale order drift={drift:P2}",
                                signal.Symbol,
                                drift);

                            try
                            {
                                await client.UsdFuturesApi.Trading.CancelOrderAsync(
                                    signal.Symbol,
                                    entryOrderId,
                                    ct: ct);
                            }
                            catch { }

                            await _simulator.SimulateMissedTradeAsync(
                                signal,
                                "StaleLimitOrder",
                                note: $"drift={drift:P2}",
                                attemptNotional: notionalAtCreate,
                                requiredMinNotional: 0m);

                            return OrderResult.Fail("STALE_LIMIT_ORDER");
                        }
                    }
                }
                catch { }
            }
            //====================




            // ============================================================
            // LIMIT TIMEOUT → MARKET FALLBACK (SAFE EXECUTION BLOCK)
            // ============================================================
            if (isToxicSymbol)
            {
                _logger.LogWarning(
                    "[TOXIC BLOCK][{symbol}] market fallback disabled",
                    signal.Symbol);

                return OrderResult.Fail("TOXIC_NO_FALLBACK_MARKET");
            }
            if (!wait.HasPosition &&
                entryType == FuturesOrderType.Limit &&
                wait.Reason == "TimeoutNoFill" &&
                !marketFallbackUsed)
            {
                marketFallbackUsed = true;

                // ========================================================
                // RACE CONDITION GUARD
                // position might appear just after timeout
                // ========================================================

                var posRecheck = await client.UsdFuturesApi.Account.GetPositionInformationAsync(signal.Symbol, ct:ct);

                if (posRecheck.Success && posRecheck.Data != null)
                {
                    var existing = posRecheck.Data.FirstOrDefault(p =>
                        Math.Abs(p.Quantity) > 0.00000001m &&
                        (
                            (signal.Side == SignalSide.Buy &&
                                (p.PositionSide == PositionSide.Long || p.PositionSide == PositionSide.Both)) ||

                            (signal.Side == SignalSide.Sell &&
                                (p.PositionSide == PositionSide.Short || p.PositionSide == PositionSide.Both))
                        ));

                    if (existing != null)
                    {
                        _logger.LogWarning(
                            "[RACE-GUARD][{symbol}] position appeared during timeout → abort fallback",
                            signal.Symbol);

                        return OrderResult.Successs(existing.EntryPrice, Math.Abs(existing.Quantity), entryOrderId);
                    }
                }

                // ========================================================
                // cancel old limit
                // ========================================================

                try
                {
                    await client.UsdFuturesApi.Trading.CancelOrderAsync(signal.Symbol, entryOrderId, ct: ct);
                }
                catch { }

                // ========================================================
                // read fresh price
                // ========================================================

                decimal markNow = lastPrice;

                try
                {
                    var pxNow = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(signal.Symbol, ct: ct);

                    if (pxNow.Success && pxNow.Data?.Price > 0)
                        markNow = pxNow.Data.Price;
                }
                catch { }

                decimal lastNow = markNow;

                // =====================================================
                // SMART LIMIT RETRY BEFORE MARKET
                // =====================================================

                decimal retryPrice;

                if (signal.Side == SignalSide.Buy)
                    retryPrice = lastNow - tick;
                else
                    retryPrice = lastNow + tick;

                retryPrice = Quantize(retryPrice, tick);

                _logger.LogInformation(
                    "[LIMIT RETRY][{symbol}] retryPrice={price}",
                    signal.Symbol,
                    retryPrice);

                var retryOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: signal.Symbol,
                    side: side,
                    type: FuturesOrderType.Limit,
                    quantity: quantity,
                    price: retryPrice,
                    positionSide: isHedge ? posSide : null,
                    timeInForce: TimeInForce.GoodTillCanceled,
                    selfTradePreventionMode: SelfTradePreventionMode.ExpireMaker,
                    ct: ct);

                if (retryOrder.Success && retryOrder.Data != null)
                {
                    var retryWait = await WaitForPositionOrOrderAsync(
                        client,
                        signal,
                        posSide,
                        retryOrder.Data.Id,
                        lastNow,
                        quantity,
                        ct);

                    if (retryWait.HasPosition)
                    {
                        wait = retryWait;
                        entryOrderId = retryOrder.Data.Id;
                        return OrderResult.Successs(wait.EntryPrice, wait.Qty, entryOrderId);
                    }
                }
                // ========================================================
                // ATR SLIPPAGE CAP
                // ========================================================
                bool structureBroken = false;
                decimal atr = signal.Atr ?? 0m;


                decimal atrSlipCap =
                    (signal.Atr ?? 0m) > 0 && entryPrice > 0
                        ? Math.Min(
                            (atr / entryPrice) * 1.4m,
                            MARKET_FALLBACK_MAX_SLIP_PCT * (signal.IsSuperSignal ? 1.4m : 1m))
                        : MARKET_FALLBACK_MAX_SLIP_PCT;

                decimal driftNowPct =
                    entryPrice > 0
                        ? Math.Abs(lastNow - entryPrice) / entryPrice
                        : 0m;

                // ========================================================
                // STRUCTURE BREAK GUARD
                // ========================================================

             

                decimal structureThreshold =
                    atr > 0
                    ? Math.Max(atr * 0.35m, tick * 2)
                    : tick * 4;
                if (signal.Side == SignalSide.Buy)
                    structureBroken = lastNow < entryPrice - structureThreshold;
                else
                    structureBroken = lastNow > entryPrice + structureThreshold;

                

                if (structureBroken)
                {
                    _logger.LogWarning(
                        "[LATE-CANCEL][{symbol}] structure broken → cancel entry",
                        signal.Symbol);

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

                // ========================================================
                // LIQUIDITY / EXTREME MOVE GUARD
                // ========================================================

                bool allowFallbackMarket =
                    !liquidityResult.IsExtreme ||
                    (signal.IsSuperSignal && rrOk);

                if (!allowFallbackMarket || driftNowPct > atrSlipCap)
                {
                    await _simulator.SimulateMissedTradeAsync(
                        signal,
                        "FallbackMarketBlocked",
                        note: $"drift={driftNowPct:P2}; max={atrSlipCap:P2}; extreme={liquidityResult.IsExtreme}",
                        attemptNotional: notionalAtCreate,
                        requiredMinNotional: 0m);

                    _executedSignalService.UpdateStatus(
                        signal.Symbol,
                        execTime,
                        TradeExecutionStatus.Blocked,
                        0,
                        0);

                    return OrderResult.Fail("FALLBACK_MKT_BLOCKED");
                }

                // ========================================================
                // PREPARE MARKET QTY
                // ========================================================

                var fMkt = await _symbolInfo.GetFuturesFiltersAsync(
                    signal.Symbol,
                    SymbolInfoService.QtyRule.Market);

                var mktQty = Math.Floor(quantity / fMkt.step) * fMkt.step;

                if (mktQty < fMkt.minQty)
                {
                    mktQty = fMkt.minQty;

                    _logger.LogInformation(
                        "[FALLBACK MARKET][{symbol}] qty bumped to minQty={minQty}",
                        signal.Symbol,
                        fMkt.minQty);
                }

                if (mktQty < fMkt.minQty)
                    return OrderResult.Fail("FALLBACK_MKT_QTY_TOO_SMALL");

                // ========================================================
                // MARKET ENTRY
                // ========================================================
                if (!await ConfirmEntryOn1m(signal.Symbol, signal.Side, ct))
                {
                    _logger.LogInformation("Entry rejected by 1m timing");
                    return OrderResult.Fail("Entry rejected by 1m timing");
                }
                var mktRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: signal.Symbol,
                    side: side,
                    type: FuturesOrderType.Market,
                    quantity: mktQty,
                    positionSide: isHedge ? posSide : null,
                    selfTradePreventionMode: SelfTradePreventionMode.ExpireMaker,
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

            // ============================================================
            // FINAL CHECK
            // ============================================================

            if (!wait.HasPosition)
            {
                var reason = marketFallbackUsed
                    ? $"FallbackMarketFailed:{wait.Reason}"
                    : wait.Reason ?? "NotFilled";

                await _simulator.SimulateMissedTradeAsync(
                    signal,
                    reason,
                    note:
                    $"marketFallbackUsed={marketFallbackUsed}; " +
                    $"entryType={entryType}; " +
                    $"orderPrice={(orderPrice?.ToString() ?? "null")}; " +
                    $"lastPrice={lastPrice}; " +
                    $"entryPrice={entryPrice}; " +
                    $"waitReason={wait.Reason}",
                    attemptNotional: orderPrice.HasValue
                        ? quantity * orderPrice.Value
                        : notional,
                    requiredMinNotional: 0m);

                _executedSignalService.UpdateStatus(
                    signal.Symbol,
                    execTime,
                    TradeExecutionStatus.Blocked,
                    0,
                    0);

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

            filters = await _symbolInfo.GetFuturesFiltersAsync(signal.Symbol);
            tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;
            // =============================================================
            // CALCULATE TAKE PROFITS (IMMEDIATE)
            // =============================================================
            List<decimal> tps = new List<decimal>();

            if (signal.TakeProfits?.Any() != true && signal.StopLoss > 0)
            {
                // пример: один TP на RR = 1.5
                decimal rrTarget = 1.5m;
                decimal risk = Math.Abs(entryPrice - signal.StopLoss);
                decimal tpPrice;

                if (signal.Side == SignalSide.Buy)
                    tpPrice = entryPrice + risk * rrTarget;
                else
                    tpPrice = entryPrice - risk * rrTarget;

                // тик-квантование
               
                tpPrice = Quantize(tpPrice, tick);

                tps.Add(tpPrice);
            }
            else if (signal.TakeProfits != null && signal.TakeProfits.Any())
            {
                // берем оригинальные TP и квантуем к тик-сайзу
                 
                tps = signal.TakeProfits
                    .Select(tp => Quantize(tp, tick))
                    .ToList();
            }

            // сохраняем в сигнал для последующего использования
            signal.TakeProfits = tps;

            // лог для дебага
            _logger.LogInformation("[TP_CALC][{symbol}] entry={entry} TP={tps}", signal.Symbol, entryPrice, string.Join(", ", tps));
            if (wait.HasPosition && tps.Any())
            {
                foreach (var tp in tps)
                {
                    await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: signal.Symbol,
                        side: signal.Side == SignalSide.Buy ? OrderSide.Sell : OrderSide.Buy,
                        type: FuturesOrderType.TakeProfitMarket,
                        stopPrice: tp,
                        quantity: quantity,
                        positionSide: isHedge ? posSide : null,
                        selfTradePreventionMode: SelfTradePreventionMode.ExpireMaker,
                        ct: ct
                    );
                }

                _logger.LogInformation("[TP_PLACED][{symbol}] TP={tps}", signal.Symbol, string.Join(", ", tps));
            }

            return OrderResult.Successs(entryPrice, quantity, entryOrderId);
        }

        private static bool IsImpulse(
         IReadOnlyList<BinanceFuturesUsdtKline> klines,
         decimal atr,
         decimal minBodyAtr = 0.8m)  
        {
            if (klines == null || klines.Count < 2 || atr <= 0)
                return false;

            var last = klines[^1];

            var body = Math.Abs(last.ClosePrice - last.OpenPrice);
            var range = last.HighPrice - last.LowPrice;

            return body >= atr * minBodyAtr || range >= atr * 1.2m;
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
            const int maxLoops = 60;            // 60 * 500ms ~ 30s
            const int delayMs = 740;
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
                                decimal runawayThreshold = signal.IsSuperSignal ? maxSlipPct * 2m : maxSlipPct;
                                if (posSide == PositionSide.Long)
                                {
                                    diffPct = (mark - fallbackEntry) / fallbackEntry;
                                    if (diffPct >= runawayThreshold && !runawayLogged)
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
                                    if (diffPct >= runawayThreshold && !runawayLogged)
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

     
    }
}
