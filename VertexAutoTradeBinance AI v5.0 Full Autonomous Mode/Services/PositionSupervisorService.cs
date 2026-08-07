using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using CryptoExchange.Net.Objects;
using System.Collections.Concurrent;
using System.Globalization;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
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
        private readonly AiStopLossOptimizer _slOptimizer;
        private readonly AiSelfLearningService _aiLearning;
        private readonly MarketDataService _marketData;
        private readonly AiMarketRegimeService _regime;
        private readonly ManualPositionHandler _manualHandler;
        private readonly IAlgoOrderRawClient _algoRaw;
        private readonly ProtectionOrderService _protection;
        private readonly LiquidityGuardService _liquidityGuard;
        private readonly IOrderDispatcher _dispatcher;
        private MarketRegime _regimeNow;

        // === Anti-spam guards for EarlyTP / BE-move ===
        private readonly ConcurrentDictionary<string, long> _earlyTpDone = new();   // key -> unixMs
        private readonly ConcurrentDictionary<string, long> _beMoved = new();      // key -> unixMs
        private readonly ConcurrentDictionary<string, decimal> _restoredEntries = new();
        // === Harvest block after partial close ===
        private readonly ConcurrentDictionary<string, long> _recentPartialClose = new();

        private readonly EngineStateSnapshotService _stateSvc;
     



        public PositionSupervisorService(
            ILogger<PositionSupervisorService> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            AiStopLossOptimizer slOptimizer,
            AiSelfLearningService aiLearning,
            MarketDataService marketData,
            AiMarketRegimeService regime,
            ManualPositionHandler manualHandler,
            LiquidityGuardService liquidityGuard,
            IOrderDispatcher dispatcher,
            EngineStateSnapshotService stateSvc,
            IAlgoOrderRawClient algoRaw,
            ProtectionOrderService protection)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
            _slOptimizer = slOptimizer;
            _aiLearning = aiLearning;
            _marketData = marketData;
            _regime = regime;
            _manualHandler = manualHandler;

            _regimeNow = MarketRegime.Range;

            _algoRaw = algoRaw;
            _protection = protection;
            _liquidityGuard = liquidityGuard;
            _dispatcher = dispatcher;
            _stateSvc = stateSvc;

        }
        private EngineState _engineState => _stateSvc.State;
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

            // FIX: детект закрытия ДО фильтра по Quantity != 0.
            // Раньше нулевые позиции отсекались здесь, поэтому ветка
            // "prevQty != 0 && pos.Quantity == 0" в HandleSideAsync была
            // недостижима: результаты сделок не попадали в ai_learning.json,
            // а guard-словари _earlyTpDone/_beMoved никогда не чистились.
            DetectClosures(symbol, posInfo.Data);

            var positions = posInfo.Data
    .Where(p => p.Symbol == symbol && p.Quantity != 0m)
    .ToList();

            var longPos = positions.FirstOrDefault(p => p.PositionSide == PositionSide.Long);
            var shortPos = positions.FirstOrDefault(p => p.PositionSide == PositionSide.Short);
 

            var hasLong = longPos != null && longPos.Quantity != 0m;
            var hasShort = shortPos != null && shortPos.Quantity != 0m;

            if (!hasLong && !hasShort)
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
                klines1m = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 160);
                var rr = _regime.DetectRegime(symbol, KlineInterval.OneMinute, klines1m);
                if (rr != null) _regimeNow = rr.Regime;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SUPERVISOR] Klines load error {symbol}", symbol);
            }

            // 4) Обработка сторон
            if (hasLong)
                await HandleSideAsync(client, symbol, PositionSide.Long, longPos!, openOrders, lastSignal, klines1m, ct);

            if (hasShort)
                await HandleSideAsync(client, symbol, PositionSide.Short, shortPos!, openOrders, lastSignal, klines1m, ct);
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




        // =====================================================================
        // CLOSE DETECTOR (v2)
        // Вызывается ДО фильтрации нулевых позиций — иначе факт закрытия
        // невозможно увидеть в принципе.
        // =====================================================================
        private void DetectClosures(string symbol, IEnumerable<BinancePositionDetailsUsdt> all)
        {
            foreach (var side in new[] { PositionSide.Long, PositionSide.Short })
            {
                var pos = all.FirstOrDefault(p => p.Symbol == symbol && p.PositionSide == side);
                if (pos == null) continue;

                var key = $"{symbol}_{side}";
                var prevQty = _manualHandler.GetPrevQty(key);
                var prevEntry = _manualHandler.GetPrevEntry(key);

                _manualHandler.SetPrevState(key, pos.Quantity, pos.EntryPrice);

                if (prevQty == 0m || pos.Quantity != 0m)
                    continue;

                decimal exitPrice = pos.MarkPrice > 0 ? pos.MarkPrice : prevEntry;
                if (exitPrice <= 0 || prevEntry <= 0)
                    continue;

                var sigSide = side == PositionSide.Long ? SignalSide.Buy : SignalSide.Sell;

                try
                {
                    _aiLearning.RecordTrade(symbol, sigSide, entry: prevEntry, exit: exitPrice, regime: _regimeNow);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AI][{symbol}] RecordTrade failed on close", symbol);
                }

                _logger.LogWarning(
                    "[AI][{symbol}][{side}] POSITION CLOSED → ai_learning | entry={entry} exit={exit}",
                    symbol, side, prevEntry, exitPrice);

                bool isStopLoss = side == PositionSide.Long
                    ? exitPrice < prevEntry
                    : exitPrice > prevEntry;

                if (isStopLoss)
                {
                    _manualHandler.RegisterStop(symbol);
                    _logger.LogWarning("[STOP][{symbol}][{side}] StopLoss detected → cooldown registered", symbol, side);
                }

                var guardKey = BuildPosGuardKey(symbol, side, prevEntry, Math.Abs(prevQty));
                _earlyTpDone.TryRemove(guardKey, out _);
                _beMoved.TryRemove(guardKey, out _);
                _restoredEntries.TryRemove(key, out _);
                _recentPartialClose.TryRemove($"{symbol}|{side}", out _);
            }
        }

        private async Task<List<BinanceUsdFuturesOrder>> LoadOrdersAsync(BinanceRestClient client, string symbol)
        {
            var res = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol);
            return res.Success && res.Data != null ? res.Data.ToList() : new List<BinanceUsdFuturesOrder>();
        }

        // =====================================================================
        // HANDLE SIDE  (v8.2 PRO)
        // =====================================================================
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
            var key = $"{symbol}_{side}";

            if (qtyAbs <= 0)
            {
                _logger.LogInformation("[SUPERVISOR] {symbol} {side}: no qty", symbol, side);
                return;
            }

            // === entry price detect (use position entry first) ===
            //decimal entry = pos.EntryPrice;
            //if (entry <= 0 && signal != null && signal.Symbol == symbol)
            //    entry = signal.EntryPrice;

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
            else if (klines != null && klines.Count >= 30)
                atr14 = _marketData.CalculateAtr(klines, 14);

            // === Side-specific orders (Hedge) ===
            var orders = allOrders.Where(o => o.PositionSide == side).ToList();

            // === Find SL/TP ===
            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            bool hasMultipleSL = orders.Count(o => o.Type == FuturesOrderType.StopMarket) > 1;
            bool hasMultipleTP = orders.Count(o => o.Type == FuturesOrderType.TakeProfitMarket) > 1;

            if (hasMultipleTP)
                _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] Multiple TP detected → skip TP create", symbol, side);

            var sl = orders.FirstOrDefault(o => o.Side == closeSide && o.Type == FuturesOrderType.StopMarket);
            var tp = orders.FirstOrDefault(o => o.Side == closeSide && o.Type == FuturesOrderType.TakeProfitMarket);

            // =================================================================
            // 0) SL ВСЕГДА ПЕРВЫМ.
            //
            // Раньше EarlyTP / BE / Harvest выполнялись ДО восстановления стопа,
            // то есть частичные закрытия шли на незащищённой позиции.
            // Также при hasMultipleSL код просто писал warning и пропускал
            // создание — дубли жили до закрытия позиции.
            // =================================================================
            if (hasMultipleSL)
            {
                await _protection.CancelDuplicateStopsAsync(symbol, side, ct);
                orders = (await LoadOrdersAsync(client, symbol)).Where(o => o.PositionSide == side).ToList();
                sl = orders.FirstOrDefault(o => o.Side == closeSide && o.Type == FuturesOrderType.StopMarket);
            }

            if (sl == null)
            {
                var desired = ResolveDesiredStop(symbol, side, entry, atr14, signal);

                if (desired <= 0)
                {
                    _logger.LogError("[SUPERVISOR][{symbol}][{side}] SL level unresolved → abort side", symbol, side);
                    return;
                }

                var restored = await _protection.EnsureStopAsync(symbol, side, desired, ct);

                if (!restored.Success)
                {
                    _logger.LogCritical(
                        "[SUPERVISOR][{symbol}][{side}] SL RESTORE FAILED: {reason} → дальнейшие операции отменены",
                        symbol, side, restored.Reason);
                    return;
                }

                _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] SL restored @ {sl}", symbol, side, restored.StopPrice);

                orders = (await LoadOrdersAsync(client, symbol)).Where(o => o.PositionSide == side).ToList();
                sl = orders.FirstOrDefault(o => o.Side == closeSide && o.Type == FuturesOrderType.StopMarket);
            }

            // =================================================================
            // 1) EARLY PROFIT + BE MOVE — только на защищённой позиции
            // =================================================================
            if (klines != null && klines.Count >= 50 && atr14 > 0 && entry > 0)
            {
                // EARLY TP (partial 35% at +0.9 ATR)
                await TryEarlyPartialTakeAsync(client, symbol, side, qtyAbs, entry, atr14, signal, klines, ct);

                // SL -> BE when +1.2 ATR
                if (sl != null)
                    await TryMoveSlToBeAsync(client, symbol, side, qtyAbs, entry, atr14, sl, signal, klines, ct);
            }

            // =================================================================
            // 2) PROFIT HARVEST
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
                    minUsd: 6m,
                    ct);
            }

            // =================================================================
            // 3) TP отсутствует → аварийный TP (если нет дублей)
            // =================================================================
            if (tp == null && !hasMultipleTP)
            {
                await CreateEmergencyTPAsync(client, symbol, side, qtyAbs, entry, signal, ct);
            }

            // 4) Трейлинг + раннер
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

            var last = klines[^1].ClosePrice;

            bool reached =
                side == PositionSide.Long
                    ? last >= entry + atr * 0.90m
                    : last <= entry - atr * 0.90m;

            if (!reached) return;

            var guardKey = BuildPosGuardKey(symbol, side, entry, qty);
            if (_earlyTpDone.ContainsKey(guardKey)) return;

            var closeQty = Math.Round(qty * 0.35m, 8);
            if (closeQty <= 0) return;

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            _dispatcher.Enqueue(async token =>
            {
                using var c = _factory.CreateRestClient();

                var res = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: closeSide,
                    type: FuturesOrderType.Market,
                    quantity: closeQty,
                    positionSide: side,
                    ct: token);

                if (!res.Success)
                {
                    _logger.LogWarning(
                        "[EARLY-TP][{symbol}][{side}] Market partial close failed: {err}",
                        symbol, side, res.Error);
                    return;
                }

                // =========================
                // SUCCESS PATH ONLY
                // =========================
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // mark Early-TP as done (authoritative)
                _earlyTpDone[guardKey] = now;

                // block harvest briefly (Binance sync lag)
                _recentPartialClose[$"{symbol}|{side}"] = now;

                // protection / BE pipeline allowed
                MarkProtection(symbol);

                _logger.LogWarning(
                    "[EARLY-TP][{symbol}][{side}] Partial profit fixed {closed}/{total} @price={price}",
                    symbol, side, closeQty, qty, last);

                // Optional AI learning hook (non-fatal)
                try
                {
                    if (signal != null && !signal.IsManual)
                    {
                        var sigSide = side == PositionSide.Long ? SignalSide.Buy : SignalSide.Sell;
                        _aiLearning.RecordTrade(symbol, sigSide, entry, last, _regimeNow);
                    }
                }
                catch
                {
                    // learning must never break execution
                }
            });
        }

        // =====================================================================
        // SL -> BE (безубыток + буфер) — ключевой фикс v8.2
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
            var last = klines[^1].ClosePrice;

            bool reached =
                side == PositionSide.Long
                    ? last >= entry + atr * 1.20m
                    : last <= entry - atr * 1.20m;

            if (!reached) return;

            var guardKey = BuildPosGuardKey(symbol, side, entry, qty);
            if (_beMoved.ContainsKey(guardKey)) return;

            decimal buffer = atr * 0.15m; // небольшой плюс к BE
            decimal newSl =
                side == PositionSide.Long
                    ? entry + buffer
                    : entry - buffer;

            // только если реально улучшает SL
            decimal oldSl = slOrder.StopPrice ?? slOrder.Price;
            if (oldSl <= 0) return;

            if (side == PositionSide.Long && newSl <= oldSl) return;
            if (side == PositionSide.Short && newSl >= oldSl) return;

            var ok = await UpdateSL_ProAsync(client, symbol, side, qty, slOrder, entry, newSl, signal, ct);
            if (ok)
            {
                _beMoved[guardKey] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _logger.LogWarning("[BE][{symbol}][{side}] SL moved to BE+buffer newSL={sl}", symbol, side, newSl);
               
            }
            MarkProtection(symbol);
        }

        private static string BuildPosGuardKey(string symbol, PositionSide side, decimal entry, decimal qty)
        {
            // грубый, но рабочий ключ: символ+side+entry+qty (округлим)
            string E(decimal v) => v.ToString("0.########", CultureInfo.InvariantCulture);
            return $"{symbol}|{side}|e={E(entry)}|q={E(qty)}";
        }
        // =====================================================================
        // РАСЧЁТ УРОВНЯ SL
        //
        // Заменяет CreateEmergencySLAsync. Старый метод слал ордер с
        // reduceOnly:true + positionSide, а Binance в Hedge Mode отклоняет
        // reduceOnly кодом -1106. IsAlgoRequired ловит только -4120, поэтому
        // фолбэка не было — стоп не вставал никогда, в лог падал только LogError.
        // Постановка теперь целиком в ProtectionOrderService (без reduceOnly,
        // closePosition:true, синхронно, с проверкой результата).
        //
        // Здесь остался только расчёт уровня.
        // =====================================================================
        private decimal ResolveDesiredStop(
            string symbol,
            PositionSide side,
            decimal entry,
            decimal atr,
            TradeSignal? signal)
        {
            if (entry <= 0)
                return 0m;

            // Сигнальный стоп годится, только если он на правильной стороне от входа.
            // SuperviseAsync вызывается из внешнего цикла с lastSignal = null, а
            // ManualPositionHandler подставляет виртуальный сигнал — стоп от чужой
            // стороны даёт мгновенный триггер или -2021.
            if (signal != null && signal.Symbol == symbol && signal.StopLoss > 0)
            {
                bool valid = side == PositionSide.Long
                    ? signal.StopLoss < entry
                    : signal.StopLoss > entry;

                if (valid)
                    return signal.StopLoss;

                _logger.LogWarning(
                    "[SUPERVISOR][{symbol}][{side}] signal.StopLoss={sl} на неверной стороне от entry={e} → ATR-фолбэк",
                    symbol, side, signal.StopLoss, entry);
            }

            if (atr <= 0)
                return 0m;

            var mult = _regimeNow switch
            {
                MarketRegime.Range => 1.2m,
                MarketRegime.Squeeze => 1.5m,
                MarketRegime.UpTrend or MarketRegime.DownTrend => 1.8m,
                MarketRegime.VolatileChop => 2.0m,
                _ => 2.2m
            };

            return side == PositionSide.Long
                ? entry - atr * mult
                : entry + atr * mult;
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
                var posInfo = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, ct: ct);
                if (!posInfo.Success || posInfo.Data == null)
                    return;

                var pos = posInfo.Data.FirstOrDefault(p =>
                    p.PositionSide == side &&
                    Math.Abs(p.Quantity) > 0);

                if (pos == null)
                {
                    _logger.LogWarning(
                        "[SUPERVISOR][{symbol}][{side}] SKIP TP → no open position",
                        symbol, side);
                    return;
                }

                // ==========================================================
                // 2) КОЛИЧЕСТВО ЗАКРЫТИЯ (НЕ БОЛЬШЕ ЧЕМ ФАКТИЧЕСКАЯ ПОЗИЦИЯ)
                // ==========================================================
                var closeQty = Math.Min(Math.Abs(pos.Quantity), qty);
                if (closeQty <= 0)
                    return;

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
                        ? entryPrice + atr * 1.25m
                        : entryPrice - atr * 1.25m;
                }

                // ==========================================================
                // 4) ПРИВЯЗКА К TICK SIZE
                // ==========================================================
                var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
                var tick = filters.tickSize > 0 ? filters.tickSize : 0.0001m;

                trigger = Math.Round(trigger / tick) * tick;

                if (side == PositionSide.Long && trigger <= entryPrice)
                    trigger = entryPrice + tick * 3;

                if (side == PositionSide.Short && trigger >= entryPrice)
                    trigger = entryPrice - tick * 3;

                var orderSide = side == PositionSide.Long
                    ? OrderSide.Sell
                    : OrderSide.Buy;

                // ==========================================================
                // 5) ОТПРАВКА ЧЕРЕЗ ДИСПЕТЧЕР (NORMAL → ALGO RAW)
                // ==========================================================
                _dispatcher.Enqueue(async ct =>
                {
                    using var c = _factory.CreateRestClient();

                    // FIX: reduceOnly убран — в Hedge Mode Binance отклоняет его
                    // кодом -1106, а IsAlgoRequired ловит только -4120,
                    // поэтому TP не вставал и фолбэк не срабатывал.
                    var res = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: symbol,
                        side: orderSide,
                        type: FuturesOrderType.TakeProfitMarket,
                        quantity: closeQty,
                        positionSide: side,
                        stopPrice: trigger,
                        workingType: WorkingType.Mark,
                        ct: ct);

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

                        _dispatcher.Enqueue(async ct =>
                        {
                            var ok = await _algoRaw.PlaceConditionalAsync(
                                symbol: symbol,
                                side: orderSide,
                                positionSide: side,
                                type: "TAKE_PROFIT_MARKET",
                                quantity: closeQty,
                                triggerPrice: trigger,
                                workingType: "MARK_PRICE",
                                reduceOnly: null,
                                closePosition: false,
                                ct: ct);

                            if (ok)
                            {
                                _logger.LogInformation(
                                    "[SUPERVISOR] TP CREATED (ALGO-RAW) {symbol} {side} tp={tp}",
                                    symbol, side, trigger);
                                return;
                            }

                            _logger.LogError(
                                "[SUPERVISOR] TP ALGO-RAW FAILED {symbol} {side}",
                                symbol, side);
                        });

                        return;
                    }

                    _logger.LogError(
                        "[SUPERVISOR] ERROR create TP (NORMAL) {symbol}: {err}",
                        symbol, res.Error);
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
        /// SL update v3.
        ///
        /// Было: старый стоп ОТМЕНЯЛСЯ, затем новый ставился внутри
        /// _dispatcher.Enqueue(...) с внешним catch {}. Если постановка падала,
        /// позиция оставалась голой и никто об этом не узнавал. Плюс метод
        /// возвращал Task.FromResult(true) ещё до отправки чего-либо.
        ///
        /// Стало: place-then-cancel внутри ProtectionOrderService, синхронно,
        /// с реальным результатом. Если новый стоп не встал — старый остаётся жив.
        /// </summary>
        private async Task<bool> UpdateSL_ProAsync(
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
            if (qty <= 0 || newSl <= 0) return false;

            decimal oldSl = slOrder.StopPrice ?? slOrder.Price;
            if (oldSl <= 0) return false;

            // Двигаем стоп только в сторону улучшения
            if (side == PositionSide.Long && newSl <= oldSl) return false;
            if (side == PositionSide.Short && newSl >= oldSl) return false;

            var res = await _protection.EnsureStopAsync(symbol, side, newSl, ct, replaceExisting: true);

            if (!res.Success)
            {
                _logger.LogError(
                    "[SL-UPDATE][{symbol}][{side}] новый SL не встал ({reason}) — позиция может остаться без стопа",
                    symbol, side, res.Reason);
                return false;
            }

            _logger.LogWarning(
                "[SL-UPDATE][{symbol}][{side}] SL {old} → {new}",
                symbol, side, oldSl, res.StopPrice);

            return true;
        }

        // FIX: HookAiLearningOnSlMove удалён из путей обновления стопа.
        // Он записывал в _aiLearning.RecordTrade «сделку» с exit = newSl при
        // ПЕРЕНОСЕ стопа в BE и дёргал _manualHandler.RegisterStop(symbol).
        // Перенос стопа — не закрытие: это отравляло обучающую выборку
        // фиктивными результатами и вешало cooldown на символ.
        // Запись результата теперь только в DetectClosures().
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
        // RAW BINANCE ALGO ORDER вынесен в Services/AlgoOrderRawClient.cs
        // (нужен и OrderExecutor'у, и Supervisor'у → один общий путь фолбэка -4120)
        // =====================================================================

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
                if (ageMs < 8_000) // 8 seconds hard block
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
            if ((DateTime.UtcNow - st.LastHarvestUtc) < TimeSpan.FromSeconds(90))
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

            if (uPnl <= 0m || uPnl < minUsd)
                return;

            decimal atr = _marketData.CalculateAtr(klines);
            if (atr <= 0) atr = 0.00000001m;

            decimal rr = Math.Abs(realPos.MarkPrice - realPos.EntryPrice) / atr;

            decimal harvestPct =
             (aiEdgeScore >= 0.75m && rr >= 1.10m) ? 0.22m :
             (aiEdgeScore >= 0.65m && rr >= 0.90m) ? 0.16m :
             (aiEdgeScore >= 0.55m && rr >= 0.70m) ? 0.12m :
             (rr >= 0.55m) ? 0.08m :
             0m;

            if (harvestPct <= 0m)
                return;

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

            if (closeQty >= qty)
            {
                closeQty = qty;
                isFullClose = true;
            }
            else
            {
                isFullClose = false;
            }

            if (isFullClose)
            {
                _logger.LogInformation(
                    "[HARVEST][{symbol}][{side}] FULL CLOSE via harvest",
                    symbol, side);
            }

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

            //decimal addToBucket = uPnl * harvestPct;
            decimal addToBucket = uPnl * (closeQty / qty);
            st.RealizedPnlBucketUsd += Math.Max(0m, addToBucket);

            if (closeQty > 0 && !isFullClose)
            {
                st.LastHarvestUtc = DateTime.UtcNow;
            }

            st.HarvestsToday++;

            _logger.LogInformation(
                "[HARVEST][{symbol}][{side}] OK closeQty={q} uPnl={pnl:F2} addBucket={b:F2} edge={e:F2} rr={rr:F2}",
                symbol, side, closeQty, uPnl, addToBucket, aiEdgeScore, rr);

            if (!isFullClose)
            {
                _recentPartialClose[$"{symbol}|{side}"] =
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        }

    }
}
