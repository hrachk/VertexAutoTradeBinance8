// ============================================================================
// ORDER EXECUTOR v6.8 — SAFE ENTRY PROTECTOR (DUAL: ORDER + POSITION)
// - ENTRY: Limit (без reduceOnly)
// - Ждём ОТКРЫТУЮ ПОЗИЦИЮ, но параллельно отслеживаем ОРДЕР
// - Если:
//      • ордер не заполняется и цена улетела → отменяем, считаем пропущенной
//      • ордер частично заполнен → НЕ трогаем, ждём появление позиции
//      • ордер Filled / PartialFilled, позиция появилась → сразу ставим SL/TP
// - НИКАКИХ позиций без SL/TP
// - НИКАКОГО тупого догоняния монеты
// ============================================================================

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
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
        private readonly TradeSignalMemoryService _signalMemory;
        private readonly ManagedPositionRegistry _managed;

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
            ManagedPositionRegistry managed)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
            _simulator = simulator;
            _executedSignalService = executedSignalService;
            _marketData = marketData;
            _marketRegimeService = marketRegimeService;
            _smartRegime = smartRegime;
            _signalMemory = signalMemory;
            _managed = managed;
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

            // =============================================================
            // 0) Regime / SmartRegime → UI / analytics
            // =============================================================
            var klines = await _marketData.GetKlines(signal.Symbol, KlineInterval.FiveMinutes, 200);
            var baseReg = _marketRegimeService.DetectRegime(signal.Symbol, KlineInterval.FiveMinutes, klines);
            var smart = _smartRegime.Evaluate(signal.Symbol, KlineInterval.FiveMinutes, klines);

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
            // 1) ENTRY (LIMIT) — БЕЗ reduceOnly
            // =====================================================================
            var entryRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: signal.Symbol,
                side: side,
                type: FuturesOrderType.Limit,
                quantity: quantity,
                price: entryPrice,
                positionSide: posSide,
                workingType: WorkingType.Mark,
                timeInForce: TimeInForce.GoodTillCanceled,
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
                // Позиция реально НЕТ → считаем пропущенной
                _logger.LogError(
                    "[ORDER][{symbol}] ENTRY FAIL — {reason}",
                    signal.Symbol, wait.Reason);

                await _simulator.SimulateMissedTradeAsync(signal, wait.Reason ?? "EntryNotFilled");

                // На всякий случай: отмена ордера (если ещё жив)
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

            // =====================================================================
            // 3) COMPUTE DYNAMIC SL/TP (ATR, trend, volatility)
            // =====================================================================
            decimal atr = signal.Atr ?? 0;
            decimal sl = signal.StopLoss;

            var tps = new List<decimal>();
            if (signal.TakeProfits != null)
            {
                foreach (var x in signal.TakeProfits)
                    if (x > 0) tps.Add(Round(x, tick));
            }
            if (tps.Count == 0 && signal.TakeProfit is > 0)
                tps.Add(Round(signal.TakeProfit.Value, tick));

            if (tps.Count == 0 && atr > 0)
            {
                decimal slDist = Math.Abs(entryPrice - sl);
                if (slDist <= 0) slDist = atr * 2.0m;
                if (signal.Side == SignalSide.Buy)
                {
                    tps.Add(Round(entryPrice + slDist * 1.5m, tick));
                    tps.Add(Round(entryPrice + slDist * 2.5m, tick));
                    tps.Add(Round(entryPrice + slDist * 4.0m, tick));
                }
                else
                {
                    tps.Add(Round(entryPrice - slDist * 1.5m, tick));
                    tps.Add(Round(entryPrice - slDist * 2.5m, tick));
                    tps.Add(Round(entryPrice - slDist * 4.0m, tick));
                }
            }
            while (tps.Count > 3) tps.RemoveAt(tps.Count - 1);

            // Если пришёл только 1 TP — развернуть в 1.5R/2.5R/4R от SL
            if (tps.Count == 1 && sl > 0)
            {
                decimal baseTp = tps[0];
                decimal slDist = Math.Abs(entryPrice - sl);
                if (slDist > 0)
                {
                    tps.Clear();
                    if (signal.Side == SignalSide.Buy)
                    {
                        tps.Add(Round(entryPrice + slDist * 1.5m, tick));
                        tps.Add(Round(entryPrice + slDist * 2.5m, tick));
                        tps.Add(Round(entryPrice + slDist * 4.0m, tick));
                    }
                    else
                    {
                        tps.Add(Round(entryPrice - slDist * 1.5m, tick));
                        tps.Add(Round(entryPrice - slDist * 2.5m, tick));
                        tps.Add(Round(entryPrice - slDist * 4.0m, tick));
                    }
                }
            }

            sl = Round(sl, tick);

            _logger.LogInformation(
                "[ORDER][{symbol}] PROTECTION → SL={sl}, TPs=[{tps}], qty={qty}",
                signal.Symbol, sl, string.Join(", ", tps.Select(x => x.ToString("0.########"))), quantity);

            signal.IsManual = false;
            signal.EntryPrice = entryPrice;
            signal.StopLoss = sl;
            if (tps.Count > 0)
            {
                signal.TakeProfits = tps.ToList();
                signal.TakeProfit = tps[0];
            }
            _signalMemory.Save(signal);
            _managed.RegisterFromSignal(signal, entryPrice);

            // =====================================================================
            // 3.5) CLEAR OLD protective orders (SL/TP algo + reduceOnly) BEFORE placing new ones
            //      Critical: after reopen / second entry / leftover from previous cycle
            // =====================================================================
            var closeSide = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
            await CancelAllProtectiveOrdersAsync(client, signal.Symbol, posSide, closeSide, ct);

            // =====================================================================
            // 4) CREATE SL via Algo Order API (STOP_MARKET) — required since Binance 2025-12-09
            //    Do NOT send reduceOnly together with positionSide (hedge mode → -1106)
            // =====================================================================

            var slOrder = await client.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
                symbol: signal.Symbol,
                side: closeSide,
                type: ConditionalOrderType.StopMarket,
                quantity: quantity,
                triggerPrice: sl,
                positionSide: posSide,
                workingType: WorkingType.Mark,
                timeInForce: TimeInForce.GoodTillCanceled,
                ct: ct);

            if (!slOrder.Success)
            {
                _logger.LogError("[ORDER][{symbol}] SL CREATE ERROR: {err}",
                    signal.Symbol, slOrder.Error);
                return OrderResult.Fail("SL_CREATE_ERROR");
            }

            _logger.LogInformation("[ORDER][{symbol}] SL OK (algo): trigger={sl}, algoId={id}",
                signal.Symbol, sl, slOrder.Data?.Id);

            // =====================================================================
            // 5) CREATE TP via Algo Order API (TAKE_PROFIT_MARKET)
            // =====================================================================
            if (tps.Count > 0)
            {
                decimal[] fracs = tps.Count switch
                {
                    1 => new[] { 1.0m },
                    2 => new[] { 0.50m, 0.50m },
                    _ => new[] { 0.40m, 0.30m, 0.30m }
                };

                decimal placed = 0m;
                for (int i = 0; i < tps.Count && i < fracs.Length; i++)
                {
                    decimal q = quantity * fracs[i];
                    if (i == Math.Min(tps.Count, fracs.Length) - 1)
                        q = quantity - placed;
                    q = Math.Round(q, 8);
                    if (q <= 0) continue;
                    placed += q;

                    var tpOrder = await client.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
                        symbol: signal.Symbol,
                        side: closeSide,
                        type: ConditionalOrderType.TakeProfitMarket,
                        quantity: q,
                        triggerPrice: tps[i],
                        positionSide: posSide,
                        workingType: WorkingType.Mark,
                        timeInForce: TimeInForce.GoodTillCanceled,
                        ct: ct);

                    if (!tpOrder.Success)
                    {
                        _logger.LogError("[ORDER][{symbol}] TP{n} CREATE ERROR: {err}",
                            signal.Symbol, i + 1, tpOrder.Error);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "[ORDER][{symbol}] TP{n} OK (algo): trigger={tp}, qty={q}, algoId={id}",
                            signal.Symbol, i + 1, tps[i], q, tpOrder.Data?.Id);
                    }
                }
            }
            else
            {
                _logger.LogWarning("[ORDER][{symbol}] TP not set — защищаем только SL", signal.Symbol);
            }

            _logger.LogInformation(
                "[ORDER][{symbol}] MANAGED OK entry={e} SL={sl} TPs={n}",
                signal.Symbol, entryPrice, sl, tps.Count);

            return OrderResult.Successs(entryPrice, quantity, entryOrderId);
        }


        // =====================================================================
        // CANCEL ALL protective (SL/TP/algo) orders for symbol+side before new placement
        // =====================================================================
        private async Task CancelAllProtectiveOrdersAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide posSide,
            OrderSide closeSide,
            CancellationToken ct)
        {
            try
            {
                // 1) Regular open orders (legacy STOP/TP/Limit reduceOnly)
                var openRes = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct);
                if (openRes.Success && openRes.Data != null)
                {
                    foreach (var o in openRes.Data)
                    {
                        bool isProtective =
                            (o.PositionSide == posSide || o.PositionSide == PositionSide.Both) &&
                            o.Side == closeSide &&
                            (
                                o.Type == FuturesOrderType.StopMarket ||
                                o.Type == FuturesOrderType.Stop ||
                                o.Type == FuturesOrderType.TakeProfitMarket ||
                                o.Type == FuturesOrderType.TakeProfit ||
                                (o.Type == FuturesOrderType.Limit && o.ReduceOnly == true)
                            );

                        if (!isProtective) continue;

                        try
                        {
                            var c = await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, o.Id, ct: ct);
                            if (c.Success)
                                _logger.LogInformation("[ORDER][{symbol}] CLEARED old order id={id} type={t}", symbol, o.Id, o.Type);
                            else
                                _logger.LogWarning("[ORDER][{symbol}] CLEAR order fail id={id}: {err}", symbol, o.Id, c.Error);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[ORDER][{symbol}] CLEAR order exception id={id}", symbol, o.Id);
                        }
                    }
                }

                // 2) Algo / conditional orders (primary path after Binance 2025-12-09)
                try
                {
                    var algoRes = await client.UsdFuturesApi.Trading.GetOpenConditionalOrdersAsync(symbol: symbol, ct: ct);
                    if (algoRes.Success && algoRes.Data != null)
                    {
                        foreach (var a in algoRes.Data)
                        {
                            bool isProtective =
                                (a.PositionSide == posSide || a.PositionSide == PositionSide.Both) &&
                                a.Side == closeSide &&
                                (
                                    a.Type == FuturesOrderType.StopMarket ||
                                    a.Type == FuturesOrderType.Stop ||
                                    a.Type == FuturesOrderType.TakeProfitMarket ||
                                    a.Type == FuturesOrderType.TakeProfit
                                );

                            if (!isProtective) continue;

                            try
                            {
                                var c = await client.UsdFuturesApi.Trading.CancelConditionalOrderAsync(orderId: a.Id, ct: ct);
                                if (c.Success)
                                    _logger.LogInformation("[ORDER][{symbol}] CLEARED old ALGO id={id} type={t}", symbol, a.Id, a.Type);
                                else
                                {
                                    // fallback: try regular cancel
                                    try { await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, a.Id, ct: ct); } catch { }
                                    _logger.LogWarning("[ORDER][{symbol}] CLEAR algo fail id={id}: {err}", symbol, a.Id, c.Error);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "[ORDER][{symbol}] CLEAR algo exception id={id}", symbol, a.Id);
                            }
                        }
                    }
                }
                catch (Exception exAlgo)
                {
                    _logger.LogWarning(exAlgo, "[ORDER][{symbol}] GetOpenConditionalOrders failed", symbol);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ORDER][{symbol}] CancelAllProtectiveOrders failed", symbol);
            }
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

            // Адаптивный slip: 0.4% убивало альты (AKE vol~4%, diff 0.83% → PriceRunAway).
            // Берём max(0.8%, ATR% * 1.25), cap 3%.
            decimal atrPct = 0.01m;
            if (signal.Atr is > 0 && fallbackEntry > 0)
                atrPct = signal.Atr.Value / fallbackEntry;
            decimal maxSlipPct = atrPct * 1.25m;
            if (maxSlipPct < 0.008m) maxSlipPct = 0.008m; // min 0.8%
            if (maxSlipPct > 0.030m) maxSlipPct = 0.030m; // max 3%

            _logger.LogInformation(
                "[ORDER][{symbol}] Wait fill: maxSlip={slip:P2} (atr%={atr:P2})",
                signal.Symbol, maxSlipPct, atrPct);

            decimal lastExecuted = 0m;

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

                    // ---- 3) Проверка "цена улетела" (только если вообще не fill'ился) ----
                    if (lastExecuted <= 0)
                    {
                        try
                        {
                            var priceRes = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(signal.Symbol, ct: ct);
                            if (priceRes.Success && priceRes.Data != null && priceRes.Data.Price > 0)
                            {
                                var mark = priceRes.Data.Price;
                                decimal diffPct = 0m;

                                if (posSide == PositionSide.Long)
                                {
                                    diffPct = (mark - fallbackEntry) / fallbackEntry;
                                    // adverse only if price ran UP and nothing filled yet
                                    if (diffPct >= maxSlipPct && executedQty <= 0)
                                    {
                                        _logger.LogWarning(
                                            "[ORDER][{symbol}] PRICE RUN AWAY (LONG): entry={e}, mark={m}, diff={d:P2}, max={max:P2}",
                                            signal.Symbol, fallbackEntry, mark, diffPct, maxSlipPct);

                                        try
                                        {
                                            await client.UsdFuturesApi.Trading.CancelOrderAsync(signal.Symbol, entryOrderId, ct: ct);
                                        }
                                        catch { }

                                        return (false, 0m, 0m, "PriceRunAway");
                                    }
                                }
                                else // Short
                                {
                                    diffPct = (fallbackEntry - mark) / fallbackEntry;
                                    // adverse for short limit: price ran DOWN and nothing filled
                                    if (diffPct >= maxSlipPct && executedQty <= 0)
                                    {
                                        _logger.LogWarning(
                                            "[ORDER][{symbol}] PRICE RUN AWAY (SHORT): entry={e}, mark={m}, diff={d:P2}, max={max:P2}",
                                            signal.Symbol, fallbackEntry, mark, diffPct, maxSlipPct);

                                        try
                                        {
                                            await client.UsdFuturesApi.Trading.CancelOrderAsync(signal.Symbol, entryOrderId, ct: ct);
                                        }
                                        catch { }

                                        return (false, 0m, 0m, "PriceRunAway");
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
                    // Теоретически позиция может появиться позже, но мы сделали всё возможное.
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
    }
}
