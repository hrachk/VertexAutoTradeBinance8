// ============================================================================
// ORDER EXECUTOR v7 — SAFE ENTRY + GUARANTEED PROTECTION
//
// Исправленные дефекты v6.8:
//  • Частичное исполнение: позиция защищалась на частичный объём, а остаток
//    лимитки висел и позже доливал позицию БЕЗ SL/TP. Теперь остаток отменяется,
//    защита ставится на фактический объём.
//  • Ошибка постановки SL → метод возвращал Fail и оставлял ГОЛУЮ позицию.
//    Теперь: 3 попытки, затем аварийное закрытие по рынку.
//  • SL ставился на фиксированный qty: после TP1/TP2 qty SL > позиции → риск
//    отклонения при срабатывании. Теперь SL = STOP_MARKET closePosition=true.
//  • Объёмы TP округлялись до 8 знаков, а не до stepSize → -1111 / -4003.
//  • Плечо на бирже не выставлялось никогда — теперь ChangeInitialLeverage.
//  • Если цена уже за SL к моменту исполнения → немедленный выход.
//  • TP сдвигаются на разницу fill−signal, чтобы сохранить R-кратности.
// ============================================================================

using System.Collections.Concurrent;
using Binance.Net.Clients;
using Binance.Net.Enums;
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
        private readonly ExecutedSignalService _journal;
        private readonly MarketDataService _marketData;
        private readonly AiMarketRegimeService _marketRegimeService;
        private readonly SmartRegimeService _smartRegime;
        private readonly TradeSignalMemoryService _signalMemory;
        private readonly ManagedPositionRegistry _managed;
        private readonly AccountStateCache _account;
        private readonly TradingOptions _options;

        private readonly ConcurrentDictionary<string, int> _leverageSet = new(StringComparer.OrdinalIgnoreCase);

        public OrderExecutor(
            ILogger<OrderExecutor> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            SimulatedTradeService simulator,
            ExecutedSignalService executedSignalService,
            MarketDataService marketData,
            AiMarketRegimeService marketRegimeService,
            SmartRegimeService smartRegime,
            TradeSignalMemoryService signalMemory,
            ManagedPositionRegistry managed,
            AccountStateCache account,
            TradingOptions options)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
            _simulator = simulator;
            _journal = executedSignalService;
            _marketData = marketData;
            _marketRegimeService = marketRegimeService;
            _smartRegime = smartRegime;
            _signalMemory = signalMemory;
            _managed = managed;
            _account = account;
            _options = options;
        }

        // =====================================================================
        // MAIN ENTRY
        // =====================================================================
        public async Task<OrderResult> ExecuteAsync(TradeSignal signal, decimal quantity, CancellationToken ct = default)
        {
            var client = _factory.CreateRestClient();

            var filters = await _symbolInfo.GetFuturesFiltersAsync(signal.Symbol);
            var tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;
            var step = filters.step <= 0 ? 0.001m : filters.step;
            var minQty = filters.minQty > 0 ? filters.minQty : step;

            quantity = SymbolInfoService.FloorToStep(quantity, step);
            if (quantity < minQty)
            {
                await _simulator.SimulateMissedTradeAsync(signal, "QuantityTooSmall");
                return OrderResult.Fail("Quantity too small");
            }

            var side = signal.Side == SignalSide.Buy ? OrderSide.Buy : OrderSide.Sell;
            var closeSide = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
            var posSide = signal.Side == SignalSide.Buy ? PositionSide.Long : PositionSide.Short;
            decimal entryPrice = SymbolInfoService.RoundToTick(signal.EntryPrice, tick);

            // ---------- 0) leverage на бирже ----------
            if (!await EnsureLeverageAsync(client, signal.Symbol, ct))
                return OrderResult.Fail("LEVERAGE_SET_FAILED");

            // ---------- 0b) journal ----------
            var klines = await _marketData.GetKlines(signal.Symbol, KlineInterval.FiveMinutes, 200);
            decimal volatility = 0, slope = 0;
            int opportunityScore = 50;
            if (klines.Count > 30)
            {
                var baseReg = _marketRegimeService.DetectRegime(signal.Symbol, KlineInterval.FiveMinutes, klines);
                var smart = _smartRegime.Evaluate(signal.Symbol, KlineInterval.FiveMinutes, klines);
                volatility = baseReg.VolatilityPercent;
                slope = baseReg.TrendSlopePercent;
                opportunityScore = (int)(smart.Confidence * 100);
            }

            var rec = _journal.AddSignalCreated(signal, opportunityScore, signal.Atr ?? 0, volatility, slope,
                quantity, quantity * entryPrice, $"tf={signal.Timeframe}");

            // ---------- 1) ENTRY LIMIT ----------
            var entryRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: signal.Symbol,
                side: side,
                type: FuturesOrderType.Limit,
                quantity: quantity,
                price: entryPrice,
                positionSide: posSide,
                timeInForce: TimeInForce.GoodTillCanceled,
                ct: ct);

            if (!entryRes.Success || entryRes.Data == null)
            {
                _logger.LogError("[ORDER][{symbol}] ENTRY ERROR: {err}", signal.Symbol, entryRes.Error);
                _journal.UpdateById(rec.Id, TradeExecutionStatus.PositionError, closeReason: "EntryRejected: " + entryRes.Error?.Message);
                await _simulator.SimulateMissedTradeAsync(signal, "EntryError");
                return OrderResult.Fail(entryRes.Error?.Message ?? "ENTRY_ERROR");
            }

            long entryOrderId = entryRes.Data.Id;
            _logger.LogInformation("[ORDER][{symbol}] ENTRY placed id={id} price={price} qty={qty}",
                signal.Symbol, entryOrderId, entryPrice, quantity);
            _journal.UpdateById(rec.Id, TradeExecutionStatus.OrderCreated, qty: quantity, notional: quantity * entryPrice);

            // ---------- 2) WAIT FILL (и отмена остатка) ----------
            var fill = await WaitForFillAsync(client, signal, posSide, entryOrderId, entryPrice, ct);
            _account.Invalidate();

            if (!fill.Filled)
            {
                _logger.LogWarning("[ORDER][{symbol}] ENTRY not filled — {reason}", signal.Symbol, fill.Reason);
                _journal.UpdateById(rec.Id, TradeExecutionStatus.Cancelled, closeReason: fill.Reason);
                await _simulator.SimulateMissedTradeAsync(signal, fill.Reason);
                return OrderResult.Fail(fill.Reason);
            }

            // фактическая позиция с биржи (источник истины)
            var pos = await _account.GetPositionAsync(signal.Symbol, posSide, ct, forceRefresh: true);
            decimal qty = pos != null ? Math.Abs(pos.Quantity) : fill.Qty;
            decimal fillPrice = pos?.EntryPrice > 0 ? pos.EntryPrice : fill.AvgPrice;

            _logger.LogInformation("[ORDER][{symbol}] POSITION OPENED {side} qty={qty} @ {px}",
                signal.Symbol, posSide, qty, fillPrice);

            // ---------- 3) SL / TP относительно фактического входа ----------
            decimal sl = SymbolInfoService.RoundToTick(signal.StopLoss, tick);
            decimal shift = fillPrice - signal.EntryPrice;
            var tps = (signal.TakeProfits ?? new List<decimal>())
                .Where(x => x > 0)
                .Select(x => SymbolInfoService.RoundToTick(x + shift, tick))
                .Take(3)
                .ToList();

            if (tps.Count == 0)
            {
                decimal r = Math.Abs(fillPrice - sl);
                int dir = posSide == PositionSide.Long ? 1 : -1;
                tps = new List<decimal> { 1.5m, 2.5m, 4.0m }
                    .Select(m => SymbolInfoService.RoundToTick(fillPrice + dir * r * m, tick)).ToList();
            }

            decimal mark = await GetMarkPriceAsync(client, signal.Symbol, fillPrice, ct);
            bool slCrossed = posSide == PositionSide.Long ? mark <= sl : mark >= sl;
            if (slCrossed)
            {
                _logger.LogError("[ORDER][{symbol}] price {mark} already beyond SL {sl} after fill → EMERGENCY CLOSE",
                    signal.Symbol, mark, sl);
                await EmergencyCloseAsync(client, signal.Symbol, posSide, qty, ct);
                _journal.UpdateById(rec.Id, TradeExecutionStatus.PositionError, entryPrice: fillPrice, closeReason: "SlCrossedOnFill");
                return OrderResult.Fail("SL_CROSSED_ON_FILL");
            }

            // убрать TP, которые уже пройдены ценой
            tps = tps.Where(tp => posSide == PositionSide.Long ? tp > mark : tp < mark).ToList();

            // Регистрация ДО постановки защиты: supervisor знает, что позиция наша, даже если процесс упадёт
            signal.IsManual = false;
            signal.EntryPrice = fillPrice;
            signal.StopLoss = sl;
            signal.TakeProfits = tps.ToList();
            signal.TakeProfit = tps.Count > 0 ? tps[0] : null;
            _signalMemory.Save(signal);
            _managed.RegisterFromSignal(signal, fillPrice, qty, rec.Id);

            await CancelProtectiveOrdersAsync(client, signal.Symbol, posSide, closeSide, ct);

            // ---------- 4) SL (closePosition) с ретраями ----------
            bool slOk = await PlaceStopLossAsync(client, signal.Symbol, posSide, sl, ct);
            if (!slOk)
            {
                _logger.LogCritical("[ORDER][{symbol}] SL could not be placed → EMERGENCY CLOSE (no naked positions)", signal.Symbol);
                await EmergencyCloseAsync(client, signal.Symbol, posSide, qty, ct);
                _journal.UpdateById(rec.Id, TradeExecutionStatus.PositionError, entryPrice: fillPrice, closeReason: "SlPlacementFailed");
                return OrderResult.Fail("SL_CREATE_ERROR");
            }

            // ---------- 5) TP ladder ----------
            int tpOk = await PlaceTpLadderAsync(client, signal.Symbol, posSide, qty, tps, step, minQty, _options.EffectiveTpSplit, _logger, ct);

            _journal.UpdateById(rec.Id, TradeExecutionStatus.PositionOpened,
                qty: qty, notional: qty * fillPrice, entryPrice: fillPrice, stopLoss: sl, takeProfits: tps);

            _logger.LogInformation("[ORDER][{symbol}] MANAGED OK entry={e} SL={sl} TPs={n}/{m}",
                signal.Symbol, fillPrice, sl, tpOk, tps.Count);

            _account.Invalidate();
            return OrderResult.Successs(fillPrice, qty, entryOrderId);
        }

        // =====================================================================
        // LEVERAGE
        // =====================================================================
        private async Task<bool> EnsureLeverageAsync(BinanceRestClient client, string symbol, CancellationToken ct)
        {
            int lev = Math.Clamp(_options.Leverage, 1, 50);
            if (_leverageSet.TryGetValue(symbol, out var done) && done == lev)
                return true;

            var res = await client.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, lev, ct: ct);
            if (res.Success)
            {
                _leverageSet[symbol] = lev;
                _logger.LogInformation("[ORDER][{symbol}] leverage set to {lev}x", symbol, lev);
                return true;
            }

            _logger.LogError("[ORDER][{symbol}] leverage {lev}x failed: {err}", symbol, lev, res.Error);
            return false;
        }

        // =====================================================================
        // WAIT FILL
        // =====================================================================
        private async Task<(bool Filled, decimal Qty, decimal AvgPrice, string Reason)> WaitForFillAsync(
            BinanceRestClient client, TradeSignal signal, PositionSide posSide, long orderId, decimal limitPrice, CancellationToken ct)
        {
            int timeoutSec = Math.Clamp(_options.EntryFillTimeoutSeconds, 10, 600);
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);

            decimal atrPct = signal.Atr is > 0 && limitPrice > 0 ? signal.Atr.Value / limitPrice : 0.01m;
            decimal maxRunPct = Math.Clamp(atrPct * 1.25m, 0.008m, 0.03m);

            decimal executed = 0m, avg = limitPrice;
            string reason = "TimeoutNoFill";

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var o = await client.UsdFuturesApi.Trading.GetOrderAsync(signal.Symbol, orderId, ct: ct);
                    if (o.Success && o.Data != null)
                    {
                        executed = o.Data.QuantityFilled;
                        if (o.Data.AveragePrice > 0) avg = o.Data.AveragePrice;

                        if (o.Data.Status == OrderStatus.Filled)
                            return (true, executed, avg, "Filled");

                        if (o.Data.Status is OrderStatus.Canceled or OrderStatus.Rejected or OrderStatus.Expired)
                            return executed > 0 ? (true, executed, avg, "PartialThenCanceled") : (false, 0m, 0m, "OrderCanceled");
                    }

                    if (executed <= 0)
                    {
                        var p = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(signal.Symbol, ct: ct);
                        if (p.Success && p.Data != null && p.Data.Price > 0)
                        {
                            var px = p.Data.Price;
                            decimal run = posSide == PositionSide.Long ? (px - limitPrice) / limitPrice : (limitPrice - px) / limitPrice;
                            if (run >= maxRunPct)
                            {
                                reason = "PriceRunAway";
                                break;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ORDER][{symbol}] wait-fill poll error", signal.Symbol);
                }

                await Task.Delay(1000, ct);
            }

            // Таймаут / runaway: отменяем ОСТАТОК, берём исполненное
            try { await client.UsdFuturesApi.Trading.CancelOrderAsync(signal.Symbol, orderId, ct: ct); } catch { }

            var final = await client.UsdFuturesApi.Trading.GetOrderAsync(signal.Symbol, orderId, ct: ct);
            if (final.Success && final.Data != null && final.Data.QuantityFilled > 0)
            {
                _logger.LogWarning("[ORDER][{symbol}] partial fill {f}/{q} — remainder cancelled ({reason})",
                    signal.Symbol, final.Data.QuantityFilled, final.Data.Quantity, reason);
                return (true, final.Data.QuantityFilled, final.Data.AveragePrice > 0 ? final.Data.AveragePrice : avg, "PartialFill");
            }

            return (false, 0m, 0m, reason);
        }

        // =====================================================================
        // PROTECTION HELPERS (используются и Supervisor'ом)
        // =====================================================================

        /// <summary>STOP_MARKET closePosition=true: всегда закрывает весь остаток позиции стороны.</summary>
        public async Task<bool> PlaceStopLossAsync(BinanceRestClient client, string symbol, PositionSide posSide, decimal sl, CancellationToken ct)
        {
            var closeSide = posSide == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var res = await client.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
                    symbol: symbol,
                    side: closeSide,
                    type: ConditionalOrderType.StopMarket,
                    quantity: null,
                    triggerPrice: sl,
                    positionSide: posSide,
                    workingType: WorkingType.Mark,
                    closePosition: true,
                    priceProtect: true,
                    ct: ct);

                if (res.Success)
                {
                    _logger.LogInformation("[ORDER][{symbol}] SL OK trigger={sl} (closePosition) algoId={id}", symbol, sl, res.Data?.Id);
                    return true;
                }

                _logger.LogError("[ORDER][{symbol}] SL attempt {n}/3 failed: {err}", symbol, attempt, res.Error);
                if (res.Error?.Code == -2021) // would immediately trigger
                    return false;
                await Task.Delay(400 * attempt, ct);
            }
            return false;
        }

        public static async Task<int> PlaceTpLadderAsync(
            BinanceRestClient client, string symbol, PositionSide posSide, decimal qty, List<decimal> tps,
            decimal step, decimal minQty, decimal[]? split, ILogger logger, CancellationToken ct)
        {
            if (tps.Count == 0 || qty <= 0) return 0;
            var closeSide = posSide == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var fr = (split is { Length: >= 3 } ? split : new[] { 0.40m, 0.30m, 0.30m }).Take(tps.Count).ToArray();
            decimal frSum = fr.Sum();
            if (frSum <= 0) fr = Enumerable.Repeat(1m / tps.Count, tps.Count).ToArray();
            else fr = fr.Select(x => x / frSum).ToArray();

            // объёмы по шагу; слишком маленькие куски сливаются в следующий уровень
            var qtys = new decimal[tps.Count];
            decimal remaining = qty;
            for (int i = 0; i < tps.Count; i++)
            {
                decimal q = i == tps.Count - 1 ? remaining : SymbolInfoService.FloorToStep(qty * fr[i], step);
                q = SymbolInfoService.FloorToStep(Math.Min(q, remaining), step);
                qtys[i] = q;
                remaining -= q;
            }
            for (int i = 0; i < qtys.Length - 1; i++)
            {
                if (qtys[i] > 0 && qtys[i] < minQty) { qtys[i + 1] += qtys[i]; qtys[i] = 0; }
            }
            if (qtys[^1] > 0 && qtys[^1] < minQty)
            {
                int j = Array.FindLastIndex(qtys, qtys.Length - 2, qtys.Length - 1, x => x > 0);
                if (j >= 0) { qtys[j] += qtys[^1]; qtys[^1] = 0; }
            }

            int ok = 0;
            for (int i = 0; i < tps.Count; i++)
            {
                if (qtys[i] < minQty) continue;
                var res = await client.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
                    symbol: symbol,
                    side: closeSide,
                    type: ConditionalOrderType.TakeProfitMarket,
                    quantity: qtys[i],
                    triggerPrice: tps[i],
                    positionSide: posSide,
                    workingType: WorkingType.Mark,
                    timeInForce: TimeInForce.GoodTillCanceled,
                    ct: ct);
                if (res.Success)
                {
                    ok++;
                    logger.LogInformation("[TP][{symbol}] TP{n} OK trigger={tp} qty={q}", symbol, i + 1, tps[i], qtys[i]);
                }
                else
                {
                    logger.LogError("[TP][{symbol}] TP{n} FAIL trigger={tp} qty={q}: {err}", symbol, i + 1, tps[i], qtys[i], res.Error);
                }
            }
            return ok;
        }

        public async Task EmergencyCloseAsync(BinanceRestClient client, string symbol, PositionSide posSide, decimal qty, CancellationToken ct)
        {
            var closeSide = posSide == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var pos = await _account.GetPositionAsync(symbol, posSide, ct, forceRefresh: true);
                decimal q = pos != null ? Math.Abs(pos.Quantity) : qty;
                if (q <= 0) return;

                var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol, side: closeSide, type: FuturesOrderType.Market,
                    quantity: q, positionSide: posSide, ct: ct);
                if (res.Success)
                {
                    _logger.LogWarning("[ORDER][{symbol}] EMERGENCY CLOSE {side} qty={q} OK", symbol, posSide, q);
                    _account.Invalidate();
                    return;
                }
                _logger.LogCritical("[ORDER][{symbol}] EMERGENCY CLOSE attempt {n} failed: {err}", symbol, attempt, res.Error);
                await Task.Delay(500 * attempt, ct);
            }
        }

        /// <summary>Отмена защитных ордеров (обычные + algo) для стороны позиции.</summary>
        public async Task CancelProtectiveOrdersAsync(BinanceRestClient client, string symbol, PositionSide posSide, OrderSide closeSide, CancellationToken ct)
        {
            try
            {
                var openRes = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct);
                if (openRes.Success && openRes.Data != null)
                {
                    foreach (var o in openRes.Data.Where(o =>
                                 o.PositionSide == posSide && o.Side == closeSide &&
                                 (o.Type is FuturesOrderType.StopMarket or FuturesOrderType.Stop
                                     or FuturesOrderType.TakeProfitMarket or FuturesOrderType.TakeProfit ||
                                  (o.Type == FuturesOrderType.Limit && o.ReduceOnly))))
                    {
                        await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, o.Id, ct: ct);
                    }
                }

                var algoRes = await client.UsdFuturesApi.Trading.GetOpenConditionalOrdersAsync(symbol: symbol, ct: ct);
                if (algoRes.Success && algoRes.Data != null)
                {
                    foreach (var a in algoRes.Data.Where(a => a.PositionSide == posSide && a.Side == closeSide))
                        await client.UsdFuturesApi.Trading.CancelConditionalOrderAsync(orderId: a.Id, ct: ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ORDER][{symbol}] cancel protective failed", symbol);
            }
        }

        private static async Task<decimal> GetMarkPriceAsync(BinanceRestClient client, string symbol, decimal fallback, CancellationToken ct)
        {
            try
            {
                var r = await client.UsdFuturesApi.ExchangeData.GetMarkPriceAsync(symbol, ct: ct);
                if (r.Success && r.Data != null && r.Data.MarkPrice > 0) return r.Data.MarkPrice;
            }
            catch { }
            return fallback;
        }
    }
}
