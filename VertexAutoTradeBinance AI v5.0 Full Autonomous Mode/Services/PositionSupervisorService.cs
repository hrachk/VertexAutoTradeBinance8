using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using CryptoExchange.Net.Objects;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// PositionSupervisorService v7.3 (ALGO FULL MODE, QUANT-REALTIME MAX)
    ///
    /// - Контроль Long / Short / Both
    /// - Авто-ремонт SL/TP (в т.ч. для МАНУАЛЬНЫХ поз)
    /// - Многоуровневый трейлинг (ATR + EMA + SuperTrend + micro-structure)
    /// - Динамический "HOLD" при сильном тренде (не тянем SL слишком рано)
    /// - Безопасная защита от -2021 (order would immediately trigger)
    /// - Manual + AI позиции (через ManualPositionHandler)
    /// - QUANT-LEARN: фикс закрытий
    /// - ВАЖНО: все SL/TP через PlaceConditionalOrderAsync (Algo Order API /fapi/v1/algoOrder)
    ///          Обычный PlaceOrderAsync для STOP*/TAKE_PROFIT* → -4120 с 2025-12-09
    ///          В hedge mode не слать reduceOnly вместе с positionSide (-1106)
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
        private readonly ExecutedSignalService _executed;
        private readonly ManagedPositionRegistry _managed;
        private readonly TradeSignalMemoryService _signalMemory;

        private MarketRegime _regimeNow;

        // === HYBRID v8.2-style (поверх биржевых SL/TP, не ломает restore) ===
        private readonly ConcurrentDictionary<string, long> _hybridEarlyDone = new();
        private readonly ConcurrentDictionary<string, long> _hybridBeDone = new();
        private readonly ConcurrentDictionary<string, long> _hybridRecentPartial = new();
        private readonly ConcurrentDictionary<string, DateTime> _hybridLastHarvest = new();

        // Внутренний уровень вероятности продолжения тренда
        private enum TrendContinuationLevel
        {
            Low,
            Medium,
            High
        }

        // Уровень "усталости" тренда (для TP exhaustion detector)
        private enum ExhaustionLevel
        {
            None = 0,
            Mild = 1,
            Strong = 2
        }

        public PositionSupervisorService(
            ILogger<PositionSupervisorService> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            AiStopLossOptimizer slOptimizer,
            AiSelfLearningService aiLearning,
            MarketDataService marketData,
            AiMarketRegimeService regime,
            ManualPositionHandler manualHandler,
            ExecutedSignalService executed,
            ManagedPositionRegistry managed,
            TradeSignalMemoryService signalMemory)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
            _slOptimizer = slOptimizer;
            _aiLearning = aiLearning;
            _marketData = marketData;
            _regime = regime;
            _manualHandler = manualHandler;
            _executed = executed;
            _managed = managed;
            _signalMemory = signalMemory;

            _regimeNow = MarketRegime.Range;
        }

        // --------------------------------------------------------------------
        // PATCH BLOCK: ручная проверка SL/TP для уже открытых позиций
        // --------------------------------------------------------------------

        // --------------------------------------------------------------------
        // SMART RESTORE: только bot-managed. Manual не трогаем.
        // --------------------------------------------------------------------
        private async Task EnsureProtectionForManagedAsync(
            BinanceRestClient client,
            string symbol,
            BinancePositionDetailsUsdt pos,
            PositionSide side,
            TradeSignal? signal,
            decimal tick,
            CancellationToken ct)
        {
            if (!_managed.CanAttemptRestore(symbol, side) && _managed.IsManaged(symbol, side))
            {
                _logger.LogWarning("[SUPERVISOR][{symbol}] restore paused (too many fails)", symbol);
                return;
            }

            // Если ещё не в registry, но signal бота есть — зарегистрировать
            if (!_managed.IsManaged(symbol, side) && signal != null && !signal.IsManual)
                _managed.RegisterFromSignal(signal, pos.EntryPrice);

            var info = _managed.Get(symbol, side);

            // AllowManualOverride / user cleared → не восстанавливаем
            if (info != null && (info.AllowManualOverride || info.UserClearedProtection))
            {
                _logger.LogInformation("[SUPERVISOR][{symbol}] manual override — skip restore", symbol);
                return;
            }

            bool hasSl = await HasStopLossAsync(client, symbol, side, ct);
            bool hasTp = await HasTakeProfitAsync(client, symbol, side, ct);

            if (hasSl && hasTp)
                return;

            decimal entry = pos.EntryPrice > 0 ? pos.EntryPrice : (signal?.EntryPrice ?? 0);
            decimal qty = Math.Abs(pos.Quantity);
            if (entry <= 0 || qty <= 0) return;

            if (!hasSl)
            {
                decimal sl = 0;
                if (info != null && info.CalculatedSL > 0) sl = info.CalculatedSL;
                else if (signal != null && signal.StopLoss > 0) sl = signal.StopLoss;

                if (sl <= 0)
                {
                    // fallback ATR ~1.3
                    try
                    {
                        var kl = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 100);
                        var atr = _marketData.CalculateAtr(kl, 14);
                        if (atr > 0)
                            sl = side == PositionSide.Long ? entry - atr * 1.3m : entry + atr * 1.3m;
                    }
                    catch { }
                }

                if (sl <= 0)
                    sl = side == PositionSide.Long ? entry * 0.987m : entry * 1.013m;

                sl = Math.Round(sl / tick) * tick;
                var ok = await PlaceManagedSlAsync(client, symbol, side, qty, sl, ct);
                _managed.MarkRestoreAttempt(symbol, side, ok);
                if (ok)
                    _logger.LogWarning("[SUPERVISOR][{symbol}] SMART RESTORE SL @ {sl} (bot-managed)", symbol, sl);
            }

            if (!hasTp)
            {
                decimal tp = 0;
                if (info != null && info.CalculatedTPs.Count > 0) tp = info.CalculatedTPs[0];
                else if (signal?.TakeProfit is > 0) tp = signal.TakeProfit.Value;
                else if (signal?.TakeProfits != null && signal.TakeProfits.Count > 0) tp = signal.TakeProfits[0];

                if (tp <= 0)
                {
                    try
                    {
                        var kl = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 100);
                        var atr = _marketData.CalculateAtr(kl, 14);
                        if (atr > 0)
                            tp = side == PositionSide.Long ? entry + atr * 2.0m : entry - atr * 2.0m;
                    }
                    catch { }
                }

                if (tp <= 0)
                    tp = side == PositionSide.Long ? entry * 1.02m : entry * 0.98m;

                tp = Math.Round(tp / tick) * tick;
                var ok = await PlaceManagedTpAsync(client, symbol, side, qty, tp, ct);
                _managed.MarkRestoreAttempt(symbol, side, ok);
                if (ok)
                    _logger.LogWarning("[SUPERVISOR][{symbol}] SMART RESTORE TP @ {tp} (bot-managed)", symbol, tp);
            }
        }

        private async Task<bool> HasStopLossAsync(BinanceRestClient client, string symbol, PositionSide side, CancellationToken ct)
        {
            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
            var orders = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct);
            if (orders.Success && orders.Data != null &&
                orders.Data.Any(o => o.Side == closeSide &&
                    (o.Type == FuturesOrderType.StopMarket || o.Type == FuturesOrderType.Stop) &&
                    (o.PositionSide == side || o.PositionSide == PositionSide.Both)))
                return true;

            try
            {
                var algo = await client.UsdFuturesApi.Trading.GetOpenConditionalOrdersAsync(symbol: symbol, ct: ct);
                if (algo.Success && algo.Data != null &&
                    algo.Data.Any(o => o.Side == closeSide &&
                        (o.Type == FuturesOrderType.StopMarket || o.Type == FuturesOrderType.Stop) &&
                        (o.PositionSide == side || o.PositionSide == PositionSide.Both)))
                    return true;
            }
            catch { }
            return false;
        }

        private async Task<bool> HasTakeProfitAsync(BinanceRestClient client, string symbol, PositionSide side, CancellationToken ct)
        {
            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
            var orders = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct);
            if (orders.Success && orders.Data != null &&
                orders.Data.Any(o => o.Side == closeSide &&
                    (o.Type == FuturesOrderType.TakeProfitMarket || o.Type == FuturesOrderType.TakeProfit ||
                     (o.Type == FuturesOrderType.Limit && o.ReduceOnly == true)) &&
                    (o.PositionSide == side || o.PositionSide == PositionSide.Both)))
                return true;

            try
            {
                var algo = await client.UsdFuturesApi.Trading.GetOpenConditionalOrdersAsync(symbol: symbol, ct: ct);
                if (algo.Success && algo.Data != null &&
                    algo.Data.Any(o => o.Side == closeSide &&
                        (o.Type == FuturesOrderType.TakeProfitMarket || o.Type == FuturesOrderType.TakeProfit) &&
                        (o.PositionSide == side || o.PositionSide == PositionSide.Both)))
                    return true;
            }
            catch { }
            return false;
        }

        private async Task<bool> PlaceManagedSlAsync(
            BinanceRestClient client, string symbol, PositionSide side, decimal qty, decimal sl, CancellationToken ct)
        {
            try
            {
                var res = await client.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
                    symbol: symbol,
                    side: side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                    type: ConditionalOrderType.StopMarket,
                    quantity: qty,
                    triggerPrice: sl,
                    positionSide: side,
                    workingType: WorkingType.Mark,
                    timeInForce: TimeInForce.GoodTillCanceled,
                    ct: ct);
                if (!res.Success)
                {
                    _logger.LogError("[SUPERVISOR] SMART SL fail {symbol}: {err}", symbol, res.Error);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SUPERVISOR] SMART SL exception {symbol}", symbol);
                return false;
            }
        }

        private async Task<bool> PlaceManagedTpAsync(
            BinanceRestClient client, string symbol, PositionSide side, decimal qty, decimal tp, CancellationToken ct)
        {
            try
            {
                var res = await client.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
                    symbol: symbol,
                    side: side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                    type: ConditionalOrderType.TakeProfitMarket,
                    quantity: qty,
                    triggerPrice: tp,
                    positionSide: side,
                    workingType: WorkingType.Mark,
                    timeInForce: TimeInForce.GoodTillCanceled,
                    ct: ct);
                if (!res.Success)
                {
                    _logger.LogError("[SUPERVISOR] SMART TP fail {symbol}: {err}", symbol, res.Error);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SUPERVISOR] SMART TP exception {symbol}", symbol);
                return false;
            }
        }

        private async Task<bool> EnsureStopLossExists(
            BinanceRestClient client,
            string symbol,
            BinancePositionDetailsUsdt pos,
            decimal tick)
        {
            var sideClose = pos.PositionSide == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            // Обычные ордера + Algo (conditional) — после миграции Binance SL только в algo
            var orders = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol);
            bool hasSL = orders.Success && orders.Data.Any(o =>
                o.Side == sideClose &&
                (o.Type == FuturesOrderType.StopMarket || o.Type == FuturesOrderType.Stop));

            if (!hasSL)
            {
                var algo = await client.UsdFuturesApi.Trading.GetOpenConditionalOrdersAsync(symbol: symbol);
                hasSL = algo.Success && algo.Data != null && algo.Data.Any(o =>
                    o.Side == sideClose &&
                    (o.Type == FuturesOrderType.StopMarket || o.Type == FuturesOrderType.Stop));
            }

            if (hasSL)
                return true;

            decimal sl = pos.PositionSide == PositionSide.Long
                ? pos.EntryPrice * 0.985m
                : pos.EntryPrice * 1.015m;

            sl = Math.Round(sl / tick) * tick;

            try
            {
                // Algo Order API (обязательно с 2025-12-09). Без reduceOnly при positionSide (hedge).
                var res = await client.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
                    symbol: symbol,
                    side: sideClose,
                    type: ConditionalOrderType.StopMarket,
                    quantity: Math.Abs(pos.Quantity),
                    triggerPrice: sl,
                    positionSide: pos.PositionSide,
                    workingType: WorkingType.Mark,
                    timeInForce: TimeInForce.GoodTillCanceled);

                if (!res.Success)
                {
                    _logger.LogError(
                        "[SUPERVISOR] ERROR Ensure SL create {symbol}: {err}",
                        symbol, res.Error);
                    return false;
                }

                _logger.LogWarning("[SUPERVISOR][{symbol}] MANUAL SL created @ {sl}", symbol, sl);
                return false; // SL только что восстановили
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SUPERVISOR] EX Ensure SL create {symbol}", symbol);
                return false;
            }
        }

        private async Task<bool> EnsureTakeProfitExists(
            BinanceRestClient client,
            string symbol,
            BinancePositionDetailsUsdt pos,
            decimal tick)
        {
            var sideClose = pos.PositionSide == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var orders = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol);
            bool hasTP = orders.Success && orders.Data.Any(o =>
                o.Side == sideClose &&
                (o.Type == FuturesOrderType.TakeProfitMarket || o.Type == FuturesOrderType.TakeProfit));

            if (!hasTP)
            {
                var algo = await client.UsdFuturesApi.Trading.GetOpenConditionalOrdersAsync(symbol: symbol);
                hasTP = algo.Success && algo.Data != null && algo.Data.Any(o =>
                    o.Side == sideClose &&
                    (o.Type == FuturesOrderType.TakeProfitMarket || o.Type == FuturesOrderType.TakeProfit));
            }

            if (hasTP)
                return true;

            decimal tp = pos.PositionSide == PositionSide.Long
                ? pos.EntryPrice * 1.02m
                : pos.EntryPrice * 0.98m;

            tp = Math.Round(tp / tick) * tick;

            try
            {
                // Algo Order API
                var res = await client.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
                    symbol: symbol,
                    side: sideClose,
                    type: ConditionalOrderType.TakeProfitMarket,
                    quantity: Math.Abs(pos.Quantity),
                    triggerPrice: tp,
                    positionSide: pos.PositionSide,
                    workingType: WorkingType.Mark,
                    timeInForce: TimeInForce.GoodTillCanceled);

                if (!res.Success)
                {
                    _logger.LogError(
                        "[SUPERVISOR] ERROR Ensure TP create {symbol}: {err}",
                        symbol, res.Error);
                    return false;
                }

                _logger.LogWarning("[SUPERVISOR][{symbol}] MANUAL TP created @ {tp}", symbol, tp);
                return false; // TP только что восстановили
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SUPERVISOR] EX Ensure TP create {symbol}", symbol);
                return false;
            }
        }

        // =====================================================================
        // MAIN ENTRY
        // =====================================================================
        public async Task SuperviseAsync(string symbol, TradeSignal? lastSignal, CancellationToken ct)
        {
            using var client = _factory.CreateRestClient();

            // 0) Resolve ownership signal (bot vs pure manual)
            // Binance не имеет MagicNumber — ownership = registry / memory / non-manual signal
            if (lastSignal == null)
            {
                var mem = _signalMemory.GetLastSignal(symbol);
                if (mem != null && !mem.IsManual)
                    lastSignal = mem;
            }

            // Pure manual detection ONLY for logging — НЕ управляем ею
            if (lastSignal == null && !_managed.IsManagedAny(symbol))
            {
                var manualSignal = await _manualHandler.DetectManualAsync(client, symbol, ct);
                if (manualSignal != null)
                {
                    // IsManual=true → supervisor пропустит restore/trail
                    lastSignal = manualSignal;
                    _logger.LogInformation(
                        "[SUPERVISOR][{symbol}] pure MANUAL position — bot will NOT manage SL/TP/trail",
                        symbol);
                }
            }

            // 1) Load positions with retry
            var posInfo = await GetPositionsWithRetryAsync(client, symbol, ct);
            if (posInfo == null || !posInfo.Success || posInfo.Data == null)
            {
                _logger.LogWarning("[SUPERVISOR] No positions {symbol}", symbol);
                return;
            }

            var longPos = posInfo.Data.FirstOrDefault(p => p.PositionSide == PositionSide.Long);
            var shortPos = posInfo.Data.FirstOrDefault(p => p.PositionSide == PositionSide.Short);

            var hasLong = longPos != null && longPos.Quantity != 0m;
            var hasShort = shortPos != null && shortPos.Quantity != 0m;

            if (!hasLong && !hasShort)
            {
                _logger.LogInformation("[SUPERVISOR] {symbol}: no positions — clear residual + unregister managed", symbol);
                await CancelAllOrdersForSymbolAsync(client, symbol, PositionSide.Long, ct);
                await CancelAllOrdersForSymbolAsync(client, symbol, PositionSide.Short, ct);
                _managed.UnregisterAll(symbol);
                _signalMemory.Clear(symbol);
                return;
            }

            // 1b) SMART RESTORE только для BOT-managed позиций (не manual)
            var filters0 = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            var tick0 = filters0.tickSize <= 0 ? 0.0001m : filters0.tickSize;

            if (hasLong)
            {
                bool botLong = _managed.IsManaged(symbol, PositionSide.Long) ||
                               (lastSignal != null && !lastSignal.IsManual && lastSignal.Side == SignalSide.Buy);
                if (botLong)
                    await EnsureProtectionForManagedAsync(client, symbol, longPos!, PositionSide.Long, lastSignal, tick0, ct);
                else
                    _logger.LogDebug("[SUPERVISOR][{symbol}] LONG is not bot-managed — skip ensure", symbol);
            }

            if (hasShort)
            {
                bool botShort = _managed.IsManaged(symbol, PositionSide.Short) ||
                                (lastSignal != null && !lastSignal.IsManual && lastSignal.Side == SignalSide.Sell);
                if (botShort)
                    await EnsureProtectionForManagedAsync(client, symbol, shortPos!, PositionSide.Short, lastSignal, tick0, ct);
                else
                    _logger.LogDebug("[SUPERVISOR][{symbol}] SHORT is not bot-managed — skip ensure", symbol);
            }

            // 2) Load open orders
            var openOrders = await LoadOrdersAsync(client, symbol);

            // 3) Load market regime + klines
            IReadOnlyList<BinanceFuturesUsdtKline>? klines1m = null;

            try
            {
                klines1m = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 160);
                var rr = _regime.DetectRegime(symbol, KlineInterval.OneMinute, klines1m);
                if (rr != null)
                    _regimeNow = rr.Regime;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SUPERVISOR] Klines load error {symbol}", symbol);
            }

            // 4) Handle both sides independently
            if (hasLong)
            {
                await HandleSideAsync(
                    client, symbol, PositionSide.Long, longPos!,
                    openOrders, lastSignal, klines1m, ct);
            }

            if (hasShort)
            {
                await HandleSideAsync(
                    client, symbol, PositionSide.Short, shortPos!,
                    openOrders, lastSignal, klines1m, ct);
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
            const int maxAttempts = 10;
            var delay = TimeSpan.FromMilliseconds(200);

            WebCallResult<BinancePositionDetailsUsdt[]> last = null!;

            for (int i = 0; i < maxAttempts; i++)
            {
                ct.ThrowIfCancellationRequested();

                var res = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, null, ct);
                last = res;

                if (res.Success && res.Data != null)
                {
                    var any = res.Data.Any(x => x.Quantity != 0m);
                    if (any)
                        return res;
                }

                await Task.Delay(delay, ct);
            }
            return last;
        }

        private async Task<List<BinanceUsdFuturesOrder>> LoadOrdersAsync(
            BinanceRestClient client,
            string symbol)
        {
            var res = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol);
            return res.Success && res.Data != null ? res.Data.ToList() : new List<BinanceUsdFuturesOrder>();
        }

        // =====================================================================
        // HANDLE SIDE
        // =====================================================================

        // =====================================================================
        // CANCEL ALL orders (regular + algo) for symbol after full position close
        // =====================================================================
        private async Task CancelAllOrdersForSymbolAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            CancellationToken ct)
        {
            try
            {
                var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

                // 1) Regular open orders
                var openRes = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct);
                if (openRes.Success && openRes.Data != null)
                {
                    foreach (var o in openRes.Data)
                    {
                        // Cancel protective orders for this side (or Both)
                        bool matchSide = o.PositionSide == side || o.PositionSide == PositionSide.Both;
                        bool isProtective =
                            matchSide &&
                            (
                                o.Side == closeSide ||
                                o.Type == FuturesOrderType.StopMarket ||
                                o.Type == FuturesOrderType.Stop ||
                                o.Type == FuturesOrderType.TakeProfitMarket ||
                                o.Type == FuturesOrderType.TakeProfit ||
                                (o.Type == FuturesOrderType.Limit && o.ReduceOnly == true)
                            );

                        if (!isProtective && matchSide == false)
                            continue;

                        // Be aggressive: if same symbol and position fully closed — cancel all residual reduce/conditional
                        try
                        {
                            var c = await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, o.Id, ct: ct);
                            if (c.Success)
                                _logger.LogInformation("[SUPERVISOR][{symbol}] CLOSED→CLEARED order id={id} type={t}", symbol, o.Id, o.Type);
                            else
                                _logger.LogWarning("[SUPERVISOR][{symbol}] CLOSED→CLEAR fail id={id}: {err}", symbol, o.Id, c.Error);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[SUPERVISOR][{symbol}] CLOSED→CLEAR exception id={id}", symbol, o.Id);
                        }
                    }
                }

                // 2) Algo / conditional orders
                try
                {
                    var algoRes = await client.UsdFuturesApi.Trading.GetOpenConditionalOrdersAsync(symbol: symbol, ct: ct);
                    if (algoRes.Success && algoRes.Data != null)
                    {
                        foreach (var a in algoRes.Data)
                        {
                            bool matchSide = a.PositionSide == side || a.PositionSide == PositionSide.Both;
                            if (!matchSide) continue;

                            try
                            {
                                var c = await client.UsdFuturesApi.Trading.CancelConditionalOrderAsync(orderId: a.Id, ct: ct);
                                if (c.Success)
                                    _logger.LogInformation("[SUPERVISOR][{symbol}] CLOSED→CLEARED ALGO id={id} type={t}", symbol, a.Id, a.Type);
                                else
                                {
                                    try { await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, a.Id, ct: ct); } catch { }
                                    _logger.LogWarning("[SUPERVISOR][{symbol}] CLOSED→CLEAR algo fail id={id}: {err}", symbol, a.Id, c.Error);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "[SUPERVISOR][{symbol}] CLOSED→CLEAR algo exception id={id}", symbol, a.Id);
                            }
                        }
                    }
                }
                catch (Exception exAlgo)
                {
                    _logger.LogWarning(exAlgo, "[SUPERVISOR][{symbol}] GetOpenConditionalOrders on close failed", symbol);
                }

                _logger.LogInformation("[SUPERVISOR][{symbol}] {side} FULL CLOSE → all protective orders cleaned", symbol, side);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SUPERVISOR][{symbol}] CancelAllOrdersForSymbol failed", symbol);
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
            decimal qty = Math.Abs(pos.Quantity);

            // ---------- CLOSE DETECTOR v7.1 ----------
            var key = $"{symbol}_{side}";

            var prevQty = _manualHandler.GetPrevQty(key);
            var prevEntry = _manualHandler.GetPrevEntry(key);

            _manualHandler.SetPrevState(key, pos.Quantity, pos.EntryPrice);

            if (prevQty != 0 && pos.Quantity == 0)
            {
                decimal exitPrice = pos.MarkPrice > 0 ? pos.MarkPrice : pos.EntryPrice;
                if (exitPrice <= 0) exitPrice = prevEntry;

                var sigSide = side == PositionSide.Long
                    ? SignalSide.Buy
                    : SignalSide.Sell;

                decimal absQty = Math.Abs(prevQty);
                // LONG: profit if exit > entry; SHORT: profit if exit < entry
                decimal pnl = side == PositionSide.Long
                    ? (exitPrice - prevEntry) * absQty
                    : (prevEntry - exitPrice) * absQty;
                decimal notional = prevEntry * absQty;
                decimal roi = notional > 0 ? pnl / notional * 100m : 0m;

                var closeStatus = pnl >= 0
                    ? TradeExecutionStatus.PositionClosedTp
                    : TradeExecutionStatus.PositionClosedSl;

                try
                {
                    _executed.UpdateStatus(
                        symbol,
                        DateTime.UtcNow,
                        closeStatus,
                        qty: absQty,
                        notional: notional,
                        exitPrice: exitPrice,
                        pnl: pnl,
                        roi: roi);
                    _logger.LogInformation(
                        "[EXEC][{symbol}] CLOSED → status={st} pnl={pnl:F4} roi={roi:F2}% entry={entry} exit={exit}",
                        symbol, closeStatus, pnl, roi, prevEntry, exitPrice);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[EXEC][{symbol}] failed to write close PnL", symbol);
                }

                _aiLearning.RecordTrade(
                    symbol,
                    sigSide,
                    entry: prevEntry,
                    exit: exitPrice,
                    regime: _regimeNow
                );

                _logger.LogWarning(
                    "[AI][{symbol}] POSITION CLOSED → saved to ai_learning.json | entry={entry} exit={exit} pnl={pnl:F4}",
                    symbol, prevEntry, exitPrice, pnl
                );

                // CRITICAL: after full close — clear orders + ownership
                await CancelAllOrdersForSymbolAsync(client, symbol, side, ct);
                _managed.Unregister(symbol, side);
                if (!_managed.IsManagedAny(symbol))
                    _signalMemory.Clear(symbol);

                return;
            }

            if (qty <= 0)
            {
                _logger.LogInformation("[SUPERVISOR] {symbol} {side}: no qty", symbol, side);
                return;
            }

            // Pure manual / not owned by bot → do not trail, do not emergency-create
            bool isBotManaged =
                _managed.IsManaged(symbol, side) ||
                (signal != null && !signal.IsManual);

            if (!isBotManaged)
            {
                _logger.LogInformation(
                    "[SUPERVISOR][{symbol}] {side} MANUAL/external — ignore (no SL/TP restore, no trail)",
                    symbol, side);
                return;
            }

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
            var orders = allOrders
                .Where(o => o.PositionSide == side || o.PositionSide == PositionSide.Both)
                .ToList();

            var sl = orders.FirstOrDefault(o =>
                o.Side == closeSide &&
                (o.Type == FuturesOrderType.StopMarket || o.Type == FuturesOrderType.Stop));

            var tp = orders.FirstOrDefault(o =>
                o.Side == closeSide &&
                (
                    o.Type == FuturesOrderType.TakeProfitMarket ||
                    o.Type == FuturesOrderType.TakeProfit ||
                    (o.Type == FuturesOrderType.Limit && o.ReduceOnly == true)
                ));

            // Algo conditional orders (обязательны после -4120 миграции)
            if (sl == null || tp == null)
            {
                var algoRes = await client.UsdFuturesApi.Trading.GetOpenConditionalOrdersAsync(symbol: symbol, ct: ct);
                if (algoRes.Success && algoRes.Data != null)
                {
                    if (sl == null)
                    {
                        var a = algoRes.Data.FirstOrDefault(o =>
                            o.Side == closeSide &&
                            (o.Type == FuturesOrderType.StopMarket || o.Type == FuturesOrderType.Stop) &&
                            (o.PositionSide == side || o.PositionSide == PositionSide.Both));
                        if (a != null)
                        {
                            sl = new BinanceUsdFuturesOrder
                            {
                                Id = a.Id,
                                Side = a.Side,
                                Type = a.Type,
                                StopPrice = a.TriggerPrice,
                                Price = a.Price,
                                Quantity = a.Quantity,
                                PositionSide = a.PositionSide
                            };
                        }
                    }
                    if (tp == null)
                    {
                        var a = algoRes.Data.FirstOrDefault(o =>
                            o.Side == closeSide &&
                            (o.Type == FuturesOrderType.TakeProfitMarket || o.Type == FuturesOrderType.TakeProfit) &&
                            (o.PositionSide == side || o.PositionSide == PositionSide.Both));
                        if (a != null)
                        {
                            tp = new BinanceUsdFuturesOrder
                            {
                                Id = a.Id,
                                Side = a.Side,
                                Type = a.Type,
                                StopPrice = a.TriggerPrice,
                                Price = a.Price,
                                Quantity = a.Quantity,
                                PositionSide = a.PositionSide
                            };
                        }
                    }
                }
            }

            decimal entry = pos.EntryPrice;
            if (entry <= 0 && signal != null && signal.Symbol == symbol)
                entry = signal.EntryPrice;

            // 1) Missing SL
            if (sl == null)
            {
                await CreateEmergencySLAsync(client, symbol, side, qty, entry, signal, ct);
                _logger.LogWarning("[SUPERVISOR][{symbol}] SL restored", symbol);
                return;
            }

            // 2) Missing TP
            if (tp == null)
            {
                await CreateEmergencyTPAsync(client, symbol, side, qty, entry, signal, ct);
                _logger.LogWarning("[SUPERVISOR][{symbol}] TP restored", symbol);
                return;
            }

            // 3) HYBRID scale-out (дек-2025 логика поверх биржевых SL/TP)
            //    EARLY 35% @ +0.9 ATR → BE @ +1.2 ATR → HARVEST кусками
            if (klines != null && klines.Count >= 30)
            {
                await TryHybridEarlyPartialAsync(client, symbol, side, qty, entry, pos, klines, ct);
                await TryHybridBreakEvenAsync(client, symbol, side, qty, entry, sl, signal, klines, ct);
                await TryHybridHarvestAsync(client, symbol, side, qty, entry, pos, klines, ct);
            }

            // 4) Trailing + Runner (как было)
            if (klines != null && klines.Count >= 50)
            {
                await ManageRunnerTpAsync(
                    client, symbol, side, qty, entry,
                    orders, signal, klines, ct);

                await ManageRunnerTpExtensionAsync(
                    client, symbol, side, qty, entry,
                    signal, orders, klines, ct);

                await MultiLayerTrailingAsync(
                    client, symbol, side, qty, entry,
                    signal, orders, klines, ct);
            }
        }

        // =====================================================================
        // EMERGENCY SL  (STOP_MARKET)
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
            if (qty <= 0) return;

            decimal rawSl;

            if (signal != null && signal.StopLoss > 0)
                rawSl = signal.StopLoss;
            else
            {
                try
                {
                    var kl = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 100);
                    if (kl.Count < 30) return;

                    var atr = _marketData.CalculateAtr(kl, 14);
                    if (atr <= 0) return;

                    var atrMultiplier = (atr > 0.0025m) ? 1.5m : 1.2m;

                    rawSl = side == PositionSide.Long
                        ? entryPrice - atr * atrMultiplier
                        : entryPrice + atr * atrMultiplier;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[SUPERVISOR] SL ATR calc failed {symbol}", symbol);
                    return;
                }
            }

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            var tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            decimal sl = Math.Round(rawSl / tick) * tick;
            decimal mark = await GetMarkPriceSafeAsync(client, symbol, entryPrice, ct);

            if (mark > 0)
            {
                if (side == PositionSide.Long && sl >= mark)
                    sl = mark - tick;

                if (side == PositionSide.Short && sl <= mark)
                    sl = mark + tick;
            }

            try
            {
                var res = await client.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
                    symbol: symbol,
                    side: side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                    type: ConditionalOrderType.StopMarket,
                    quantity: qty,
                    triggerPrice: sl,
                    positionSide: side,
                    workingType: WorkingType.Mark,
                    timeInForce: TimeInForce.GoodTillCanceled,
                    ct: ct);

                if (!res.Success)
                {
                    _logger.LogError("[SUPERVISOR] ERROR SL create {symbol}: {err}", symbol, res.Error);
                    return;
                }

                _logger.LogInformation("[SUPERVISOR] SL CREATED {symbol} sl={sl}", symbol, sl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SUPERVISOR] EX SL create {symbol}", symbol);
            }
        }

        // =====================================================================
        // EMERGENCY TP (TAKE_PROFIT_MARKET)
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
            if (qty <= 0) return;

            decimal trigger;

            if (signal != null && signal.TakeProfits != null && signal.TakeProfits.Count > 0)
                trigger = signal.TakeProfits[0];
            else
            {
                try
                {
                    var kl = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 100);
                    if (kl.Count < 30) return;

                    var atr = _marketData.CalculateAtr(kl, 14);
                    if (atr <= 0) return;

                    trigger = side == PositionSide.Long
                        ? entryPrice + atr * 1.5m
                        : entryPrice - atr * 1.5m;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[SUPERVISOR] TP ATR calc failed {symbol}", symbol);
                    return;
                }
            }

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            var tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            trigger = Math.Round(trigger / tick) * tick;

            // --- VALIDATE TP AGAINST ENTRY ---
            if (side == PositionSide.Long)
            {
                if (trigger <= entryPrice)
                    trigger = entryPrice + tick * 3;
            }
            else
            {
                if (trigger >= entryPrice)
                    trigger = entryPrice - tick * 3;
            }

            try
            {
                var res = await client.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
                    symbol: symbol,
                    side: side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                    type: ConditionalOrderType.TakeProfitMarket,
                    quantity: qty,
                    triggerPrice: trigger,
                    positionSide: side,
                    workingType: WorkingType.Mark,
                    timeInForce: TimeInForce.GoodTillCanceled,
                    ct: ct);

                if (!res.Success)
                {
                    _logger.LogError("[SUPERVISOR] ERROR create TP {symbol}: {err}", symbol, res.Error);
                    return;
                }

                _logger.LogInformation("[SUPERVISOR] TP CREATED {symbol} tp={tp}", symbol, trigger);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SUPERVISOR] EX create TP {symbol}", symbol);
            }
        }

        // =====================================================================
        // RUNNER MODE — TP EXTENSION (Dynamic TP2 for runner 30%)
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
            if (signal == null || qty <= 0 || klines == null || klines.Count < 60)
                return;

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var runnerTp = orders.FirstOrDefault(o =>
                o.Side == closeSide &&
                o.Type == FuturesOrderType.Limit &&
                o.ReduceOnly == true &&
                o.Quantity < qty * 0.50m);

            if (runnerTp == null)
                return;

            decimal qtyRunner = runnerTp.Quantity;

            var last = klines[^1];
            decimal lastPrice = last.ClosePrice;
            decimal atr = CalculateAtr(klines);
            if (atr <= 0)
                return;

            var exhaustion = DetectExhaustionLevel(side, entryPrice, atr, klines);

            if (exhaustion == ExhaustionLevel.Strong)
            {
                _logger.LogInformation(
                    "[RUNNER-EXT][{symbol}] Exhaustion STRONG → stop extending TP2 (runner stays, SL manages)",
                    symbol);
                return;
            }

            bool sweepInFavor = IsLiquiditySweepInFavor(side, klines);
            bool sweepAgainst = IsLiquiditySweepAgainst(side, klines);

            if (sweepAgainst)
            {
                _logger.LogInformation(
                    "[RUNNER-EXT][{symbol}] Liquidity sweep AGAINST position → skip TP2 extension",
                    symbol);
                return;
            }

            var ema21 = CalculateEma(klines, 21);
            var ema55 = CalculateEma(klines, 55);
            if (ema21 <= 0 || ema55 <= 0)
                return;

            bool emaUp = ema21 > ema55;
            decimal emaSlope = ema21 - ema55;

            var recent = klines.Skip(klines.Count - 25).ToList();
            decimal swingHigh = recent.Max(k => k.HighPrice);
            decimal swingLow = recent.Min(k => k.LowPrice);

            decimal baseTpExt;

            if (side == PositionSide.Long)
            {
                baseTpExt = swingHigh + atr * 1.0m;
            }
            else
            {
                baseTpExt = swingLow - atr * 1.0m;
            }

            if (side == PositionSide.Long && emaUp && emaSlope > 0)
                baseTpExt += emaSlope * 0.5m;
            else if (side == PositionSide.Short && !emaUp && emaSlope < 0)
                baseTpExt += emaSlope * 0.5m;

            decimal tpExt = baseTpExt;

            if (exhaustion == ExhaustionLevel.Mild)
            {
                tpExt = side == PositionSide.Long
                    ? Math.Min(tpExt, lastPrice + atr * 1.2m)
                    : Math.Max(tpExt, lastPrice - atr * 1.2m);
            }

            if (sweepInFavor && exhaustion == ExhaustionLevel.None)
            {
                if (side == PositionSide.Long)
                    tpExt += atr * 0.7m;
                else
                    tpExt -= atr * 0.7m;
            }

            decimal currTp = runnerTp.Price;
            if (currTp <= 0)
                return;

            if (side == PositionSide.Long && tpExt <= currTp)
                return;
            if (side == PositionSide.Short && tpExt >= currTp)
                return;

            await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, runnerTp.Id, ct: ct);

            var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol,
                closeSide,
                FuturesOrderType.Limit,
                qtyRunner,
                price: tpExt,
                positionSide: side,
                reduceOnly: true,
                timeInForce: TimeInForce.GoodTillCanceled,
                ct: ct);

            if (signal.Meta == null)
                signal.Meta = new AiLearningTradeMeta();

            signal.Meta.Tp2Extensions.Add(tpExt);
            signal.Meta.ExhaustionDetected = exhaustion != ExhaustionLevel.None;
            signal.Meta.ExhaustionLevel = exhaustion.ToString();
            signal.Meta.SweepInFavor = sweepInFavor;
            signal.Meta.SweepAgainst = sweepAgainst;

            if (!res.Success)
            {
                _logger.LogError("[RUNNER-EXT][{symbol}] ERROR updating TP2: {err}", symbol, res.Error);
                return;
            }

            _logger.LogInformation(
                "[RUNNER-EXT][{symbol}] Runner TP EXTENDED {old} → {tpExt} (exh={exh}, sweepFavor={sf})",
                symbol, currTp, tpExt, exhaustion, sweepInFavor);
        }

        // =====================================================================
        // RUNNER MODE — TP1 (70%) FIX + RUNNER 30%
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
            if (signal == null || qty <= 0 || klines == null || klines.Count < 50)
                return;

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var tpOrder = orders.FirstOrDefault(o =>
                o.Side == closeSide &&
                (o.Type == FuturesOrderType.TakeProfitMarket ||
                 o.Type == FuturesOrderType.TakeProfit ||
                 (o.Type == FuturesOrderType.Limit && o.ReduceOnly == true)));

            if (tpOrder == null)
                return;

            if (tpOrder.Quantity < qty * 0.99m)
                return;

            decimal qtyTp1 = Math.Round(qty * 0.70m, 8);
            decimal qtyRunner = qty - qtyTp1;

            if (qtyTp1 <= 0 || qtyRunner <= 0)
                return;

            decimal tp1Price = tpOrder.Price > 0 ? tpOrder.Price : (tpOrder.StopPrice ?? 0m);
            if (tp1Price <= 0)
                return;

            await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, tpOrder.Id, ct: ct);

            var resTp1 = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol,
                closeSide,
                FuturesOrderType.Limit,
                quantity: qtyTp1,
                price: tp1Price,
                positionSide: side,
                reduceOnly: true,
                timeInForce: TimeInForce.GoodTillCanceled,
                ct: ct);

            signal.Meta ??= new AiLearningTradeMeta
            {
                RunnerQty = qtyRunner,
                Tp1Price = tp1Price,
                Tp2Start = tp1Price
            };

            if (!resTp1.Success)
            {
                _logger.LogError("[RUNNER][{symbol}] ERROR creating TP1: {err}", symbol, resTp1.Error);
                return;
            }

            _logger.LogInformation("[RUNNER][{symbol}] TP1 CREATED qty={q} price={p}", symbol, qtyTp1, tp1Price);
            _logger.LogInformation("[RUNNER][{symbol}] Runner ACTIVE qty={q} — managed by trailing SL", symbol, qtyRunner);
        }

        // =====================================================================
        // MULTI-LAYER TRAILING + Dynamic Trend Hold
        // =====================================================================
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
            if (qty <= 0 || signal == null)
                return;

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var slOrder = orders.FirstOrDefault(o =>
                o.Side == closeSide &&
                (o.Type == FuturesOrderType.StopMarket || o.Type == FuturesOrderType.Stop));

            // После миграции Binance SL живёт в Algo Orders — подтягиваем, если нет в обычных
            if (slOrder == null)
            {
                var algoRes = await client.UsdFuturesApi.Trading.GetOpenConditionalOrdersAsync(symbol: symbol, ct: ct);
                if (algoRes.Success && algoRes.Data != null)
                {
                    var algoSl = algoRes.Data.FirstOrDefault(o =>
                        o.Side == closeSide &&
                        (o.Type == FuturesOrderType.StopMarket || o.Type == FuturesOrderType.Stop));
                    if (algoSl != null)
                    {
                        // Синтетический ордер для UpdateSLAsync (Id = algoId, StopPrice = trigger)
                        slOrder = new BinanceUsdFuturesOrder
                        {
                            Id = algoSl.Id,
                            Side = algoSl.Side,
                            Type = algoSl.Type,
                            StopPrice = algoSl.TriggerPrice,
                            Price = algoSl.Price,
                            Quantity = algoSl.Quantity,
                            PositionSide = algoSl.PositionSide
                        };
                    }
                }
            }

            if (slOrder == null)
            {
                _logger.LogWarning("[SUPERVISOR] {symbol} {side}: TRAILING skipped — SL not found", symbol, side);
                return;
            }

            var last = klines.Last();
            // Mark price предпочтительнее close — меньше ложных BE на wick
            decimal mark = last.ClosePrice;
            try
            {
                var mp = await GetMarkPriceSafeAsync(client, symbol, last.ClosePrice, ct);
                if (mp > 0) mark = mp;
            }
            catch { /* keep close */ }

            PositionSide realSide = side;
            if (side == PositionSide.Both)
                realSide = mark >= entryPrice ? PositionSide.Long : PositionSide.Short;

            decimal atr = CalculateAtr(klines);
            if (atr <= 0) atr = entryPrice * 0.005m; // fallback 0.5%

            decimal currentSl = slOrder.StopPrice;
            if (currentSl <= 0) currentSl = slOrder.Price;

            // =============================================================
            // PRO TRAIL — не micro-trail к цене (главная причина серии SL)
            // Старое: Max(mark-0.9ATR, ema-0.3ATR, microLow) → SL прилипал к цене.
            // Новое:
            //  1) trail только после profit ≥ 2.0 ATR
            //  2) Chandelier extreme(12) ± 2.2 ATR (за ликвидностью)
            //  3) Structure swing(15) ± 0.5 ATR
            //  4) floor: SL не ближе 1.8 ATR к mark
            //  5) шаг ≥ 0.30 ATR
            // Ранний BE здесь ОТКЛЮЧЁН (делает Hybrid @ +1.2 ATR + 0.35 buf)
            // =============================================================

            decimal profitAtr = atr > 0 ? Math.Abs(mark - entryPrice) / atr : 0m;
            bool longSide = realSide == PositionSide.Long;

            var contLevel = EvaluateTrendContinuation(realSide, entryPrice, atr, klines);
            if (contLevel == TrendContinuationLevel.High && profitAtr < 3.0m)
            {
                _logger.LogInformation(
                    "[SUPERVISOR] {symbol} {side}: HIGH trend + profit {p:F2}ATR < 3 → trail HOLD",
                    symbol, realSide, profitAtr);
                return;
            }

            if (profitAtr < 2.0m)
                return;

            int n = klines.Count;
            int from12 = Math.Max(0, n - 12);
            int from15 = Math.Max(0, n - 15);

            decimal swingHigh12 = decimal.MinValue, swingLow12 = decimal.MaxValue;
            for (int i2 = from12; i2 < n; i2++)
            {
                if (klines[i2].HighPrice > swingHigh12) swingHigh12 = klines[i2].HighPrice;
                if (klines[i2].LowPrice < swingLow12) swingLow12 = klines[i2].LowPrice;
            }
            decimal swingHigh15 = decimal.MinValue, swingLow15 = decimal.MaxValue;
            for (int i2 = from15; i2 < n; i2++)
            {
                if (klines[i2].HighPrice > swingHigh15) swingHigh15 = klines[i2].HighPrice;
                if (klines[i2].LowPrice < swingLow15) swingLow15 = klines[i2].LowPrice;
            }

            decimal chandelier = longSide
                ? swingHigh12 - atr * 2.2m
                : swingLow12 + atr * 2.2m;

            decimal structure = longSide
                ? swingLow15 - atr * 0.50m
                : swingHigh15 + atr * 0.50m;

            decimal targetSl = longSide
                ? Math.Min(chandelier, structure)
                : Math.Max(chandelier, structure);

            decimal minDist = atr * 1.8m;
            if (longSide && mark - targetSl < minDist)
                targetSl = mark - minDist;
            if (!longSide && targetSl - mark < minDist)
                targetSl = mark + minDist;

            if (profitAtr >= 1.2m && entryPrice > 0)
            {
                if (longSide)
                    targetSl = Math.Max(targetSl, entryPrice + atr * 0.10m);
                else
                    targetSl = Math.Min(targetSl, entryPrice - atr * 0.10m);
            }

            if (longSide)
            {
                if (targetSl <= currentSl) return;
                if (currentSl > 0 && targetSl - currentSl < atr * 0.30m) return;
            }
            else
            {
                if (currentSl > 0 && targetSl >= currentSl) return;
                if (currentSl > 0 && currentSl - targetSl < atr * 0.30m) return;
            }

            _logger.LogInformation(
                "[SUPERVISOR][{symbol}][{side}] PRO-TRAIL SL {old} → {neu} (profit={p:F2}ATR, minDist=1.8ATR)",
                symbol, realSide, currentSl, targetSl, profitAtr);

            await UpdateSLAsync(
                client, symbol, realSide, qty,
                slOrder, entryPrice, targetSl,
                signal, ct);
        }

        private TrendContinuationLevel EvaluateTrendContinuation(
            PositionSide side,
            decimal entryPrice,
            decimal atr,
            IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines.Count < 30 || atr <= 0)
                return TrendContinuationLevel.Medium;

            var last = klines[^1];

            int lookback = Math.Min(20, klines.Count - 1);
            var past = klines[^lookback];

            if (past.ClosePrice <= 0)
                return TrendContinuationLevel.Medium;

            var movePct = (last.ClosePrice - past.ClosePrice) / past.ClosePrice;
            var rr = Math.Abs(last.ClosePrice - entryPrice) / atr;

            if (side == PositionSide.Long)
            {
                if (rr >= 1.5m && movePct >= 0.015m)
                    return TrendContinuationLevel.High;

                if (rr >= 0.8m && movePct >= 0.0075m)
                    return TrendContinuationLevel.Medium;

                return TrendContinuationLevel.Low;
            }
            else
            {
                if (rr >= 1.5m && movePct <= -0.015m)
                    return TrendContinuationLevel.High;

                if (rr >= 0.8m && movePct <= -0.0075m)
                    return TrendContinuationLevel.Medium;

                return TrendContinuationLevel.Low;
            }
        }

        // =====================================================================
        // EXHAUSTION DETECTOR
        // =====================================================================
        private ExhaustionLevel DetectExhaustionLevel(
            PositionSide side,
            decimal entryPrice,
            decimal atr,
            IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines.Count < 40 || atr <= 0)
                return ExhaustionLevel.None;

            var last = klines[^1];

            var ema21 = CalculateEma(klines, 21);
            if (ema21 <= 0)
                return ExhaustionLevel.None;

            var distanceFromEma = Math.Abs(last.ClosePrice - ema21) / atr;

            var rr = Math.Abs(last.ClosePrice - entryPrice) / atr;

            var body = Math.Abs(last.ClosePrice - last.OpenPrice);
            var upperWick = last.HighPrice - Math.Max(last.ClosePrice, last.OpenPrice);
            var lowerWick = Math.Min(last.ClosePrice, last.OpenPrice) - last.LowPrice;

            bool longCase = side == PositionSide.Long;
            bool bigWickAgainst =
                longCase
                    ? upperWick > body * 2m && upperWick > atr * 0.7m
                    : lowerWick > body * 2m && lowerWick > atr * 0.7m;

            if (rr >= 3.0m && distanceFromEma >= 2.5m && bigWickAgainst)
                return ExhaustionLevel.Strong;

            if (rr >= 2.0m && distanceFromEma >= 1.8m)
                return ExhaustionLevel.Mild;

            return ExhaustionLevel.None;
        }

        // =====================================================================
        // LIQUIDITY SWEEP DETECTION
        // =====================================================================
        private bool IsLiquiditySweepInFavor(
            PositionSide side,
            IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines.Count < 25)
                return false;

            var last = klines[^1];
            var range = klines.Skip(klines.Count - 25).Take(24).ToList();

            decimal prevHigh = range.Max(k => k.HighPrice);
            decimal prevLow = range.Min(k => k.LowPrice);

            var body = Math.Abs(last.ClosePrice - last.OpenPrice);
            var upperWick = last.HighPrice - Math.Max(last.ClosePrice, last.OpenPrice);
            var lowerWick = Math.Min(last.ClosePrice, last.OpenPrice) - last.LowPrice;

            if (side == PositionSide.Long)
            {
                bool tookHigh = last.HighPrice > prevHigh * 1.0005m;
                bool closeBelowHigh = last.ClosePrice < prevHigh;
                return tookHigh && closeBelowHigh && upperWick > body * 1.5m;
            }
            else
            {
                bool tookLow = last.LowPrice < prevLow * 0.9995m;
                bool closeAboveLow = last.ClosePrice > prevLow;
                return tookLow && closeAboveLow && lowerWick > body * 1.5m;
            }
        }

        private bool IsLiquiditySweepAgainst(
            PositionSide side,
            IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines.Count < 25)
                return false;

            var last = klines[^1];
            var range = klines.Skip(klines.Count - 25).Take(24).ToList();

            decimal prevHigh = range.Max(k => k.HighPrice);
            decimal prevLow = range.Min(k => k.LowPrice);

            var body = Math.Abs(last.ClosePrice - last.OpenPrice);
            var upperWick = last.HighPrice - Math.Max(last.ClosePrice, last.OpenPrice);
            var lowerWick = Math.Min(last.ClosePrice, last.OpenPrice) - last.LowPrice;

            if (side == PositionSide.Long)
            {
                bool tookLow = last.LowPrice < prevLow * 0.9995m;
                bool closeAboveLow = last.ClosePrice > prevLow;
                return tookLow && closeAboveLow && lowerWick > body * 1.5m;
            }
            else
            {
                bool tookHigh = last.HighPrice > prevHigh * 1.0005m;
                bool closeBelowHigh = last.ClosePrice < prevHigh;
                return tookHigh && closeBelowHigh && upperWick > body * 1.5m;
            }
        }

        private decimal CalculateAtr(IReadOnlyList<BinanceFuturesUsdtKline> kl)
        {
            if (kl.Count < 16) return 0;

            decimal atr = 0;
            int start = kl.Count - 15;

            for (int i = start; i < kl.Count; i++)
            {
                var c = kl[i];
                var p = kl[i - 1];

                var tr = Math.Max(
                    c.HighPrice - c.LowPrice,
                    Math.Max(
                        Math.Abs(c.HighPrice - p.ClosePrice),
                        Math.Abs(c.LowPrice - p.ClosePrice)));

                atr += tr;
            }

            return atr / 14m;
        }

        private decimal CalculateEma(IReadOnlyList<BinanceFuturesUsdtKline> kl, int period)
        {
            if (kl.Count < period + 1) return 0;

            decimal k = 2m / (period + 1);
            int start = kl.Count - period;

            decimal ema = kl[start].ClosePrice;

            for (int i = start + 1; i < kl.Count; i++)
                ema = kl[i].ClosePrice * k + ema * (1 - k);

            return ema;
        }

        private decimal SuperTrend(IReadOnlyList<BinanceFuturesUsdtKline> kl, decimal atr)
        {
            var last = kl.Last();
            return (last.HighPrice + last.LowPrice) / 2 + atr * 1.5m;
        }

        // =====================================================================
        // UPDATE SL + LEARNING HOOK (STOP_MARKET)
        // =====================================================================
        private async Task UpdateSLAsync(
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
            if (qty <= 0 || newSl <= 0) return;

            decimal oldSl = slOrder.StopPrice ?? slOrder.Price;
            if (oldSl <= 0) return;

            if (side == PositionSide.Long && newSl <= oldSl) return;
            if (side == PositionSide.Short && newSl >= oldSl) return;

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            decimal tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            decimal s = Math.Round(newSl / tick) * tick;
            decimal mark = await GetMarkPriceSafeAsync(client, symbol, entry, ct);

            if (mark > 0)
            {
                if (side == PositionSide.Long && s >= mark)
                    s = mark - tick;

                if (side == PositionSide.Short && s <= mark)
                    s = mark + tick;

                if (side == PositionSide.Long && s >= mark)
                {
                    _logger.LogWarning("[SUPERVISOR] TRAIL SKIP {symbol}: new SL > mark", symbol);
                    return;
                }

                if (side == PositionSide.Short && s <= mark)
                {
                    _logger.LogWarning("[SUPERVISOR] TRAIL SKIP {symbol}: new SL < mark", symbol);
                    return;
                }
            }

            // -2021 guard: не ставим SL, который сразу триггерится
            if (mark > 0)
            {
                if (side == PositionSide.Long && s >= mark - tick)
                {
                    _logger.LogWarning("[SUPERVISOR] TRAIL SKIP {symbol}: SL {sl} too close/above mark {mark}", symbol, s, mark);
                    return;
                }
                if (side == PositionSide.Short && s <= mark + tick)
                {
                    _logger.LogWarning("[SUPERVISOR] TRAIL SKIP {symbol}: SL {sl} too close/below mark {mark}", symbol, s, mark);
                    return;
                }
            }

            // Algo orders cancel via CancelConditionalOrderAsync (algoId)
            var cancelRes = await client.UsdFuturesApi.Trading.CancelConditionalOrderAsync(orderId: slOrder.Id, ct: ct);
            if (!cancelRes.Success)
            {
                // fallback: maybe it was a regular order id
                try { await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, slOrder.Id, ct: ct); } catch { }
            }

            var res = await client.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
                symbol: symbol,
                side: side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                type: ConditionalOrderType.StopMarket,
                quantity: qty,
                triggerPrice: s,
                positionSide: side,
                workingType: WorkingType.Mark,
                timeInForce: TimeInForce.GoodTillCanceled,
                ct: ct);

            if (!res.Success)
            {
                _logger.LogError("[SUPERVISOR] ERROR update SL {symbol}: {err}", symbol, res.Error);
                // если отменили старый SL и новый не встал — попробуем вернуть старый
                try
                {
                    await client.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
                        symbol: symbol,
                        side: side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                        type: ConditionalOrderType.StopMarket,
                        quantity: qty,
                        triggerPrice: oldSl,
                        positionSide: side,
                        workingType: WorkingType.Mark,
                        timeInForce: TimeInForce.GoodTillCanceled,
                        ct: ct);
                }
                catch { }
                return;
            }

            _logger.LogInformation("[SUPERVISOR] TRAIL SL UPDATED {symbol} {old} → {ns}", symbol, oldSl, s);

            if (signal != null && signal.IsManual)
                return;

            var sigSide = side == PositionSide.Short ? SignalSide.Sell : SignalSide.Buy;

            signal ??= new TradeSignal
            {
                Symbol = symbol,
                Side = sigSide,
                EntryPrice = entry,
                Time = DateTime.UtcNow
            };

            signal.Meta ??= new AiLearningTradeMeta();
            signal.Meta.FinalExitPrice = s;
            signal.Meta.ExitReason = "TRAIL_SL";

            _aiLearning.RecordTrade(symbol, sigSide, entry, s, _regimeNow);
        }

        // =====================================================================
        // MARK PRICE SAFE
        // =====================================================================

        // =====================================================================
        // HYBRID scale-out (EARLY → BE → HARVEST) — не отключает биржевые SL/TP
        // =====================================================================

        private static string HybridPosKey(string symbol, PositionSide side, decimal entry)
            => $"{symbol}|{side}|{entry:F8}";

        private async Task TryHybridEarlyPartialAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entry,
            BinancePositionDetailsUsdt pos,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            CancellationToken ct)
        {
            if (qty <= 0 || entry <= 0 || klines.Count < 20)
                return;

            decimal atr = CalculateAtr(klines);
            if (atr <= 0) return;

            decimal mark = pos.MarkPrice > 0 ? pos.MarkPrice : klines[^1].ClosePrice;
            bool reached = side == PositionSide.Long
                ? mark >= entry + atr * 0.90m
                : mark <= entry - atr * 0.90m;
            if (!reached) return;

            var key = HybridPosKey(symbol, side, entry);
            if (_hybridEarlyDone.ContainsKey(key)) return;

            decimal closeQty = Math.Round(qty * 0.35m, 8);
            try
            {
                var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
                var step = filters.step > 0 ? filters.step : 0.001m;
                closeQty = Math.Floor(closeQty / step) * step;
                if (closeQty < filters.minQty) return;
                if (closeQty >= qty) closeQty = Math.Floor((qty * 0.35m) / step) * step;
                if (closeQty <= 0 || closeQty >= qty) return;
            }
            catch { /* best effort */ }

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
                _logger.LogWarning("[HYBRID-EARLY][{symbol}][{side}] FAIL: {err}", symbol, side, res.Error);
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _hybridEarlyDone[key] = now;
            _hybridRecentPartial[$"{symbol}|{side}"] = now;

            _logger.LogInformation(
                "[HYBRID-EARLY][{symbol}][{side}] +0.9ATR partial {closed}/{total} mark={mark} entry={entry}",
                symbol, side, closeQty, qty, mark, entry);
        }

        private async Task TryHybridBreakEvenAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entry,
            BinanceUsdFuturesOrder? slOrder,
            TradeSignal? signal,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            CancellationToken ct)
        {
            if (qty <= 0 || entry <= 0 || klines.Count < 20)
                return;

            decimal atr = CalculateAtr(klines);
            if (atr <= 0) return;

            decimal mark = await GetMarkPriceSafeAsync(client, symbol, entry, ct);
            bool reached = side == PositionSide.Long
                ? mark >= entry + atr * 1.20m
                : mark <= entry - atr * 1.20m;
            if (!reached) return;

            var key = HybridPosKey(symbol, side, entry);
            if (_hybridBeDone.ContainsKey(key)) return;

            // BE + буфер 0.35 ATR (комиссии + noise; 0.15 было слишком тесно)
            decimal beSl = side == PositionSide.Long
                ? entry + atr * 0.35m
                : entry - atr * 0.35m;

            decimal currentSl = 0m;
            if (slOrder != null)
            {
                decimal sp = slOrder.StopPrice;
                decimal px = slOrder.Price;
                currentSl = sp > 0 ? sp : px;
            }

            // Только улучшаем SL (не ослабляем)
            if (side == PositionSide.Long && currentSl > 0 && currentSl >= beSl)
            {
                _hybridBeDone[key] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return;
            }
            if (side == PositionSide.Short && currentSl > 0 && currentSl <= beSl)
            {
                _hybridBeDone[key] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return;
            }

            await UpdateSLAsync(client, symbol, side, qty, slOrder, entry, beSl, signal, ct);
            _hybridBeDone[key] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _logger.LogInformation(
                "[HYBRID-BE][{symbol}][{side}] SL → BE+buf {be} (entry={entry}, mark={mark}, +1.2ATR)",
                symbol, side, beSl, entry, mark);
        }

        private async Task TryHybridHarvestAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entry,
            BinancePositionDetailsUsdt pos,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            CancellationToken ct)
        {
            if (qty <= 0 || entry <= 0)
                return;

            // Не harvest сразу после EARLY (sync lag)
            if (_hybridRecentPartial.TryGetValue($"{symbol}|{side}", out var ts))
            {
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ts < 8000)
                    return;
            }

            if (_hybridLastHarvest.TryGetValue($"{symbol}|{side}", out var lastH)
                && DateTime.UtcNow - lastH < TimeSpan.FromMinutes(6))
                return;

            // PnL from mark (portable across Binance.Net property names)
            decimal mark0 = pos.MarkPrice > 0 ? pos.MarkPrice : entry;
            decimal uPnl = side == PositionSide.Long
                ? (mark0 - entry) * qty
                : (entry - mark0) * qty;

            // Минимум $0.30 unrealized, иначе шум
            if (uPnl < 0.30m)
                return;

            decimal atr = CalculateAtr(klines);
            if (atr <= 0) return;

            decimal mark = pos.MarkPrice > 0 ? pos.MarkPrice : entry;
            decimal rr = Math.Abs(mark - entry) / atr;
            if (rr < 1.0m)
                return; // harvest только после ≥1R

            // Чем сильнее edge (RR), тем меньше кусок — оставляем runner
            decimal harvestPct =
                rr >= 2.5m ? 0.18m :
                rr >= 1.6m ? 0.28m :
                0.40m;

            decimal closeQty = qty * harvestPct;
            try
            {
                var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
                var step = filters.step > 0 ? filters.step : 0.001m;
                closeQty = Math.Floor(closeQty / step) * step;
                if (closeQty < filters.minQty) return;
                if (closeQty >= qty) return; // не full-close harvest'ом
            }
            catch { return; }

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
                _logger.LogWarning("[HYBRID-HARVEST][{symbol}][{side}] FAIL: {err}", symbol, side, res.Error);
                return;
            }

            _hybridLastHarvest[$"{symbol}|{side}"] = DateTime.UtcNow;
            _hybridRecentPartial[$"{symbol}|{side}"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _logger.LogInformation(
                "[HYBRID-HARVEST][{symbol}][{side}] close={q}/{total} uPnl={pnl:F2} rr={rr:F2} pct={pct:P0}",
                symbol, side, closeQty, qty, uPnl, rr, harvestPct);
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
                // ignore
            }

            return fallback > 0 ? fallback : 0m;
        }
    }
}
