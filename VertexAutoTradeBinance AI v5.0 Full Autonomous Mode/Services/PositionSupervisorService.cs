using System;
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

        private MarketRegime _regimeNow;

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
            ExecutedSignalService executed)
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

            _regimeNow = MarketRegime.Range;
        }

        // --------------------------------------------------------------------
        // PATCH BLOCK: ручная проверка SL/TP для уже открытых позиций
        // --------------------------------------------------------------------
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

            // 0) Detect manual position
            if (lastSignal == null)
            {
                var manualSignal = await _manualHandler.DetectManualAsync(client, symbol, ct);
                if (manualSignal != null)
                {
                    lastSignal = manualSignal;
                    _logger.LogWarning("[MANUAL][{symbol}] Virtual signal injected", symbol);
                }
            }

            // 1) Load positions with retry
            var posInfo = await GetPositionsWithRetryAsync(client, symbol, ct);
            if (posInfo == null || !posInfo.Success || posInfo.Data == null)
            {
                _logger.LogWarning("[SUPERVISOR] No positions {symbol}", symbol);
                return;
            }

            // PATCH: manual positions SL/TP ensure
            var posResult = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol);
            var posForPatch = posResult.Data?.FirstOrDefault();

            if (posForPatch != null && posForPatch.Quantity != 0)
            {
                _logger.LogInformation("[PATCH] Existing/manual position detected for {symbol}. Checking SL/TP…", symbol);

                var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
                var tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

                await EnsureStopLossExists(client, symbol, posForPatch, tick);
                await EnsureTakeProfitExists(client, symbol, posForPatch, tick);
            }

            var longPos = posInfo.Data.FirstOrDefault(p => p.PositionSide == PositionSide.Long);
            var shortPos = posInfo.Data.FirstOrDefault(p => p.PositionSide == PositionSide.Short);

            var hasLong = longPos != null && longPos.Quantity != 0m;
            var hasShort = shortPos != null && shortPos.Quantity != 0m;

            if (!hasLong && !hasShort)
            {
                _logger.LogInformation("[SUPERVISOR] {symbol}: no positions", symbol);
                return;
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

                return;
            }

            if (qty <= 0)
            {
                _logger.LogInformation("[SUPERVISOR] {symbol} {side}: no qty", symbol, side);
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

            // 3) Trailing + Runner
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

            // =============================================================
            // BREAK-EVEN LOCK v1
            // Как только позиция в плюсе — сразу тянем SL на entry.
            // Цель: не отдавать депозит, если цена развернулась после плюса.
            // Триггер: прибыль ≥ max(0.20×ATR, 0.12% цены) — фильтр шума.
            // =============================================================
            decimal beBuffer = Math.Max(atr * 0.20m, entryPrice * 0.0012m);
            bool inProfit = realSide == PositionSide.Long
                ? mark >= entryPrice + beBuffer
                : mark <= entryPrice - beBuffer;

            if (inProfit && entryPrice > 0)
            {
                // Чуть выше/ниже entry, чтобы покрыть комиссии (~0.04–0.08%)
                decimal feePad = entryPrice * 0.0004m;
                decimal beSl = realSide == PositionSide.Long
                    ? entryPrice + feePad
                    : entryPrice - feePad;

                bool needBe = realSide == PositionSide.Long
                    ? currentSl < beSl
                    : (currentSl <= 0 || currentSl > beSl);

                if (needBe)
                {
                    _logger.LogInformation(
                        "[SUPERVISOR][{symbol}] BREAK-EVEN LOCK → SL {old} → {be} (entry={entry}, mark={mark})",
                        symbol, currentSl, beSl, entryPrice, mark);

                    await UpdateSLAsync(
                        client, symbol, realSide, qty,
                        slOrder, entryPrice, beSl,
                        signal, ct);

                    // после BE обновляем локальный currentSl для дальнейшего трейла
                    currentSl = beSl;
                    // перечитаем algo SL id может смениться — следующий цикл подхватит
                }
            }

            var contLevel = EvaluateTrendContinuation(realSide, entryPrice, atr, klines);

            // HIGH trend: не трогаем агрессивный trail, но BE уже поставлен выше
            if (contLevel == TrendContinuationLevel.High)
            {
                _logger.LogInformation(
                    "[SUPERVISOR] {symbol} {side}: trend continuation HIGH → trailing HOLD (BE already applied if in profit)",
                    symbol, realSide);
                return;
            }

            decimal ema21 = CalculateEma(klines, 21);
            decimal st = SuperTrend(klines, atr);

            decimal slAtr = realSide == PositionSide.Long
                ? mark - atr * 0.9m
                : mark + atr * 0.9m;

            decimal slEma = realSide == PositionSide.Long
                ? ema21 - atr * 0.3m
                : ema21 + atr * 0.3m;

            decimal slSt = st;

            var prev = klines[klines.Count - 2];

            bool microUp = last.LowPrice > prev.LowPrice;
            bool microDn = last.HighPrice < prev.HighPrice;

            decimal slMicro = realSide == PositionSide.Long
                ? (microUp ? last.LowPrice - atr * 0.2m : last.LowPrice - atr * 0.6m)
                : (microDn ? last.HighPrice + atr * 0.2m : last.HighPrice + atr * 0.6m);

            decimal targetSl = realSide switch
            {
                PositionSide.Long => new[] { slAtr, slEma, slSt, slMicro }.Max(),
                PositionSide.Short => new[] { slAtr, slEma, slSt, slMicro }.Min(),
                _ => signal.StopLoss
            };

            // Пол после BE: никогда не опускаем SL ниже entry, если уже были в плюсе
            if (inProfit)
            {
                if (realSide == PositionSide.Long)
                    targetSl = Math.Max(targetSl, entryPrice);
                else if (realSide == PositionSide.Short)
                    targetSl = Math.Min(targetSl, entryPrice);
            }

            // Тянем SL только в прибыльную сторону (не ослабляем)
            if (realSide == PositionSide.Long && targetSl <= currentSl)
                return;
            if (realSide == PositionSide.Short && currentSl > 0 && targetSl >= currentSl)
                return;

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
