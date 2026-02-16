using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using Binance.Net.Objects.Models.Futures;
using Binance.Net.Objects.Models.Spot;
using CryptoExchange.Net.Objects;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services.Interface;
using VertexAutoTradeBinance8.Services.State;
using VertexAutoTradeBinance8.Strategy;

namespace VertexAutoTradeBinance8.Services
{


    public sealed class PositionLifecycleTracker
    {
        // key = symbol_side_entryPrice
        private readonly ConcurrentDictionary<string, int> _barsInTrade = new();

        public int IncBars(string key)
            => _barsInTrade.AddOrUpdate(key, 1, (_, v) => v + 1);

        public void Clear(string key)
            => _barsInTrade.TryRemove(key, out _);


    }

    /// <summary>
    /// PositionSupervisorService v8.2 PRO (Production)
    ///
    /// v8.2 FIXES (раз и навсегда):
    /// 0) EARLY TP (Partial close) 35% на +0.9 ATR → чтобы прибыль фиксировалась ДО откатов
    /// 1) SL -> BE (безубыток + буфер) на +1.2 ATR → чтобы после ранней прибыли не ловить минус
    /// 2) Анти-спам: partial/BE выполняются один раз на "позицию" (entry+qty+side)
    /// 3) UpdateSL: без reduceOnly (и без зависания на -1106), WorkingType.Mark используем осторожно
    /// 4) Если Binance вернёт -4120 → ставим/обновляем через RAW /fapi/v1/algoOrder (CONDITIONAL)
    /// 5) NEW 15-15-12-2025 -КОНЦЕПЦИЯ: PROTECT → PROBE → CONFIRM → SCALE PROBE — умный тест обратного движения(ключ)

    /// </summary>
    public class PositionSupervisorService
    {
        private readonly ILogger<PositionSupervisorService> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly SymbolInfoService _symbolInfo;
        private readonly AiSelfLearningService _aiLearning;
        private readonly MarketDataService _marketData;
        private readonly AiMarketRegimeService _regime;
        private readonly ManualPositionHandler _manualHandler;
        private readonly BinanceAlgoOrderRaw _algoRaw;
        private readonly LiquidityGuardService _liquidityGuard;
        private readonly IAccountStateService _accountState;
        private readonly IOrderDispatcher _dispatcher;
        private MarketRegime _regimeNow;

        // === Anti-spam guards for EarlyTP / BE-move ===
        private readonly ConcurrentDictionary<string, long> _earlyTpDone = new();   // key -> unixMs
        private readonly ConcurrentDictionary<string, long> _beMoved = new();      // key -> unixMs
        private readonly ConcurrentDictionary<string, decimal> _restoredEntries = new();
        private readonly PositionLifecycleTracker _lifecycle;

        // === Harvest block after partial close ===
        private readonly ConcurrentDictionary<string, long> _recentPartialClose = new();

        private readonly EngineStateSnapshotService _stateSvc;

        private readonly SmartRegimeService _smartRegime;
        private readonly ReverseProbeEngine _reverseProbe;

        // === Attach idempotency (existing position attach) ===
        private readonly ConcurrentDictionary<string, bool> _attached = new();

        // ===============================
        // NEW STATE DICTIONARIES
        // ===============================
        private readonly ConcurrentDictionary<string, BeStage> _beStage = new();

        private EngineState _engineState => _stateSvc.State;
        // private readonly AtrAdaptiveProfitLockManager _atrLock;
        // tracks staircase level after each partial / BE move
        private readonly ConcurrentDictionary<string, int> _beLevel = new();
        private readonly ConcurrentDictionary<string, DateTime> _pendingReset = new();
        private readonly ConcurrentDictionary<string, bool> _finalCleanupDone = new();


        public PositionSupervisorService(
            ILogger<PositionSupervisorService> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            AiSelfLearningService aiLearning,
            MarketDataService marketData,
            AiMarketRegimeService regime,
            ManualPositionHandler manualHandler,
            IConfiguration cfg,
            IHttpClientFactory httpFactory,
            LiquidityGuardService liquidityGuard,
            IOrderDispatcher dispatcher, EngineStateSnapshotService stateSvc,
            SmartRegimeService smartRegime,
            IAccountStateService accountState,
            ReverseProbeEngine reverseProbe, PositionLifecycleTracker lifecycle/*, AtrAdaptiveProfitLockManager atrLock*/)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
            _aiLearning = aiLearning;
            _marketData = marketData;
            _regime = regime;
            _manualHandler = manualHandler;

            _regimeNow = MarketRegime.Range;

            _algoRaw = new BinanceAlgoOrderRaw(cfg, httpFactory, _logger);
            _liquidityGuard = liquidityGuard;
            _dispatcher = dispatcher;
            _stateSvc = stateSvc;
            _smartRegime = smartRegime;
            _reverseProbe = reverseProbe;
            _lifecycle = lifecycle;
            // _atrLock = atrLock;
            _accountState = accountState;

        }

        private async Task HandleFinalCloseAsync(
    BinanceRestClient client,
    string symbol,
    PositionSide side,
    CancellationToken ct)
        {
            var key = $"{symbol}_{side}";

            if (!_finalCleanupDone.TryAdd(key, true))
                return;

            try
            {
                _logger.LogWarning(
                    "[FINAL CLEANUP][{symbol}][{side}] start",
                    symbol, side);

                // 1️⃣ Отменяем все ордера по символу
                var cancel = await client
                    .UsdFuturesApi
                    .Trading
                    .CancelAllOrdersAsync(symbol, ct:ct);

                if (!cancel.Success)
                {
                    _logger.LogWarning(
                        "[FINAL CLEANUP][{symbol}] CancelAllOrders failed: {err}",
                        symbol, cancel.Error?.Message);
                }

                // 2️⃣ Сбрасываем BE состояние
                _beStage.TryRemove(key, out _);
                _beLevel.TryRemove(key, out _);
                _pendingReset.TryRemove(key, out _);

                _logger.LogWarning(
                    "[FINAL CLEANUP][{symbol}][{side}] done",
                    symbol, side);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[FINAL CLEANUP FAILED][{symbol}][{side}]",
                    symbol, side);

                _finalCleanupDone.TryRemove(key, out _);
            }
            finally
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(15));
                    _finalCleanupDone.TryRemove(key, out _);
                });
            }
        }

        private static string BuildExitKey(string symbol, PositionSide side, decimal entryPrice)
        {
            return $"{symbol}|{side}|{entryPrice:F8}";
        }

        // =====================================================================
        // MAIN ENTRY
        // =====================================================================
        public async Task SuperviseAsync(string symbol, TradeSignal? lastSignal, CancellationToken ct)
        {
            using var client = _factory.CreateRestClient();

            // 0) MANUAL → виртуальный сигнал
            if (lastSignal == null)
            {
                var manualSignal = await _manualHandler.DetectManualAsync(client, symbol, ct);
                if (manualSignal != null)
                {
                    lastSignal = manualSignal;
                    _logger.LogWarning("[MANUAL][{symbol}] Virtual signal injected", symbol);
                }
            }

            // 1) Позиции (с ретраями)
            var posInfo = await GetPositionsWithRetryAsync(client, symbol, ct);
            if (!posInfo.Success || posInfo.Data == null)
            {
                _logger.LogWarning("[SUPERVISOR] No positions {symbol}", symbol);
                return;
            }

            var positions = posInfo.Data
    .Where(p => p.Symbol == symbol)
    .ToList();

            var longPos = positions.FirstOrDefault(p => p.PositionSide == PositionSide.Long);
            var shortPos = positions.FirstOrDefault(p => p.PositionSide == PositionSide.Short);

            DetectClose(symbol, longPos, PositionSide.Long);
            DetectClose(symbol, shortPos, PositionSide.Short);

            // проверяем, остались ли открытые позиции
            var hasLong = longPos?.Quantity > 0m;
            var hasShort = shortPos?.Quantity > 0m;

            if ((longPos?.Quantity ?? 0) == 0 && (shortPos?.Quantity ?? 0) == 0)
            {
                _logger.LogInformation("[SUPERVISOR] {symbol}: no positions", symbol);
                return;
            }

            // 2) Ордера
            var openOrders = await LoadOrdersAsync(client, symbol);

            // 3) Режим + klines (1m)
            IReadOnlyList<BinanceFuturesUsdtKline>? klines1m = null;
            try
            {
                klines1m = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 200);
                var rr = _regime.DetectRegime(symbol, KlineInterval.OneMinute, klines1m);
                if (rr != null) _regimeNow = rr.Regime;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SUPERVISOR] Klines load error {symbol}", symbol);
            }

            SmartRegimeInfo? smart1m = null;
            decimal atr14_1m = 0m;

            try
            {
                if (klines1m != null && klines1m.Count >= 50)
                {
                    smart1m = _smartRegime.Evaluate(symbol, KlineInterval.OneMinute, klines1m);
                    atr14_1m = _marketData.CalculateAtr(klines1m, 14);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SUPERVISOR] SmartRegime/Evaluate failed {symbol}", symbol);
            }

            // 🔁 PROBE должен быть ОДИН раз на тик supervise, до HandleSideAsync
            if (smart1m != null && atr14_1m > 0)
            {
                await TryReverseProbeAsync(client, symbol, longPos, shortPos, smart1m, atr14_1m, ct);
            }


            ///////////////////////////////////TEST DIAGNOSTIC SL/BE/MOVE/////////////////////////////////////////////////////////
            if (atr14_1m > 0 && klines1m != null && klines1m.Count >= 10)
            {
                void ProbeSide(BinancePositionDetailsUsdt? pos, PositionSide side)
                {
                    var key = symbol + "_" + side;

                    // =====================================================
                    // RESET HANDLING (position fully closed confirmation)
                    // =====================================================
                    if (pos == null || pos.Quantity == 0)
                    {
                        if (!_pendingReset.ContainsKey(key))
                        {
                            _pendingReset[key] = DateTime.UtcNow;

                            _logger.LogDebug(
                                "[BE RESET PENDING][{symbol}][{side}] waiting confirmation",
                                symbol, side);

                            return;
                        }

                        if (DateTime.UtcNow - _pendingReset[key] < TimeSpan.FromSeconds(45))
                            return;

                        _pendingReset.TryRemove(key, out _);
                        _beStage.TryRemove(key, out _);
                        _beLevel.TryRemove(key, out _);

                        _logger.LogInformation(
                            "[BE RESET CONFIRMED][{symbol}][{side}] Position confirmed closed",
                            symbol, side);

                        return;
                    }

                    _pendingReset.TryRemove(key, out _);

                    var qty = Math.Abs(pos.Quantity);
                    var entry = pos.EntryPrice;
                    var mark = pos.MarkPrice;

                    if (qty <= 0 || entry <= 0 || mark <= 0)
                        return;

                    // =====================================================
                    // ROI CALCULATION
                    // =====================================================
                    var roi = side == PositionSide.Long
                        ? (mark - entry) / entry
                        : (entry - mark) / entry;

                    // =====================================================
                    // GOLDEN-MIDDLE CONFIG (symbol adaptive)
                    // =====================================================
                    decimal STEP;
                    decimal PARTIAL_STEP;
                    decimal TRUE_BE_BUFFER;
                    decimal MIN_BE_BUFFER;
                    decimal PARTIAL_SIZE;

                    if (symbol == "BTCUSDT")
                    {
                        STEP = 0.0040m;           // 0.40%
                        PARTIAL_STEP = 0.0100m;   // 1.00%
                        TRUE_BE_BUFFER = 0.0022m; // 0.22%
                        MIN_BE_BUFFER = 0.0030m;  // 0.30%
                        PARTIAL_SIZE = 0.18m;     // close 18%
                    }
                    else if (symbol == "ETHUSDT")
                    {
                        STEP = 0.0045m;
                        PARTIAL_STEP = 0.0120m;
                        TRUE_BE_BUFFER = 0.0025m;
                        MIN_BE_BUFFER = 0.0035m;
                        PARTIAL_SIZE = 0.18m;
                    }
                    else
                    {
                        STEP = 0.0060m;
                        PARTIAL_STEP = 0.0160m;
                        TRUE_BE_BUFFER = 0.0035m;
                        MIN_BE_BUFFER = 0.0045m;
                        PARTIAL_SIZE = 0.15m;
                    }

                    // =====================================================
                    // MIN ROI FILTER (ignore noise)
                    // =====================================================
                    if (Math.Abs(roi) < MIN_BE_BUFFER)
                        return;

                    // =====================================================
                    // BE LEVEL TRACKING
                    // =====================================================
                    var level = (int)(roi / STEP);

                    var prevLevel = _beLevel.GetOrAdd(key, 0);

                    if (level <= prevLevel)
                        return;

                    _beLevel[key] = level;

                    _logger.LogInformation(
                        "[BE LEVEL][{symbol}][{side}] level {old} → {new} roi={roi:P2}",
                        symbol, side, prevLevel, level, roi);

                    // =====================================================
                    // PARTIAL CLOSE
                    // =====================================================
                    var partialLevel = (int)(roi / PARTIAL_STEP);

                    var prevPartial = (int)_beStage.GetOrAdd(key, BeStage.None);

                    if (partialLevel > prevPartial && partialLevel >= 1)
                    {
                        _beStage[key] = (BeStage)partialLevel;

                        var closeQty = Math.Round(qty * PARTIAL_SIZE, 8);

                        if (closeQty > 0)
                        {
                            _logger.LogWarning(
                                "[PARTIAL CLOSE][{symbol}][{side}] stage={stage} closeQty={qty}",
                                symbol, side, partialLevel, closeQty);

                            SafeFireAndForget(
                                ClosePartialAsync(client, symbol, side, closeQty, pos, ct));
                        }
                    }

                    // =====================================================
                    // MOVE STOP LOSS (progressive BE lock)
                    // =====================================================
                    var slOrder = openOrders.FirstOrDefault(o =>
                        o.PositionSide == side &&
                        o.Type == FuturesOrderType.StopMarket);

                    decimal newSl =
                        side == PositionSide.Long
                            ? entry * (1m + (level - 1) * STEP + TRUE_BE_BUFFER)
                            : entry * (1m - (level - 1) * STEP - TRUE_BE_BUFFER);

                    bool shouldMove =
                        slOrder == null ||
                        (side == PositionSide.Long
                            ? newSl > (slOrder.StopPrice ?? 0)
                            : newSl < (slOrder.StopPrice ?? 0));

                    if (shouldMove)
                    {
                        _logger.LogWarning(
                            "[BE MOVE][{symbol}][{side}] SL {old} → {new}",
                            symbol, side,
                            slOrder?.StopPrice ?? 0,
                            newSl);

                        SafeFireAndForget(
                            PlaceStopLossAtBeAsync(
                                client,
                                symbol,
                                side,
                                qty,
                                newSl,
                                pos,
                                ct));
                    }
                }

                ProbeSide(longPos, PositionSide.Long);
                ProbeSide(shortPos, PositionSide.Short);
            }

            // /////////////////////////////////////////////////

            /* #region TESTING ZONE FULL BLOCK — ATR ADAPTIVE VERSION 
            if (atr14_1m > 0 && klines1m != null && klines1m.Count >= 10)
            {
                void ProbeSide(BinancePositionDetailsUsdt? pos, PositionSide side)
                {
                    var key = symbol + "_" + side;

                    // =====================================================
                    // RESET HANDLING
                    // =====================================================
                    if (pos == null || pos.Quantity == 0)
                    {
                        if (!_pendingReset.ContainsKey(key))
                        {
                            _pendingReset[key] = DateTime.UtcNow;
                            _logger.LogDebug("[BE RESET PENDING][{symbol}][{side}] waiting confirmation", symbol, side);
                            return;
                        }

                        if (DateTime.UtcNow - _pendingReset[key] < TimeSpan.FromSeconds(45))
                            return;

                        _pendingReset.TryRemove(key, out _);
                        _beStage.TryRemove(key, out _);
                        _beLevel.TryRemove(key, out _);

                        _logger.LogInformation("[BE RESET CONFIRMED][{symbol}][{side}] Position confirmed closed", symbol, side);
                        return;
                    }

                    _pendingReset.TryRemove(key, out _);

                    var qty = Math.Abs(pos.Quantity);
                    var entry = pos.EntryPrice;
                    var mark = pos.MarkPrice;

                    if (qty <= 0 || entry <= 0 || mark <= 0)
                        return;

                    // =====================================================
                    // ROI
                    // =====================================================
                    var roi = side == PositionSide.Long
                        ? (mark - entry) / entry
                        : (entry - mark) / entry;

                    // =====================================================
                    // ATR VOLATILITY SCALING
                    // =====================================================
                    var volRatio = atr14_1m / mark; // relative volatility

                    decimal baseVol;

                    if (symbol == "BTCUSDT")      baseVol = 0.0025m;
                    else if (symbol == "ETHUSDT") baseVol = 0.0035m;
                    else                          baseVol = 0.0050m;

                    var volMultiplier = baseVol > 0
                        ? volRatio / baseVol
                        : 1m;

                    // clamp multiplier
                    volMultiplier = Math.Max(0.7m, Math.Min(1.8m, volMultiplier));

                    // =====================================================
                    // BASE CONFIG
                    // =====================================================
                    decimal BASE_STEP;
                    decimal BASE_PARTIAL_STEP;
                    decimal BASE_TRUE_BE_BUFFER;
                    decimal BASE_MIN_BE_BUFFER;
                    decimal PARTIAL_SIZE;

                    if (symbol == "BTCUSDT")
                    {
                        BASE_STEP = 0.0040m;
                        BASE_PARTIAL_STEP = 0.0100m;
                        BASE_TRUE_BE_BUFFER = 0.0022m;
                        BASE_MIN_BE_BUFFER = 0.0030m;
                        PARTIAL_SIZE = 0.18m;
                    }
                    else if (symbol == "ETHUSDT")
                    {
                        BASE_STEP = 0.0045m;
                        BASE_PARTIAL_STEP = 0.0120m;
                        BASE_TRUE_BE_BUFFER = 0.0025m;
                        BASE_MIN_BE_BUFFER = 0.0035m;
                        PARTIAL_SIZE = 0.18m;
                    }
                    else
                    {
                        BASE_STEP = 0.0060m;
                        BASE_PARTIAL_STEP = 0.0160m;
                        BASE_TRUE_BE_BUFFER = 0.0035m;
                        BASE_MIN_BE_BUFFER = 0.0045m;
                        PARTIAL_SIZE = 0.15m;
                    }

                    // =====================================================
                    // ADAPTIVE THRESHOLDS
                    // =====================================================
                    var STEP = BASE_STEP * volMultiplier;
                    var PARTIAL_STEP = BASE_PARTIAL_STEP * volMultiplier;
                    var TRUE_BE_BUFFER = BASE_TRUE_BE_BUFFER * volMultiplier;
                    var MIN_BE_BUFFER = BASE_MIN_BE_BUFFER * volMultiplier;

                    if (Math.Abs(roi) < MIN_BE_BUFFER)
                        return;

                    // =====================================================
                    // BE LEVEL
                    // =====================================================
                    var level = (int)(roi / STEP);
                    var prevLevel = _beLevel.GetOrAdd(key, 0);

                    if (level <= prevLevel)
                        return;

                    _beLevel[key] = level;

                    _logger.LogInformation(
                        "[BE LEVEL][{symbol}][{side}] {old}→{new} roi={roi:P2} volMul={vm:F2}",
                        symbol, side, prevLevel, level, roi, volMultiplier);

                    // =====================================================
                    // PARTIAL CLOSE
                    // =====================================================
                    var partialLevel = (int)(roi / PARTIAL_STEP);
                    var prevPartial = (int)_beStage.GetOrAdd(key, BeStage.None);

                    if (partialLevel > prevPartial && partialLevel >= 1)
                    {
                        _beStage[key] = (BeStage)partialLevel;

                        var closeQty = Math.Round(qty * PARTIAL_SIZE, 8);

                        if (closeQty > 0)
                        {
                            _logger.LogWarning(
                                "[PARTIAL CLOSE][{symbol}][{side}] stage={stage} qty={qty}",
                                symbol, side, partialLevel, closeQty);

                            SafeFireAndForget(
                                ClosePartialAsync(client, symbol, side, closeQty, pos, ct));
                        }
                    }

                    // =====================================================
                    // MOVE STOP LOSS
                    // =====================================================
                    var slOrder = openOrders.FirstOrDefault(o =>
                        o.PositionSide == side &&
                        o.Type == FuturesOrderType.StopMarket);

                    decimal newSl = side == PositionSide.Long
                        ? entry * (1m + (level - 1) * STEP + TRUE_BE_BUFFER)
                        : entry * (1m - (level - 1) * STEP - TRUE_BE_BUFFER);

                    bool shouldMove =
                        slOrder == null ||
                        (side == PositionSide.Long
                            ? newSl > (slOrder.StopPrice ?? 0)
                            : newSl < (slOrder.StopPrice ?? 0));

                    if (shouldMove)
                    {
                        _logger.LogWarning(
                            "[BE MOVE][{symbol}][{side}] SL {old}→{new}",
                            symbol, side,
                            slOrder?.StopPrice ?? 0,
                            newSl);

                        SafeFireAndForget(
                            PlaceStopLossAtBeAsync(
                                client,
                                symbol,
                                side,
                                qty,
                                newSl,
                                pos,
                                ct));
                    }
                }

                ProbeSide(longPos, PositionSide.Long);
                ProbeSide(shortPos, PositionSide.Short);
            }

            #endregion  */


            // 4) Обработка сторон
            if (hasLong)
                await HandleSideAsync(client, symbol, PositionSide.Long, longPos!, openOrders, lastSignal, klines1m, ct);

            if (hasShort)
                await HandleSideAsync(client, symbol, PositionSide.Short, shortPos!, openOrders, lastSignal, klines1m, ct);
        }


        // ===== PLACE BE SL =====
        private async Task PlaceStopLossAtBeAsync(
            IBinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entryPrice,
            BinancePositionDetailsUsdt pos,
            CancellationToken ct)
        {
            if (qty <= 0 || pos == null) return;

            var orderSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
            bool reduceOnly = pos.Quantity != 0; // ReduceOnly только если реально есть позиция


            // --- нормализация ---
            var (qtyPrecision, pricePrecision) = await GetSymbolPrecisionsAsync(client, symbol);
            qty = await NormalizeQuantityAsync(symbol, side, qty, client, ct);   // ✅ здесь нормализуем по MaxNotional


            entryPrice = RoundPrice(entryPrice, pricePrecision);

            // получить filters
            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);

            // КРИТИЧЕСКИЙ ФИКС — normalize trigger
            entryPrice = await NormalizeTriggerPriceAsync(
     client,
     symbol,
     side,
     entryPrice,
     filters.tickSize,
     true,
     ct);

            //await client.UsdFuturesApi.Trading.CancelAllConditionalOrdersAsync(symbol, ct: ct);
            var open = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct);

            if (open.Success)
            {
                foreach (var o in open.Data)
                {
                    if (o.Type == FuturesOrderType.StopMarket &&
                        o.PositionSide == side)
                    {
                        await client.UsdFuturesApi.Trading.CancelOrderAsync(
                            symbol,
                            orderId: o.Id,
                            ct: ct);
                    }
                }
            }
            var result = await client.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
                symbol: symbol,
                side: orderSide,
                type: ConditionalOrderType.StopMarket,
                quantity: qty,
                positionSide: side,
                triggerPrice: entryPrice,
                workingType: WorkingType.Mark,
                // reduceOnly: reduceOnly, // в Hedge Mode Binance не требует
                priceProtect: true,
                ct: ct
            );

            if (result.Success)
                _logger.LogInformation("[BE MOVE][{symbol}][{side}] BE SL placed at {price} qty={qty}", symbol, side, entryPrice, qty);
            else
                _logger.LogError("[BE MOVE][{symbol}][{side}] Failed: {msg}", symbol, side, result.Error?.Message);
        }


        private async Task ClosePartialChunkedAsync(
    IBinanceRestClient client,
    string symbol,
    PositionSide side,
    decimal totalQty,
    CancellationToken ct)
        {
            const decimal CHUNK_USDT = 5000m;

            var mark = await client.UsdFuturesApi.ExchangeData.GetMarkPriceAsync(symbol, ct);

            if (!mark.Success)
                return;

            var markPrice = mark.Data.MarkPrice;

            var chunkQty = CHUNK_USDT / markPrice;

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol, ct: ct);

            chunkQty = Math.Floor(chunkQty / filters.step) * filters.step;

            if (chunkQty <= 0)
                return;

            var remaining = totalQty;

            while (remaining > 0)
            {
                var qty = Math.Min(chunkQty, remaining);

                var orderSide = side == PositionSide.Long
                    ? OrderSide.Sell
                    : OrderSide.Buy;

                var result = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: orderSide,
                    type: FuturesOrderType.Market,
                    quantity: qty,
                    positionSide: side,
                    ct: ct);

                if (!result.Success)
                {
                    _logger.LogError(
                        "[PARTIAL CLOSE CHUNK FAILED][{symbol}] {err}",
                        symbol,
                        result.Error?.Message);

                    return;
                }
                if (result.Success)
                {
                    // ===== REALIZED PNL CALC =====

                    var entry = await GetEntryPriceSafeAsync(client, symbol, side, ct);

                    if (entry > 0)
                    {
                        decimal realizedPnl =
                            side == PositionSide.Long
                            ? (markPrice - entry) * qty
                            : (entry - markPrice) * qty;

                        _accountState.AddRealizedPnl(realizedPnl);

                        _logger.LogInformation(
                            "[REALIZED PNL][PARTIAL][{symbol}][{side}] pnl={pnl}",
                            symbol, side, realizedPnl);
                    }
                }

                remaining -= qty;

                await Task.Delay(50, ct);
            }

            _logger.LogInformation(
                "[PARTIAL CLOSE OK][{symbol}] totalQty={qty}",
                symbol,
                totalQty);
        }

        private async Task<decimal> GetEntryPriceSafeAsync(
    IBinanceRestClient client,
    string symbol,
    PositionSide side,
    CancellationToken ct)
        {
            var posInfo = await client
                .UsdFuturesApi
                .Account
                .GetPositionInformationAsync(symbol, ct: ct);

            if (!posInfo.Success)
                return 0;

            var pos = posInfo.Data.FirstOrDefault(p =>
                p.PositionSide == side &&
                Math.Abs(p.Quantity) > 0);

            return pos?.EntryPrice ?? 0;
        }

        // ===== CLOSE PARTIAL =====
        private async Task ClosePartialAsync(
         IBinanceRestClient client,
         string symbol,
         PositionSide side,
         decimal qty,
         BinancePositionDetailsUsdt pos,
         CancellationToken ct)
        {
            if (qty <= 0 || pos == null)
                return;

            var orderSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var (qtyPrecision, _) = await GetSymbolPrecisionsAsync(client, symbol);
            qty = await NormalizeQuantityAsync(symbol, side, qty, client, ct);
            var positionQty = Math.Abs(pos.Quantity);

            if (qty > positionQty)
                qty = positionQty * 0.98m;
            if (qty <= 0)
            {
                _logger.LogWarning("[PARTIAL CLOSE SKIPPED][{symbol}][{side}] qty rounded to 0", symbol, side);
                return;
            }


            await ClosePartialChunkedAsync(
            client,
            symbol,
            side,
            qty,
            ct);

        }
        private async Task<decimal> NormalizeQuantityAsync(
    string symbol,
    PositionSide side,
    decimal requestedQty,
    IBinanceRestClient client,
    CancellationToken ct)
        {
            // 1) получить реальную позицию
            var posInfo = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, ct: ct);

            if (!posInfo.Success)
                return 0;

            var pos = posInfo.Data.FirstOrDefault(p =>
                p.PositionSide == side &&
                Math.Abs(p.Quantity) > 0);

            if (pos == null)
                return 0;

            var positionQty = Math.Abs(pos.Quantity);

            // 2) symbol filters
            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);

            var maxQty = filters.maxQty;
            var step = filters.step;
            var minQty = filters.minQty;

            // 3) clamp
            var qty = Math.Min(requestedQty, positionQty);
            if (maxQty > 0)
                qty = Math.Min(qty, maxQty);

            // 4) round to stepSize
            qty = Math.Floor(qty / step) * step;

            // 5) validate
            if (qty < minQty)
                return 0;

            return qty;
        }

        private async Task<decimal> NormalizeTriggerPriceAsync(
     IBinanceRestClient client,
     string symbol,
     PositionSide side,
     decimal trigger,
     decimal tick,
     bool isStopLoss,
     CancellationToken ct)
        {

            var mark = await client
                .UsdFuturesApi
                .ExchangeData
                .GetMarkPriceAsync(symbol, ct);

            if (!mark.Success)
                return trigger;

            var price = mark.Data.MarkPrice;

            if (side == PositionSide.Long)
            {
                if (isStopLoss)
                {
                    if (trigger >= price)
                        trigger = price - tick * 3;
                }
                else
                {
                    if (trigger <= price)
                        trigger = price + tick * 3;
                }
            }
            else
            {
                if (isStopLoss)
                {
                    if (trigger <= price)
                        trigger = price + tick * 3;
                }
                else
                {
                    if (trigger >= price)
                        trigger = price - tick * 3;
                }
            }

            return Math.Round(trigger / tick) * tick;
        }



        // ===== UTILITY: Получение точностей символа =====
        private async Task<(int qtyPrecision, int pricePrecision)> GetSymbolPrecisionsAsync(
            IBinanceRestClient client, string symbol)
        {
            var infoResult = await client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();
            var sym = infoResult.Data.Symbols.FirstOrDefault(s => s.Name == symbol);
            if (sym == null)
                return (0, 0);

            int qtyPrecision = 0;
            int pricePrecision = 0;

            // ===== Количество =====
            var lot = sym.Filters.FirstOrDefault(f => f.FilterType == SymbolFilterType.LotSize);
            if (lot != null)
            {
                // безопасно приводим к BinanceSymbolLotSizeFilter
                if (lot is BinanceSymbolLotSizeFilter lotFilter)
                {
                    qtyPrecision = (int)Math.Round(Math.Log10((double)(1 / lotFilter.StepSize)));
                }
            }

            // ===== Цена =====
            var priceF = sym.Filters.FirstOrDefault(f => f.FilterType == SymbolFilterType.Price);
            if (priceF != null)
            {
                // безопасно приводим к BinanceSymbolPriceFilter
                if (priceF is BinanceSymbolPriceFilter priceFilter)
                {
                    pricePrecision = (int)Math.Round(Math.Log10((double)(1 / priceFilter.TickSize)));
                }
            }

            return (qtyPrecision, pricePrecision);
        }

        private decimal RoundPrice(decimal price, int precision)
        {
            return Math.Floor(price * (decimal)Math.Pow(10, precision)) / (decimal)Math.Pow(10, precision);
        }


        private void SafeFireAndForget(Task task)
        {
            _ = task.ContinueWith(t =>
            {
                if (t.Exception != null)
                {
                    _logger.LogError(
                        t.Exception,
                        "[FIRE&FORGET ERROR]");
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private bool ShouldUseReduceOnly(BinancePositionDetailsUsdt? pos)
        {
            // reduceOnly = true только если реально есть позиция на этой стороне
            return pos != null && pos.Quantity != 0;
        }



        private decimal ResolveExitPrice(string symbol)
        {
            // 1) Пытаемся взять свежий стакан
            var depth = _marketData.GetCachedDepth(symbol);

            if (depth != null && depth.Bids.Count > 0 && depth.Asks.Count > 0)
            {
                var bestBid = depth.Bids[0].price;
                var bestAsk = depth.Asks[0].price;

                if (bestBid > 0 && bestAsk > 0)
                    return (bestBid + bestAsk) / 2m;
            }

            return 0m;
        }

        private void DetectClose(
     string symbol,
     BinancePositionDetailsUsdt? pos,
     PositionSide side)
        {
            var key = $"{symbol}_{side}";

            var prevQty = _manualHandler.GetPrevQty(key);
            var prevEntry = _manualHandler.GetPrevEntry(key);

            var currQty = pos?.Quantity ?? 0m;
            var currEntry = pos?.EntryPrice ?? 0m;

            // 🔥 CLOSE DETECTED
            if (prevQty != 0m && currQty == 0m)
            {
                var exitPrice = ResolveExitPrice(symbol);

                if (exitPrice <= 0m)
                {
                    _logger.LogWarning(
                        "[CLOSE][{symbol}][{side}] Exit price unresolved, skip record",
                        symbol, side);
                }
                else
                {
                    // ✅ REALIZED PNL (добавлено)
                    var qty = Math.Abs(prevQty);

                    decimal realizedPnl =
                        side == PositionSide.Long
                            ? (exitPrice - prevEntry) * qty
                            : (prevEntry - exitPrice) * qty;

                    _accountState.AddRealizedPnl(realizedPnl);

                    // AI learning остаётся как есть
                    _aiLearning.RecordTrade(
                        symbol,
                        side == PositionSide.Long ? SignalSide.Buy : SignalSide.Sell,
                        entry: prevEntry,
                        exit: exitPrice,
                        regime: _regimeNow);

                    _logger.LogWarning(
                        "[CLOSE][{symbol}][{side}] qty={qty} entry={entry} exit={exit} pnl={pnl}",
                        symbol, side, prevQty, prevEntry, exitPrice, realizedPnl);
                }

                var client = _factory.CreateRestClient();

                SafeFireAndForget(
                    HandleFinalCloseAsync(
                        client,
                        symbol,
                        side,
                        CancellationToken.None));

                // ⚠️ ОБНОВЛЯЕМ СОСТОЯНИЕ ТОЛЬКО ПОСЛЕ ПРОВЕРКИ
                _manualHandler.SetPrevState(key, currQty, currEntry);
            }
        }



        // =====================================================================
        // RETRY POSITIONS
        // =====================================================================
        private async Task<WebCallResult<BinancePositionDetailsUsdt[]>> GetPositionsWithRetryAsync(
        BinanceRestClient client,
        string symbol,
        CancellationToken ct)
        {
            const int maxAttempts = 5;
            WebCallResult<BinancePositionDetailsUsdt[]> last = null!;

            for (int i = 0; i < maxAttempts; i++)
            {
                ct.ThrowIfCancellationRequested();

                // 🔥 БЕЗ symbol-фильтра — Binance bug-safe
                var res = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                last = res;

                if (res.Success && res.Data != null)
                {
                    // ⏳ ждём, пока позиция реально появится
                    if (res.Data.Any(p => p.Symbol == symbol && p.Quantity != 0m))
                        return res;
                }

                await Task.Delay(300, ct);
            }

            return last;
        }


        public async Task AttachExistingPositionAsync(
         string symbol,
         PositionSide side,
         decimal qty,
         decimal entryPrice,
         CancellationToken ct)
        {
            qty = Math.Abs(qty);

            if (qty <= 0 || entryPrice <= 0) return;

            var key = $"{symbol}:{side}:{entryPrice:F8}:{qty:F8}";

            if (_attached.TryAdd(key, true) == false)
            {
                _logger.LogInformation("[SUPERVISOR][ATTACH] already attached {key}", key);
                return;
            }

            _logger.LogWarning(
                "[SUPERVISOR][ATTACH] attaching existing position {symbol} {side} qty={qty} entry={entry}",
                symbol, side, qty, entryPrice);

            await EnsureEmergencyProtectionAsync(symbol, side, qty, entryPrice, ct);
        }


        private async Task EnsureEmergencyProtectionAsync(
        string symbol,
        PositionSide side,
        decimal qty,
        decimal entryPrice,
        CancellationToken ct)
        {
            if (qty <= 0 || entryPrice <= 0)
                return;

            // Важно: qty у позиции в Binance может быть со знаком
            qty = Math.Abs(qty);

            using var client = _factory.CreateRestClient();

            // 1) Проверяем существующие ордера
            var openOrders = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(
                symbol: symbol,
                ct: ct);

            if (!openOrders.Success || openOrders.Data == null)
            {
                _logger.LogWarning(
                    "[SUPERVISOR][EMERGENCY] cannot fetch open orders {symbol}: {err}",
                    symbol, openOrders.Error?.Message);
                return;
            }

            bool hasSL = openOrders.Data.Any(o =>
                o.Type == FuturesOrderType.StopMarket &&
                o.PositionSide == side);

            if (hasSL)
            {
                _logger.LogInformation(
                    "[SUPERVISOR][EMERGENCY] SL already exists {symbol} {side}",
                    symbol, side);
                return;
            }

            // 2) Emergency SL без klines (bootstrap-safe)
            // Консервативно: 1.2% от entry (под твой v8.2 смысл "быстро защитить")
            const decimal emergencyPct = 0.012m;

            decimal slPrice = side == PositionSide.Long
                ? entryPrice * (1m - emergencyPct)
                : entryPrice * (1m + emergencyPct);

            _logger.LogWarning(
                "[SUPERVISOR][EMERGENCY] placing SL {symbol} {side} qty={qty} sl={sl}",
                symbol, side, qty, slPrice);

            // 3) Пробуем обычный endpoint (без reduceOnly параметра -> меньше риска -1106)
            var orderSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var sl = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: orderSide,
                type: FuturesOrderType.StopMarket,
                quantity: qty,
                stopPrice: slPrice,
                positionSide: side,
                workingType: WorkingType.Mark,
                reduceOnly: null,
                ct: ct);

            if (sl.Success)
            {
                _logger.LogInformation(
                    "[SUPERVISOR][EMERGENCY] SL placed OK (NORMAL) {symbol} {side} sl={sl}",
                    symbol, side, slPrice);
                return;
            }

            // 4) Fallback: ALGO RAW CONDITIONAL (если -4120)
            if (IsAlgoRequired(sl.Error))
            {
                _logger.LogWarning(
                    "[SUPERVISOR][EMERGENCY] requires ALGO (-4120) -> RAW /fapi/v1/algoOrder {symbol} {side}",
                    symbol, side);

                // workingType: "MARK_PRICE" чтобы совпасть с WorkingType.Mark
                var ok = await _algoRaw.PlaceConditionalAsync(
                    symbol: symbol,
                    side: orderSide,
                    positionSide: side,
                    type: "STOP_MARKET",
                    quantity: qty,
                    triggerPrice: slPrice,
                    workingType: "MARK_PRICE",
                    reduceOnly: null,
                    ct: ct);

                if (ok)
                {
                    _logger.LogWarning(
                        "[SUPERVISOR][EMERGENCY] SL placed OK (ALGO-RAW) {symbol} {side} sl={sl}",
                        symbol, side, slPrice);
                    return;
                }

                _logger.LogCritical(
                    "[SUPERVISOR][EMERGENCY] ALGO-RAW FAILED {symbol} {side}",
                    symbol, side);
                return;
            }

            _logger.LogError(
                "[SUPERVISOR][EMERGENCY] SL FAILED (NORMAL) {symbol}: {err}",
                symbol, sl.Error?.Message);
        }



        private async Task TryReverseProbeAsync(
    BinanceRestClient client,
    string symbol,
    BinancePositionDetailsUsdt? longPos,
    BinancePositionDetailsUsdt? shortPos,
    SmartRegimeInfo smart,
    decimal atr,
    CancellationToken ct)
        {


            // 0) protection must exist (PROTECT stage already done by EarlyTP/BE)
            var sKey = EngineState.Key(symbol);
            if (!_engineState.Symbols.TryGetValue(sKey, out var st))
                return;

            bool protectedRecently = st.LastProtectionUtc > DateTime.UtcNow.AddMinutes(-15);
            if (!protectedRecently)
                return;

            // 1) do not probe if both sides already exist (already hedged)
            bool hasLong = longPos != null && longPos.Quantity != 0m;
            bool hasShort = shortPos != null && shortPos.Quantity != 0m;
            if (hasLong && hasShort)
                return;

            // 2) base side = existing position side
            PositionSide baseSide =
                hasLong ? PositionSide.Long :
                hasShort ? PositionSide.Short :
                PositionSide.Both;

            if (baseSide == PositionSide.Both)
                return;

            // if liquidity recent → wait a bit, but allow early TP later           
            // 🚫 no probe right after liquidity event
            if (_liquidityGuard.IsDangerRecent(TimeSpan.FromMinutes(5)))
            {
                if (DateTime.UtcNow - _liquidityGuard.LastDanger!.UtcTime < TimeSpan.FromMinutes(2))
                {
                    _logger.LogInformation(
                        "[PROBE][{symbol}] SKIP → recent liquidity danger {reason}",
                        symbol, _liquidityGuard.LastDanger?.Reason);
                    return;
                }
            }

            if (_liquidityGuard.LastDanger?.Reason == LiquidityGuardReason.LowVolume)
                return;


            // 3) flip condition (strict)
            bool flipToShort =
                baseSide == PositionSide.Long &&
                smart.BaseRegime == MarketRegime.StrongDownTrend &&
                smart.TrendSlopePercent < -0.01m;

            bool flipToLong =
                baseSide == PositionSide.Short &&
                smart.BaseRegime == MarketRegime.StrongUpTrend &&
                smart.TrendSlopePercent > 0.01m;

            if (!flipToShort && !flipToLong)
                return;

            var probeSide = flipToShort ? PositionSide.Short : PositionSide.Long;

            // 4) anti-spam (single probe per 5 minutes per symbol)
            if (!_reverseProbe.CanProbeNow(symbol))
                return;

            // 5) size = 7% of existing position qty (micro-hedge, no RiskManager needed)
            decimal baseQtyAbs = Math.Abs((baseSide == PositionSide.Long ? longPos!.Quantity : shortPos!.Quantity));
            if (baseQtyAbs <= 0m)
                return;

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            var step = filters.step > 0 ? filters.step : 1m;

            decimal probeQty = baseQtyAbs * 0.07m;
            probeQty = Math.Floor(probeQty / step) * step;

            if (probeQty < filters.minQty)
                return;

            // 6) place MARKET entry (Hedge side-aware)
            var orderSide = probeSide == PositionSide.Long ? OrderSide.Buy : OrderSide.Sell;

            _logger.LogWarning(
                "[PROBE][{symbol}] START micro-hedge: base={baseSide} -> probe={probeSide} qty={qty} smart={reg} slope={slope:P2}",
                symbol, baseSide, probeSide, probeQty, smart.BaseRegime, smart.TrendSlopePercent);

            _dispatcher.Enqueue(async token =>
            {
                using var c = _factory.CreateRestClient();

                var entryRes = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: orderSide,
                    type: FuturesOrderType.Market,
                    quantity: probeQty,
                    positionSide: probeSide,
                    ct: token);

                if (!entryRes.Success)
                {
                    _logger.LogWarning("[PROBE][{symbol}] Entry failed: {err}", symbol, entryRes.Error);
                    return;
                }

                // small sync lag
                await Task.Delay(350, token);

                // resolve entry from actual position
                var posInfo = await c.UsdFuturesApi.Account.GetPositionInformationAsync(ct: token);
                if (!posInfo.Success || posInfo.Data == null)
                    return;

                var p = posInfo.Data.FirstOrDefault(x =>
                    x.Symbol == symbol &&
                    x.PositionSide == probeSide &&
                    Math.Abs(x.Quantity) > 0);

                if (p == null || p.EntryPrice <= 0)
                {
                    _logger.LogWarning("[PROBE][{symbol}] Entry resolve failed after open", symbol);
                    return;
                }

                var entry = p.EntryPrice;

                // set emergency SL/TP for probe immediately
                await CreateEmergencySLAsync(c, symbol, probeSide, probeQty, entry, signal: null, token);
                await CreateEmergencyTPAsync(c, symbol, probeSide, probeQty, entry, signal: null, token);

                _logger.LogWarning(
                    "[PROBE][{symbol}] OPENED {side} qty={qty} entry={entry} -> SL/TP restored",
                    symbol, probeSide, probeQty, entry);
            });

            _reverseProbe.MarkProbe(symbol);
        }


        private async Task<List<BinanceUsdFuturesOrder>> LoadOrdersAsync(BinanceRestClient client, string symbol)
        {
            var res = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol);
            return res.Success && res.Data != null ? res.Data.ToList() : new List<BinanceUsdFuturesOrder>();
        }

        // =====================================================================
        // HANDLE SIDE  (v8.2 PRO)
        // =====================================================================

        public async Task ClosePositionMarketAsync(
            string symbol,
            BinanceRestClient client,
            BinancePositionDetailsUsdt pos,
            CancellationToken ct)
        {
            if (pos == null || pos.Quantity == 0) return;

            var side = pos.Quantity > 0 ? OrderSide.Sell : OrderSide.Buy;
            var absQty = Math.Abs(pos.Quantity);
            bool reduceOnly = ShouldUseReduceOnly(pos); // ✅ универсальный фикс

            var result = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: side,
                type: FuturesOrderType.Market,
                quantity: absQty,
                positionSide: pos.PositionSide,
                reduceOnly: reduceOnly,
                ct: ct
            );

            if (result.Success)
                _logger.LogInformation("[FULL CLOSE OK][{symbol}][{side}] qty={qty}", symbol, pos.PositionSide, absQty);
            else
                _logger.LogError("[FULL CLOSE FAILED][{symbol}][{side}] {err}", symbol, pos.PositionSide, result.Error?.Message);
        }


        public async Task<int> GetActivePositionsCountAsync(CancellationToken ct = default)
        {
            using var client = _factory.CreateRestClient();

            try
            {
                var result = await client
                    .UsdFuturesApi
                    .Trading
                    .GetPositionsAsync(ct: ct)
                    .ConfigureAwait(false);

                if (!result.Success || result.Data == null)
                {
                    _logger.LogWarning(
                        "API вернул ошибку или null при получении позиций: {code} - {msg}",
                        result.Error?.Code,
                        result.Error?.Message);

                    return 0; // <- безопасно, бот продолжает работать
                }

                int activeCount = result.Data.Count(p => p.PositionAmt != 0);

                _logger.LogInformation(
                    "Проверка позиций завершена. Активных: {count}",
                    activeCount);

                return activeCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Критическая ошибка при получении позиций");
                return 0; // <- безопасно, BackgroundService не падает
            }
        }


        private async Task HandleSideAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            BinancePositionDetailsUsdt pos,
            List<BinanceUsdFuturesOrder> allOrders,
            TradeSignal? signal,
            IReadOnlyList<BinanceFuturesUsdtKline>? klines,
            CancellationToken ct)
        {
            decimal qtyAbs = Math.Abs(pos.Quantity);

            // ---------- CLOSE DETECTOR ----------
            var key = $"{symbol}_{side}";
            var prevQty = _manualHandler.GetPrevQty(key);
            var prevEntry = _manualHandler.GetPrevEntry(key);



            if (prevQty != 0 && pos.Quantity == 0)
            {
                decimal exitPrice = pos.MarkPrice > 0
             ? pos.MarkPrice
             : pos.EntryPrice; // fallback safety
                var sigSide = side == PositionSide.Long ? SignalSide.Buy : SignalSide.Sell;

                _aiLearning.RecordTrade(symbol, sigSide, entry: prevEntry, exit: exitPrice, regime: _regimeNow);

                _logger.LogWarning(
                    "[AI][{symbol}] POSITION CLOSED → saved to ai_learning.json | entry={entry} exit={exit}",
                    symbol, prevEntry, exitPrice);


                // =======================================
                // STOP LOSS DETECT → STRATEGY COOLDOWN
                // =======================================
                bool isStopLoss =
     side == PositionSide.Long
         ? exitPrice < prevEntry
         : exitPrice > prevEntry;

                if (isStopLoss)
                {
                    _manualHandler.RegisterStop(symbol);

                    _logger.LogWarning(
                        "[STOP][{symbol}] StopLoss detected → cooldown registered",
                        symbol);
                }
                // cleanup guards
                _earlyTpDone.TryRemove(BuildPosGuardKey(symbol, side, prevEntry, prevQty), out _);
                _beMoved.TryRemove(BuildPosGuardKey(symbol, side, prevEntry, prevQty), out _);

                return;
            }
            _manualHandler.SetPrevState(key, pos.Quantity, pos.EntryPrice);

            // === no position ===
            if (qtyAbs <= 0)
            {
                // обязательно чистим lifecycle
                _lifecycle.Clear(BuildExitKey(symbol, side, prevEntry));
                _logger.LogInformation("[SUPERVISOR] {symbol} {side} {prevEntry}: no qty", symbol, side, prevEntry);
                return;
            }

            // =====================================================
            // IMPULSE CONTINUATION — FORCE EXIT CONTRACT
            // =====================================================
            if (signal != null && signal.ForceFullExit)
            {
                // key per position lifecycle
                var posKey = BuildPosGuardKey(symbol, side, pos.EntryPrice, qtyAbs);

                // считаем бары
                int bars = _lifecycle.IncBars(posKey);

                // --- TIME STOP ---
                if (signal.TimeStopBars.HasValue && bars >= signal.TimeStopBars.Value)
                {
                    _logger.LogWarning(
                        "[EXIT][{symbol}] IMPULSE_CONTINUATION TimeStop → FULL CLOSE ({bars} bars)",
                        symbol, bars);

                    return;
                }

                // --- LOSS OF IMPULSE (optional but recommended) ---
                if (klines != null && klines.Count >= 3)
                {
                    int i = klines.Count - 1;
                    var c0 = klines[i];
                    var c1 = klines[i - 1];

                    decimal atr = 0m;


                    if (signal?.Atr != null && signal.Atr.Value > 0)
                        atr = signal.Atr.Value;

                    decimal body0 = Math.Abs(c0.ClosePrice - c0.OpenPrice);
                    decimal body1 = Math.Abs(c1.ClosePrice - c1.OpenPrice);

                    bool impulseLost =
                        body0 < atr * 0.2m &&
                        body1 < atr * 0.2m;

                    if (impulseLost)
                    {
                        _logger.LogWarning(
                            "[EXIT][{symbol}] IMPULSE_CONTINUATION impulse lost → FULL CLOSE",
                            symbol);

                        await ClosePositionMarketAsync(symbol, client, pos, ct);

                        return;
                    }
                }
                // ⛔ ВАЖНО: если ForceFullExit — НИЧЕГО больше не делаем
                // запрещаем partial / BE / trailing ниже
                return;
            }



            decimal entry = pos.EntryPrice;

            if (entry <= 0)
            {
                key = $"{symbol}_{side}";

                if (!_restoredEntries.TryGetValue(key, out entry))
                {
                    var restored = await ResolveEntryFromExchangeAsync(client, symbol, side, ct);
                    if (!restored.HasValue)
                    {
                        _logger.LogError("[SUPERVISOR][{symbol}] Entry unresolved → skip SL/TP", symbol);
                        return;
                    }

                    entry = restored.Value;
                    _restoredEntries[key] = entry;

                    _logger.LogWarning(
                        "[SUPERVISOR][{symbol}] Entry restored from exchange = {entry}",
                        symbol, entry);
                }
            }

            // In case signal missing ATR in supervisor context, try compute
            decimal atr14 = 0m;
            if (signal?.Atr != null && signal.Atr.Value > 0)
                atr14 = signal.Atr.Value;
            else if (klines != null && klines.Count >= 40)
                atr14 = _marketData.CalculateAtr(klines, 15);

            // === Side-specific orders (Hedge) ===
            var orders = allOrders.Where(o => o.PositionSide == side).ToList();

            // === Find SL/TP ===
            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            bool hasMultipleSL = orders.Count(o => o.Type == FuturesOrderType.StopMarket) > 1;
            bool hasMultipleTP = orders.Count(o => o.Type == FuturesOrderType.TakeProfitMarket) > 1;

            if (hasMultipleSL)
                _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] Multiple SL detected → skip SL create", symbol, side);

            if (hasMultipleTP)
                _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] Multiple TP detected → skip TP create", symbol, side);

            var sl = orders.FirstOrDefault(o => o.Side == closeSide && o.Type == FuturesOrderType.StopMarket);
            var tp = orders.FirstOrDefault(o => o.Side == closeSide && o.Type == FuturesOrderType.TakeProfitMarket);

            // =================================================================
            // 🔁 RESTART PROTECTION (NO KLINES / NO MEMORY)
            // =================================================================
            if (sl != null)
            {
                var slPrice =
    sl.StopPrice > 0
        ? sl.StopPrice
        : sl.Price;


                if (slPrice > 0)
                {
                    var last = pos.MarkPrice > 0 ? pos.MarkPrice : entry;
                    var guardKey = BuildPosGuardKey(symbol, side, entry, qtyAbs);

                    bool slBelowEntry =
                        side == PositionSide.Long
                            ? slPrice < entry
                            : slPrice > entry;

                    bool marketInProfit =
                        side == PositionSide.Long
                            ? last > entry
                            : last < entry;

                    if (marketInProfit && slBelowEntry && !_beMoved.ContainsKey(guardKey))
                    {
                        decimal minimalBe =
                            side == PositionSide.Long
                                ? entry + entry * 0.0005m   // ~0.05%
                                : entry - entry * 0.0005m;

                        await UpdateSL_ProAsync(
                            client,
                            symbol,
                            side,
                            qtyAbs,
                            sl,
                            entry,
                            minimalBe,
                            signal,
                            ct);

                        _beMoved[guardKey] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                        _logger.LogWarning(
                            "[RESTART-BE][{symbol}][{side}] SL moved to minimal BE (no klines)",
                            symbol, side);
                    }
                }
            }

            // =================================================================
            // v8.2 PRO: STARTUP / LIVE BE REHYDRATION (WITH KLINES)
            // =================================================================
            if (klines != null && klines.Count >= 50 && atr14 > 0 && entry > 0)
            {
                var guardKey = BuildPosGuardKey(symbol, side, entry, qtyAbs);

                // Если позиция уже в плюсе, а BE ещё не отмечен — двигаем SL в минимальный BE
                // Порог мягкий (0.30 ATR), чтобы не ждать 1.2 ATR

                if (sl != null)
                {
                    var keey = BuildBeKey(symbol, side, entry);
                    var stage = _beStage.GetValueOrDefault(keey, BeStage.None);

                    if (stage < BeStage.Rehydrate)
                    {
                        var last = klines[^1].ClosePrice;

                        bool eligible =
                            side == PositionSide.Long
                                ? last >= entry + atr14 * 0.30m
                                : last <= entry - atr14 * 0.30m;

                        if (eligible)
                        {
                            decimal rehydrateBe =
                                side == PositionSide.Long
                                    ? entry + entry * 0.0004m
                                    : entry - entry * 0.0004m;

                            var ok = await UpdateSL_ProAsync(
                                client, symbol, side, qtyAbs, sl, entry, rehydrateBe, signal, ct);

                            if (ok)
                            {
                                _beStage[keey] = BeStage.Rehydrate;

                                _logger.LogWarning(
                                    "[BE][REHYDRATE][{symbol}][{side}] SL={sl}",
                                    symbol, side, rehydrateBe);
                            }
                        }
                    }
                }

                // 1) EARLY TP (ЖЁСТКО после BE) — у тебя уже есть HARD RULE внутри метода
                await TryEarlyPartialTakeAsync(client, symbol, side, qtyAbs, entry, atr14, signal, klines, ct);

                // 2) Стандартный BE по ATR (сработает позже, когда реально будет +1.2 ATR)
                if (sl != null)
                    await TryMoveSlToBeAsync(client, symbol, side, qtyAbs, entry, atr14, sl, signal, klines, ct);
            }


            // PROFIT HARVEST (ПОСЛЕ early/BE, ДО restore SL/TP)
            // =================================================================
            if (klines != null && klines.Count >= 50)
            {
                decimal aiEdgeScore =
                    _regimeNow is MarketRegime.StrongUpTrend or MarketRegime.StrongDownTrend
                        ? 0.82m
                        : 0.62m;

                await TryHarvestProfitAsync(
                    client,
                    _engineState,
                    symbol,
                    side,
                    pos,
                    klines,
                    aiEdgeScore,
                    minUsd: 4m,
                    ct);
            }



            // 1) SL отсутствует → аварийный SL (если нет дублей)
            if (sl == null && !hasMultipleSL)
            {
                await CreateEmergencySLAsync(client, symbol, side, qtyAbs, entry, signal, ct);
                _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] SL restored", symbol, side);
                //return;
            }

            // 2) TP отсутствует → аварийный TP (если нет дублей)
            if (tp == null && !hasMultipleTP)
            {
                await CreateEmergencyTPAsync(client, symbol, side, qtyAbs, entry, signal, ct);
                _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] TP restored", symbol, side);
                // return;
            }

            // 3) Трейлинг + раннер
            if (klines != null && klines.Count >= 50)
            {
                await ManageRunnerTpAsync(client, symbol, side, qtyAbs, entry, orders, signal, klines, ct);
                await ManageRunnerTpExtensionAsync(client, symbol, side, qtyAbs, entry, signal, orders, klines, ct);
                await MultiLayerTrailingAsync(client, symbol, side, qtyAbs, entry, signal, orders, klines, ct);
            }
        }


        private async Task<decimal?> ResolveEntryFromExchangeAsync(
    BinanceRestClient client,
    string symbol,
    PositionSide side,
    CancellationToken ct)
        {
            var trades = await client.UsdFuturesApi.Trading.GetUserTradesAsync(
                symbol: symbol,
                limit: 50,
                ct: ct);

            if (!trades.Success || trades.Data == null)
                return null;

            var lastOpen = trades.Data
                .Where(t => t.PositionSide == side)
                .OrderByDescending(t => t.Timestamp)
                .FirstOrDefault();

            return lastOpen?.Price;
        }

        private void MarkProtection(string symbol)
        {
            var key = EngineState.Key(symbol);
            var st = _engineState.Symbols.GetOrAdd(key, _ => new SymbolState());
            st.LastProtectionUtc = DateTime.UtcNow;

            _logger.LogInformation(
                "[PROTECTION][{symbol}] protection marked @ {time}",
                symbol, st.LastProtectionUtc);
        }

        // =====================================================================
        // EARLY TP (Partial close) — ключевой фикс v8.2
        // =====================================================================
        private async Task TryEarlyPartialTakeAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entry,
            decimal atr,
            TradeSignal? signal,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            CancellationToken ct)
        {
            // Блокируем, если LiquidityGuard сигналит опасность (не лезем в рынок лишний раз)
            if (_liquidityGuard.LastDanger?.Block == true)
                return;

            // ⚠️ skip early TP if liquidity was recent (soft protection)
            // if liquidity recent → wait a bit, but allow early TP later
            if (_liquidityGuard.IsDangerRecent(TimeSpan.FromMinutes(5)))
            {
                if (DateTime.UtcNow - _liquidityGuard.LastDanger!.UtcTime < TimeSpan.FromMinutes(2))
                    return;
            }

            var last = klines[^1].ClosePrice;

            var lastCandle = klines[^1];
            var body = Math.Abs(lastCandle.ClosePrice - lastCandle.OpenPrice);
            var wickAgainst =
                side == PositionSide.Long
                    ? lastCandle.HighPrice - lastCandle.ClosePrice
                    : lastCandle.ClosePrice - lastCandle.LowPrice;

            // если свеча с хвостом против — это не импульс
            if (wickAgainst > body * 0.8m)
                return;


            bool reached =
    side == PositionSide.Long
        ? last >= entry + atr * 0.90m
        : last <= entry - atr * 0.90m;

            if (!reached) return;

            var guardKey = BuildPosGuardKey(symbol, side, entry, qty);

            // =======================
            // 🔒 HARD RULE (PROP-DESK):
            // BE → потом PARTIAL
            // =======================
            if (!_beMoved.ContainsKey(guardKey))
                return;

            if (_earlyTpDone.ContainsKey(guardKey))
                return;

            var closeQty = Math.Round(qty * 0.35m, 8);
            if (closeQty <= 0) return;

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            _dispatcher.Enqueue(async ct =>
            {
                using var c = _factory.CreateRestClient();
                var res = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: closeSide,
                    type: FuturesOrderType.Market,
                    quantity: closeQty,
                    positionSide: side,
                    ct: ct);


                if (!res.Success)
                {
                    _logger.LogWarning("[EARLY-TP][{symbol}][{side}] Market partial close failed: {err}", symbol, side, res.Error);
                    return;
                }


                MarkProtection(symbol);

            });


            _earlyTpDone[guardKey] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _earlyTpDone[guardKey] = now;

            // 🔒 BLOCK HARVEST for 8 seconds after EARLY-TP
            _recentPartialClose[$"{symbol}|{side}"] = now;

            _logger.LogWarning(
                "[EARLY-TP][{symbol}][{side}] Partial profit fixed {closed}/{total} @price={price} (+0.9ATR)",
                symbol, side, closeQty, qty, last);

            // Optional learning hook
            try
            {
                if (signal != null && !signal.IsManual)
                {
                    var sigSide = side == PositionSide.Long ? SignalSide.Buy : SignalSide.Sell;
                    _aiLearning.RecordTrade(symbol, sigSide, entry, last, _regimeNow);
                }
            }
            catch { }
        }

        enum BeStage
        {
            None = 0,
            Rehydrate = 1,
            Atr = 2,
            Trailing = 3
        }

        private string BuildBeKey(string symbol, PositionSide side, decimal entry)
    => $"{symbol}:{side}:{entry}";


        // =====================================================================
        // SL → BE (+ buffer, structural-aware, liquidity-safe)
        // =====================================================================
        private async Task TryMoveSlToBeAsync(
        BinanceRestClient client,
        string symbol,
        PositionSide side,
        decimal qty,
        decimal entry,
        decimal atr,
        BinanceUsdFuturesOrder slOrder,
        TradeSignal? signal,
        IReadOnlyList<BinanceFuturesUsdtKline> klines,
        CancellationToken ct)
        {
            if (klines == null || klines.Count < 10) return;
            if (atr <= 0 || entry <= 0) return;

            var key = BuildBeKey(symbol, side, entry);
            var stage = _beStage.GetValueOrDefault(key, BeStage.None);

            if (stage >= BeStage.Atr)
                return;

            var last = klines[^1].ClosePrice;

            bool lowAtr = atr / entry < 0.004m;

            decimal trigger = lowAtr
                ? Math.Max(atr * 0.25m, entry * 0.0006m)
                : atr * 1.20m;

            bool reached =
                side == PositionSide.Long
                    ? last >= entry + trigger
                    : last <= entry - trigger;

            if (!reached)
            {
                _logger.LogDebug("[BE][ATR][{symbol}] not reached last={last} trigger={trigger}");
                return;
            }

            decimal buffer = lowAtr
                ? entry * 0.0005m
                : atr * 0.15m;

            if (_liquidityGuard.IsDangerRecent(TimeSpan.FromSeconds(90)))
                buffer *= 0.5m;

            decimal structural =
                side == PositionSide.Long
                    ? klines.TakeLast(5).Min(x => x.LowPrice)
                    : klines.TakeLast(5).Max(x => x.HighPrice);

            decimal beBase =
                side == PositionSide.Long
                    ? entry + buffer
                    : entry - buffer;

            // ❗ structural НЕ МОЖЕТ УХУДШАТЬ BE
            decimal finalSl =
                side == PositionSide.Long
                    ? Math.Max(beBase, structural)
                    : Math.Min(beBase, structural);

            decimal? oldSl =
                slOrder.StopPrice > 0
                    ? slOrder.StopPrice
                    : entry;


            bool improves =
                side == PositionSide.Long
                    ? finalSl > oldSl
                    : finalSl < oldSl;

            if (!improves)
            {
                _logger.LogDebug(
                    "[BE][ATR][{symbol}] no improve old={old} new={new}",
                    symbol, oldSl, finalSl);
                return;
            }

            var ok = await UpdateSL_ProAsync(
                client, symbol, side, qty, slOrder, entry, finalSl, signal, ct);

            if (!ok) return;

            _beStage[key] = BeStage.Atr;
            MarkProtection(symbol);

            _logger.LogWarning(
                "[BE][ATR][{symbol}][{side}] SL={sl}",
                symbol, side, finalSl);
        }


        private static string BuildPosGuardKey(string symbol, PositionSide side, decimal entry, decimal qty)
        {
            // грубый, но рабочий ключ: символ+side+entry+qty (округлим)
            string E(decimal v) => v.ToString("0.########", CultureInfo.InvariantCulture);
            return $"{symbol}|{side}|e={E(entry)}|q={E(qty)}";
        }
        // =====================================================================
        // EMERGENCY SL  (TRY NORMAL → FALLBACK ALGO RAW on -4120)
        // =====================================================================
        private async Task CreateEmergencySLAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entryPrice,
            TradeSignal? signal,
            CancellationToken ct)
        {
            try
            {
                // ==========================================================
                // 1) ЖЁСТКАЯ ПРОВЕРКА ФАКТИЧЕСКОЙ ПОЗИЦИИ
                // ==========================================================
                var posInfo = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, ct: ct);
                if (!posInfo.Success || posInfo.Data == null)
                    return;

                var pos = posInfo.Data.FirstOrDefault(p =>
                    p.PositionSide == side &&
                    Math.Abs(p.Quantity) > 0);

                if (pos == null)
                {
                    _logger.LogWarning(
                        "[SUPERVISOR][{symbol}][{side}] SKIP SL → no open position",
                        symbol, side);
                    return;
                }

                // ==========================================================
                // 2) КОЛИЧЕСТВО ЗАКРЫТИЯ
                // ==========================================================
                var closeQty = Math.Min(Math.Abs(pos.Quantity), qty);
                if (closeQty <= 0)
                    return;

                // ==========================================================
                // 3) РАСЧЁТ SL
                // ==========================================================
                decimal rawSl;

                if (signal?.StopLoss > 0)
                {
                    rawSl = signal.StopLoss;
                }
                else
                {
                    var kl = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 100);
                    if (kl.Count < 30) return;

                    var atr = _marketData.CalculateAtr(kl, 14);
                    if (atr <= 0) return;

                    var atrMult = _regimeNow switch
                    {
                        MarketRegime.Range => 1.2m,
                        MarketRegime.Squeeze => 1.5m,
                        MarketRegime.UpTrend or MarketRegime.DownTrend => 1.8m,
                        MarketRegime.VolatileChop => 2.0m,
                        _ => 2.2m
                    };

                    rawSl = side == PositionSide.Long
                        ? entryPrice - atr * atrMult
                        : entryPrice + atr * atrMult;
                }

                // ==========================================================
                // 4) TICK + MARK PRICE SAFETY
                // ==========================================================
                var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
                var tick = filters.tickSize > 0 ? filters.tickSize : 0.0001m;

                decimal sl = Math.Round(rawSl / tick) * tick;
                decimal mark = await GetMarkPriceSafeAsync(client, symbol, entryPrice, ct);

                if (mark > 0)
                {
                    if (side == PositionSide.Long && sl >= mark)
                        sl = mark - tick;

                    if (side == PositionSide.Short && sl <= mark)
                        sl = mark + tick;
                }

                var orderSide = side == PositionSide.Long
                    ? OrderSide.Sell
                    : OrderSide.Buy;

                // ==========================================================
                // 5) ОТПРАВКА (NORMAL → ALGO RAW)
                // ==========================================================
                _dispatcher.Enqueue(async ct =>
                {
                    using var c = _factory.CreateRestClient();
                    var res = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: symbol,
                        side: orderSide,
                        type: FuturesOrderType.StopMarket,
                        quantity: closeQty,
                        positionSide: side,
                        stopPrice: sl,
                        reduceOnly: null,
                        ct: ct);

                    if (res.Success)
                    {
                        _logger.LogInformation(
                            "[SUPERVISOR] SL CREATED (NORMAL) {symbol} {side} sl={sl}",
                            symbol, side, sl);
                        return;
                    }

                    // ======================================================
                    // 6) FALLBACK → ALGO RAW (-4120)
                    // ======================================================
                    if (IsAlgoRequired(res.Error))
                    {
                        _logger.LogWarning(
                            "[SUPERVISOR] SL requires ALGO endpoint (-4120) → RAW ALGO {symbol} {side}",
                            symbol, side);

                        var ok = await _algoRaw.PlaceConditionalAsync(
                            symbol: symbol,
                            side: orderSide,
                            positionSide: side,
                            type: "STOP_MARKET",
                            quantity: closeQty,
                            triggerPrice: sl,
                            workingType: "CONTRACT_PRICE",
                            reduceOnly: null,
                            ct: ct);

                        if (ok)
                        {
                            _logger.LogInformation(
                                "[SUPERVISOR] SL CREATED (ALGO-RAW) {symbol} {side} sl={sl}",
                                symbol, side, sl);
                            return;
                        }

                        _logger.LogWarning(
                            "[SUPERVISOR] SL skipped: ALGO-RAW failed {symbol} {side}",
                            symbol, side);
                        return;
                    }

                    _logger.LogError(
                        "[SUPERVISOR] ERROR SL create (NORMAL) {symbol}: {err}",
                        symbol, res.Error);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SUPERVISOR] EX SL create {symbol}", symbol);
            }
        }

        // =====================================================================
        // EMERGENCY TP  (TRY NORMAL → FALLBACK ALGO RAW on -4120)
        // =====================================================================
        private async Task CreateEmergencyTPAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entryPrice,
            TradeSignal? signal,
            CancellationToken ct)
        {
            try
            {
                // ==========================================================
                // 1) ЖЁСТКАЯ ПРОВЕРКА ФАКТИЧЕСКОЙ ПОЗИЦИИ (ОБЯЗАТЕЛЬНО)
                // ==========================================================
                //var posInfo = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, ct: ct);
                //if (!posInfo.Success || posInfo.Data == null)
                //    return;

                //var pos = posInfo.Data.FirstOrDefault(p =>
                //    p.PositionSide == side &&
                //    Math.Abs(p.Quantity) > 0);

                //if (pos == null)
                //{
                //    _logger.LogWarning(
                //        "[SUPERVISOR][{symbol}][{side}] SKIP TP → no open position",
                //        symbol, side);
                //    return;
                //}

                // ==========================================================
                // 2) КОЛИЧЕСТВО ЗАКРЫТИЯ (НЕ БОЛЬШЕ ЧЕМ ФАКТИЧЕСКАЯ ПОЗИЦИЯ)
                // ==========================================================
                //var closeQty = Math.Min(Math.Abs(pos.Quantity), qty);
                //if (closeQty <= 0)
                //    return;
                qty = Math.Abs(qty);

                if (qty == 0)
                {
                    _logger.LogWarning(
                        "[SUPERVISOR][{symbol}][{side}] SKIP TP → qty=0",
                        symbol, side);
                    return;
                }
                // ==========================================================
                // 3) РАСЧЁТ TP
                // ==========================================================
                decimal trigger;

                if (signal?.TakeProfits != null && signal.TakeProfits.Count > 0)
                {
                    trigger = signal.TakeProfits[0];
                }
                else
                {
                    var kl = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 100);
                    if (kl.Count < 30) return;

                    var atr = _marketData.CalculateAtr(kl, 14);
                    if (atr <= 0) return;

                    trigger = side == PositionSide.Long
                        ? entryPrice + atr * 1.7m
                        : entryPrice - atr * 1.7m;
                }

                // ==========================================================
                // 4) ПРИВЯЗКА К TICK SIZE
                // ==========================================================
                var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);

                var tick = filters.tickSize > 0 ? filters.tickSize : 0.0001m;
                var step = filters.step > 0 ? filters.step : 0.0001m;

                // normalize qty
                qty = Math.Floor(qty / step) * step;
                if (qty <= 0)
                {
                    _logger.LogWarning(
                        "[SUPERVISOR][{symbol}] TP qty normalized to 0",
                        symbol);
                    return;
                }


                trigger = await NormalizeTriggerPriceAsync(
    client,
    symbol,
    side,
    entryPrice,
    filters.tickSize,
    true,
    ct);

                //if (side == PositionSide.Long && trigger <= entryPrice)
                //    trigger = entryPrice + tick * 3;

                //if (side == PositionSide.Short && trigger >= entryPrice)
                //    trigger = entryPrice - tick * 3;

                var orderSide = side == PositionSide.Long
                    ? OrderSide.Sell
                    : OrderSide.Buy;

                // ==========================================================
                // 5) ОТПРАВКА ЧЕРЕЗ ДИСПЕТЧЕР (NORMAL → ALGO RAW)
                // ==========================================================
                _dispatcher.Enqueue(async _ =>
                {
                    using var c = _factory.CreateRestClient();
                    var res = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: symbol,
                        side: orderSide,
                        type: FuturesOrderType.TakeProfitMarket,
                        quantity: qty,
                        positionSide: side,
                        stopPrice: trigger,
                        reduceOnly: null,
                        ct: CancellationToken.None);

                    if (res.Success)
                    {
                        _logger.LogInformation(
                            "[SUPERVISOR] TP CREATED (NORMAL) {symbol} {side} tp={tp}",
                            symbol, side, trigger);
                        return;
                    }

                    // ======================================================
                    // 6) FALLBACK → ALGO RAW (-4120)
                    // ======================================================
                    if (IsAlgoRequired(res.Error))
                    {
                        _logger.LogWarning(
                            "[SUPERVISOR] TP requires ALGO endpoint (-4120) → RAW ALGO {symbol} {side}",
                            symbol, side);

                        _dispatcher.Enqueue(async _ =>
                        {
                            var ok = await _algoRaw.PlaceConditionalAsync(
                                symbol: symbol,
                                side: orderSide,
                                positionSide: side,
                                type: "TAKE_PROFIT_MARKET",
                                quantity: qty,
                                triggerPrice: trigger,
                                workingType: "CONTRACT_PRICE",
                                reduceOnly: null,
                                ct: CancellationToken.None);

                            if (ok)
                            {
                                _logger.LogInformation(
                     "[SUPERVISOR] TP CREATED (ALGO) {symbol} {side}",
                     symbol, side);
                                return;
                            }

                            _logger.LogError(
                  "[SUPERVISOR] TP ALGO FAILED {symbol} {side}",
                  symbol, side);
                        });

                        return;
                    }


                    _logger.LogError(
                        "[SUPERVISOR] TP FAILED {symbol} {side} {err}",
                        symbol, side, res.Error?.Message);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SUPERVISOR] EX create TP {symbol}", symbol);
            }
        }


        private static bool IsAlgoRequired(CryptoExchange.Net.Objects.Error? err)
        {
            if (err == null) return false;

            if (err.Code == -4120) return true;

            var msg = (err.Message ?? string.Empty).ToLowerInvariant();
            return msg.Contains("algo order api") || msg.Contains("algoorder") || msg.Contains("not supported for this endpoint");
        }

        // =====================================================================
        // PLACEHOLDERS: keep your existing methods below
        // =====================================================================
        private async Task ManageRunnerTpExtensionAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entryPrice,
            TradeSignal? signal,
            List<BinanceUsdFuturesOrder> orders,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            CancellationToken ct)
        {
            await Task.CompletedTask;
        }
        // =====================================================================
        // RUNNER (твоя логика) + SL update (v8.2 PRO)
        // =====================================================================
        private async Task ManageRunnerTpAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entryPrice,
            List<BinanceUsdFuturesOrder> orders,
            TradeSignal? signal,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            CancellationToken ct)
        {
            // ===== STRONG TREND RUNNER FIX =====
            bool strongTrend =
                _regimeNow == MarketRegime.StrongUpTrend ||
                _regimeNow == MarketRegime.StrongDownTrend;

            if (!strongTrend)
                return;


            if (signal?.TakeProfit is null || signal.TakeProfit <= 0)
                return;

            var priceRes = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(symbol, ct: ct);
            if (!priceRes.Success || priceRes.Data == null)
                return;

            var price = priceRes.Data.Price;

            bool tpHit =
                side == PositionSide.Long ? price >= signal.TakeProfit : price <= signal.TakeProfit;

            if (!tpHit) return;

            bool trendOk =
                side == PositionSide.Long
                    ? _regimeNow is MarketRegime.StrongUpTrend or MarketRegime.UpTrend
                    : _regimeNow is MarketRegime.StrongDownTrend or MarketRegime.DownTrend;

            if (!trendOk) return;

            var atr = signal.Atr ?? 0m;
            if (atr <= 0 || klines.Count < 2) return;

            var last = klines[^1];
            var body = Math.Abs(last.ClosePrice - last.OpenPrice);
            if (body < atr * 0.7m) return;

            if (_liquidityGuard.LastDanger?.Block == true) return;

            // 1) частичное закрытие (70%)
            var closeQty = Math.Round(qty * 0.7m, 8);
            var runnerQty = qty - closeQty;
            if (closeQty <= 0 || runnerQty <= 0) return;

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
            _dispatcher.Enqueue(async ct =>
            {
                await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: closeSide,
                type: FuturesOrderType.Market,
                quantity: closeQty,
                positionSide: side,
                ct: ct);

                _logger.LogInformation("[TP-EXT][{symbol}] Partial TP executed {closed}/{total}, runner={runner}",
                    symbol, closeQty, qty, runnerQty);

                // 2) перенос SL → BE + 0.25 ATR
                decimal newSl =
                    side == PositionSide.Long ? entryPrice + atr * 0.25m : entryPrice - atr * 0.25m;

                var slOrder = orders.FirstOrDefault(o =>
                    o.Type == FuturesOrderType.StopMarket &&
                    o.PositionSide == side);

                if (slOrder != null)
                {
                    await UpdateSL_ProAsync(client, symbol, side, runnerQty, slOrder, entryPrice, newSl, signal, ct);
                }

                _logger.LogWarning("[TP-EXT][{symbol}] Runner activated | new SL={sl}", symbol, newSl);
            });
        }

        /// <summary>
        /// v8.2 PRO SL update:
        /// - Cancel old SL
        /// - Place new SL via NORMAL endpoint
        /// - If -4120 -> ALGO-RAW
        /// - NO reduceOnly (важно для Hedge/ошибок -1106)
        /// - WorkingType.Mark используем осторожно: сначала пробуем, если Binance ругается — повтор без него
        /// </summary>
        private Task<bool> UpdateSL_ProAsync(
         BinanceRestClient client,
         string symbol,
         PositionSide side,
         decimal qty,
         BinanceUsdFuturesOrder slOrder,
         decimal entry,
         decimal newSl,
         TradeSignal? signal,
         CancellationToken ct)
        {
            if (qty <= 0 || newSl <= 0) return Task.FromResult(false);

            decimal oldSl = slOrder.StopPrice ?? slOrder.Price;
            if (oldSl <= 0) return Task.FromResult(false);

            if (side == PositionSide.Long && newSl <= oldSl) return Task.FromResult(false);
            if (side == PositionSide.Short && newSl >= oldSl) return Task.FromResult(false);

            _dispatcher.Enqueue(async token =>
            {
                using var c = _factory.CreateRestClient();

                var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
                var tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;
                var s = Math.Round(newSl / tick) * tick;

                try
                {
                    try
                    {
                        await c.UsdFuturesApi.Trading.CancelOrderAsync(symbol, slOrder.Id, ct: token);
                    }
                    catch { }

                    var orderSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

                    var r1 = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: symbol,
                        side: orderSide,
                        type: FuturesOrderType.StopMarket,
                        quantity: qty,
                        stopPrice: s,
                        positionSide: side,
                        workingType: WorkingType.Mark,
                        ct: token);

                    if (!r1.Success && IsAlgoRequired(r1.Error))
                    {
                        await _algoRaw.PlaceConditionalAsync(
                            symbol: symbol,
                            side: orderSide,
                            positionSide: side,
                            type: "STOP_MARKET",
                            quantity: qty,
                            triggerPrice: s,
                            workingType: "CONTRACT_PRICE",
                            reduceOnly: null,
                            ct: token);
                    }
                    else if (!r1.Success)
                    {
                        await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                            symbol: symbol,
                            side: orderSide,
                            type: FuturesOrderType.StopMarket,
                            quantity: qty,
                            stopPrice: s,
                            positionSide: side,
                            ct: token);
                    }
                    HookAiLearningOnSlMove(signal, symbol, side, entry, s);
                }
                catch { }
            });

            return Task.FromResult(true);
        }
        private void HookAiLearningOnSlMove(TradeSignal? signal, string symbol, PositionSide side, decimal entry, decimal newSl)
        {
            try
            {
                if (signal != null && !signal.IsManual)
                {
                    var sigSide = side == PositionSide.Long ? SignalSide.Buy : SignalSide.Sell;

                    _manualHandler.RegisterStop(symbol);
                    _aiLearning.RecordTrade(symbol, sigSide, entry, newSl, _regimeNow);
                }
            }
            catch { }
        }
        private async Task MultiLayerTrailingAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entryPrice,
            TradeSignal? signal,
            List<BinanceUsdFuturesOrder> orders,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            CancellationToken ct)
        {
            await Task.CompletedTask;
        }
        // =====================================================================
        // MARK PRICE SAFE
        // =====================================================================
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
            catch { }

            return fallback > 0 ? fallback : 0m;
        }
        // =====================================================================
        // RAW BINANCE ALGO ORDER (POST /fapi/v1/algoOrder)
        // =====================================================================
        private sealed class BinanceAlgoOrderRaw
        {
            private readonly HttpClient _http;
            private readonly ILogger _logger;
            private readonly string _apiKey;
            private readonly string _apiSecret;
            private readonly string _baseUrl;
            public BinanceAlgoOrderRaw(IConfiguration cfg, IHttpClientFactory httpFactory, ILogger logger)
            {
                _logger = logger;

                _apiKey = cfg["Binance:ApiKey"] ?? string.Empty;
                _apiSecret = cfg["Binance:ApiSecret"] ?? string.Empty;
                _baseUrl = (cfg["Binance:FuturesBaseUrl"] ?? "https://fapi.binance.com").TrimEnd('/');

                _http = httpFactory.CreateClient("BinanceAlgoRaw");
                _http.Timeout = TimeSpan.FromSeconds(8);
            }
            public async Task<bool> PlaceConditionalAsync(
                string symbol,
                OrderSide side,
                PositionSide positionSide,
                string type,
                decimal quantity,
                decimal triggerPrice,
                string workingType,
                bool? reduceOnly,
                CancellationToken ct)
            {
                if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
                {
                    _logger.LogError("[ALGO-RAW] Missing Binance:ApiKey / Binance:ApiSecret in config");
                    return false;
                }

                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string D(decimal v) => v.ToString("0.########", CultureInfo.InvariantCulture);

                var q = new List<KeyValuePair<string, string>>
                {
                    new("algoType", "CONDITIONAL"),
                    new("symbol", symbol),
                    new("side", side == OrderSide.Buy ? "BUY" : "SELL"),
                    new("type", type),
                    new("timestamp", ts.ToString(CultureInfo.InvariantCulture)),
                    new("workingType", workingType),
                    new("triggerPrice", D(triggerPrice)),
                    new("positionSide", positionSide.ToString().ToUpperInvariant()),
                    new("quantity", D(quantity))
                };

                // reduceOnly — только если positionSide == BOTH (в Hedge не шлём)
                if (reduceOnly.HasValue && positionSide == PositionSide.Both)
                    q.Add(new("reduceOnly", reduceOnly.Value ? "true" : "false"));

                var query = BuildQuery(q);
                var sig = Sign(query, _apiSecret);

                var url = $"{_baseUrl}/fapi/v1/algoOrder?{query}&signature={sig}";

                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.TryAddWithoutValidation("X-MBX-APIKEY", _apiKey);

                try
                {
                    using var resp = await _http.SendAsync(req, ct);
                    var body = await resp.Content.ReadAsStringAsync(ct);

                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogError("[ALGO-RAW] HTTP {code} body={body}", (int)resp.StatusCode, body);
                        return false;
                    }

                    _logger.LogInformation("[ALGO-RAW] OK {symbol} {type} posSide={ps} trig={tp} body={body}",
                        symbol, type, positionSide, triggerPrice, body);

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ALGO-RAW] EX PlaceConditionalAsync {symbol}", symbol);
                    return false;
                }
            }

            private static string BuildQuery(IEnumerable<KeyValuePair<string, string>> q)
            {
                var sb = new StringBuilder();
                foreach (var kv in q)
                {
                    if (sb.Length > 0) sb.Append('&');
                    sb.Append(Uri.EscapeDataString(kv.Key));
                    sb.Append('=');
                    sb.Append(Uri.EscapeDataString(kv.Value));
                }
                return sb.ToString();
            }

            private static string Sign(string queryString, string secret)
            {
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(queryString));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }



        }

        private async Task TryHarvestProfitAsync(
            BinanceRestClient client,
            EngineState state,
            string symbol,
            PositionSide side,
            BinancePositionDetailsUsdt pos,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            decimal aiEdgeScore,
            decimal minUsd,
            CancellationToken ct)
        {

            // ==========================================================
            // 🔒 BLOCK HARVEST right after EARLY-TP (Binance sync lag)
            // ==========================================================
            var harvestKey = $"{symbol}|{side}";

            if (_recentPartialClose.TryGetValue(harvestKey, out var ts))
            {
                var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ts;
                if (ageMs < 8000) // 8 seconds hard block
                {
                    _logger.LogInformation(
                        "[HARVEST][{symbol}][{side}] SKIP → recent EARLY-TP ({ms}ms)",
                        symbol, side, ageMs);
                    return;
                }

                _recentPartialClose.TryRemove(harvestKey, out _);
            }




            var sKey = EngineState.Key(symbol);
            var st = state.Symbols.GetOrAdd(sKey, _ => new SymbolState());

            // throttle
            if ((DateTime.UtcNow - st.LastHarvestUtc) < TimeSpan.FromMinutes(3))
                return;

            // ==========================================================
            // 🔒 ЖЁСТКАЯ ПРОВЕРКА ФАКТИЧЕСКОЙ ПОЗИЦИИ
            // ==========================================================
            var posInfo = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
            if (!posInfo.Success || posInfo.Data == null)
                return;

            var realPos = posInfo.Data.FirstOrDefault(p =>
                p.Symbol == symbol &&
                p.PositionSide == side &&
                Math.Abs(p.Quantity) > 0);

            if (realPos == null)
            {
                _logger.LogWarning("[HARVEST][{symbol}][{side}] SKIP → no open position", symbol, side);
                return;
            }

            decimal qty = Math.Abs(realPos.Quantity);
            if (qty <= 0) return;

            // ==========================================================
            // uPnL
            // ==========================================================
            decimal uPnl;
            try { uPnl = realPos.UnrealizedPnl; }
            catch { return; }

            // ==========================================================
            // dynamic minUsd (PRO, non-invasive)
            // ==========================================================

            // absolute floor (fees / noise protection)
            const decimal ABS_MIN_USD = 3.8m;

            // notional-based floor (≈0.12% position size)
            decimal notionalUsd = Math.Abs(realPos.EntryPrice * realPos.Quantity);
            decimal minByNotional = notionalUsd * 0.0012m;

            // ATR-based floor (will be clamped later)
            decimal atrTmp = _marketData.CalculateAtr(klines);
            if (atrTmp <= 0) atrTmp = 0.00000001m;
            decimal minByAtr = atrTmp * Math.Abs(realPos.Quantity) * 0.25m;

            // final minUsd (keep variable name, keep logic below)
            minUsd = Math.Max(
              ABS_MIN_USD,
              Math.Min(minByNotional, minByAtr)
          );

            if (symbol == "BTCUSDT")
                return;
            if (uPnl <= 0m || uPnl < minUsd)
                return;

            decimal atr = _marketData.CalculateAtr(klines);
            if (atr <= 0) atr = 0.00000001m;

            var last = klines[^1];
            var body = Math.Abs(last.ClosePrice - last.OpenPrice);

            // сильный импульс → не режем
            if (
                (_regimeNow == MarketRegime.StrongUpTrend ||
                 _regimeNow == MarketRegime.StrongDownTrend)
                && body > atr * 1.1m
            )
            {
                _logger.LogInformation(
                    "[HARVEST][{symbol}] SKIP → trend expansion",
                    symbol);
                return;
            }



            decimal rr = Math.Abs(realPos.MarkPrice - realPos.EntryPrice) / atr;

            decimal harvestPct =
                aiEdgeScore >= 0.80m && rr >= 1.4m ? 0.22m :
                aiEdgeScore >= 0.70m ? 0.28m :
                0.45m;

            //  decimal closeQty = Math.Round(qty * harvestPct, 8);
            // if (closeQty <= 0) return;

            // ==========================================================
            // 🔥 FULL vs PARTIAL CLOSE LOGIC (КЛЮЧЕВО)
            // ==========================================================

            decimal closeQty = qty * harvestPct;

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            var step = filters.step > 0 ? filters.step : 1m;

            // ❗ КЛЮЧЕВО: всегда вниз
            closeQty = Math.Floor(closeQty / step) * step;

            if (closeQty < filters.minQty)
            {
                _logger.LogInformation(
                    "[HARVEST][{symbol}][{side}] SKIP → rounded closeQty {q} < minQty {min}",
                    symbol, side, closeQty, filters.minQty);
                return;
            }


            bool isFullClose = closeQty >= qty;

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
     symbol: symbol,
     side: closeSide,
     type: FuturesOrderType.Market,
     quantity: closeQty,
     positionSide: side,
     ct: ct);


            if (!res.Success)
            {
                _logger.LogWarning("[HARVEST][{symbol}][{side}] FAIL: {err}", symbol, side, res.Error);
                return;
            }

            decimal addToBucket = uPnl * harvestPct;
            st.RealizedPnlBucketUsd += Math.Max(0m, addToBucket);
            st.LastHarvestUtc = DateTime.UtcNow;
            st.HarvestsToday++;

            _logger.LogInformation(
                "[HARVEST][{symbol}][{side}] OK closeQty={q} uPnl={pnl:F2} addBucket={b:F2} edge={e:F2} rr={rr:F2}",
                symbol, side, closeQty, uPnl, addToBucket, aiEdgeScore, rr);

            _recentPartialClose[$"{symbol}|{side}"] =
    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        }

    }

}
