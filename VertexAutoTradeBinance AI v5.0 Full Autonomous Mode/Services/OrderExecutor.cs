using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class OrderExecutor
    {
        private readonly ILogger<OrderExecutor> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly SymbolInfoService _symbolInfo;
        private readonly TradeResultMonitorService _tradeMonitor;
        private readonly OrderTracerService _orderTracer;          // <=== НОВОЕ
        private readonly TradeSignalMemoryService _signalMemory;   // <=== если ещё не добавлял (для 4.2)
        private readonly AiLeverageService _aiLeverage;

        public OrderExecutor(
            ILogger<OrderExecutor> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            TradeResultMonitorService tradeMonitor,
            OrderTracerService orderTracer,
            TradeSignalMemoryService signalMemory,
            AiLeverageService aiLeverage)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
            _tradeMonitor = tradeMonitor;
            _orderTracer = orderTracer;
            _signalMemory = signalMemory;
            _aiLeverage = aiLeverage;
        }

        private decimal RoundToTick(decimal price, decimal tick)
        {
            if (tick <= 0) return price;
            return Math.Round(price / tick) * tick;
        }

        //=== SAFETY: аварийное закрытие позиции маркетом ======================
        private async Task<bool> ClosePositionMarketAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide positionSide,
            decimal qty)
        {
            if (qty <= 0m)
                return true;

            var side = positionSide == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: side,
                type: FuturesOrderType.Market,
                quantity: qty,
                positionSide: positionSide);

            if (!res.Success)
            {
                _logger.LogError(
                    "❌ [SAFETY] Market close FAILED for {symbol} side={side} qty={qty}: {err}",
                    symbol, positionSide, qty, res.Error);
                return false;
            }

            _logger.LogWarning(
                "✅ [SAFETY] Position {symbol} side={side} qty={qty} closed MARKET due to SL failure",
                symbol, positionSide, qty);

            return true;
        }

        //====================================================================
        // MAIN
        //====================================================================
        /* public async Task PlaceOrderAsync(TradeSignal signal, decimal quantity, CancellationToken ct = default)
         {
             _logger.LogWarning("[ENTRY] PlaceOrderAsync CALLED for {Symbol} side={Side} qty={Qty}",
         signal.Symbol, signal.Side, quantity);

             using var client = _factory.CreateRestClient();

             var side = signal.Side == SignalSide.Buy ? OrderSide.Buy : OrderSide.Sell;
             var positionSide = side == OrderSide.Buy ? PositionSide.Long : PositionSide.Short;

             // =========================================================
             // 1) Биржевые фильтры
             // =========================================================
             var filters = await _symbolInfo.GetFuturesFiltersAsync(signal.Symbol);
             decimal step = filters.step <= 0 ? 0.001m : filters.step;
             decimal tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;
             decimal minQty = filters.minQty;
             decimal minNotional = filters.minNotional;

             decimal qty = Math.Floor(quantity / step) * step;
             if (qty < minQty) qty = minQty;

             // =========================================================
             // 2) MARK price + базовый diff
             // =========================================================
             var markResult = await client.UsdFuturesApi.ExchangeData.GetMarkPriceAsync(signal.Symbol);
             decimal mark = markResult.Success ? markResult.Data.MarkPrice : signal.EntryPrice;

             decimal rawDiffPct = Math.Abs(mark - signal.EntryPrice) / mark * 100m;





             // Допустимый сдвиг цены от сигнальной
             const decimal maxLateEntryPct = 0.8m;           // было 0.35m   // обычный сигнал, если цена ушла дальше → скипаем

             // Супер-сигнал: если цена сильно убежала — сразу чистый Market
             const decimal superSignalMarketDiff = 0.10m;    // было 0.03m   // SUPER-сигнал: при большом diff → чистый Market


             if (!signal.IsSuperSignal && rawDiffPct > maxLateEntryPct)
             {
                 _logger.LogWarning(
                     "[ENTRY][SKIP] {0}: rawDiff={1:F4}% > maxLate={2:F2}%",
                     signal.Symbol, rawDiffPct, maxLateEntryPct);
                 return;
             }


             bool isSuper = signal.IsSuperSignal == true;

             // =========================================================
             // 3) Подготовка SL
             // =========================================================
             decimal slTrig = RoundToTick(signal.StopLoss, tick);
             decimal slLimit =
                 signal.Side == SignalSide.Buy
                     ? RoundToTick(slTrig - tick, tick)
                     : RoundToTick(slTrig + tick, tick);

             decimal initialEntry = RoundToTick(signal.EntryPrice, tick);

             // =========================================================
             // 4) EXECUTION LOOP (умный вход v4.4)
             // =========================================================
             const int maxAttempts = 7;
             const decimal qtyStepFactor = 0.80m;
             const decimal chaseMaxSlippagePct = 0.5m;
             const decimal chaseRecenterTicks = 2m;
             const int chaseDelayMs = 800;

             bool entryDone = false;
             bool isMarketEntry = isSuper && rawDiffPct > superSignalMarketDiff;

             ConsoleReportFormatter.EntryPrep(
                 _logger,
                 signal.Symbol,
                 side == OrderSide.Buy ? "LONG" : "SHORT",
                 initialEntry,
                 slTrig,
                 slLimit,
                 qty,
                 step,
                 tick);

             for (int attempt = 1; attempt <= maxAttempts; attempt++)
             {
                 ct.ThrowIfCancellationRequested();

                 if (qty <= 0 || qty < minQty)
                 {
                     ConsoleReportFormatter.EntryFailedHard(
                         _logger,
                         signal.Symbol,
                         $"qty={qty:F6} < minQty={minQty:F6}");
                     return;
                 }

                 // обновляем MARK
                 markResult = await client.UsdFuturesApi.ExchangeData.GetMarkPriceAsync(signal.Symbol);
                 if (markResult.Success)
                     mark = markResult.Data.MarkPrice;

                 decimal totalDiffPct = Math.Abs(mark - signal.EntryPrice) / mark * 100m;

                 if (!isSuper && totalDiffPct > maxLateEntryPct)
                 {
                     ConsoleReportFormatter.EntryFailedHard(
                         _logger,
                         signal.Symbol,
                         $"late entry: diff={totalDiffPct:F4}% > {maxLateEntryPct:F2}%");


                     _logger.LogWarning(
         "[ENTRY] SKIP {symbol}: totalDiff={diff:F4}% > maxLateEntry={max:F2}%",
         signal.Symbol, totalDiffPct, maxLateEntryPct);


                     return;
                 }

                 // ---------- выбор ENTRY ----------
                 decimal entry;

                 if (isMarketEntry)
                 {
                     entry = mark;
                 }
                 else
                 {
                     bool wantShort = side == OrderSide.Sell;

                     if (wantShort)
                     {
                         if (mark >= signal.EntryPrice)
                         {
                             entry = RoundToTick(signal.EntryPrice, tick);
                         }
                         else
                         {
                             if (totalDiffPct > chaseMaxSlippagePct)
                             {
                                 ConsoleReportFormatter.EntryFailedHard(
                                     _logger,
                                     signal.Symbol,
                                     $"short chase too far: diff={totalDiffPct:F4}% > {chaseMaxSlippagePct:F2}%");
                                 return;
                             }

                             entry = RoundToTick(mark + chaseRecenterTicks * tick, tick);
                         }
                     }
                     else
                     {
                         if (mark <= signal.EntryPrice)
                         {
                             entry = RoundToTick(signal.EntryPrice, tick);
                         }
                         else
                         {
                             if (totalDiffPct > chaseMaxSlippagePct)
                             {
                                 ConsoleReportFormatter.EntryFailedHard(
                                     _logger,
                                     signal.Symbol,
                                     $"long chase too far: diff={totalDiffPct:F4}% > {chaseMaxSlippagePct:F2}%");
                                 return;
                             }

                             entry = RoundToTick(mark - chaseRecenterTicks * tick, tick);
                         }
                     }
                 }

                 // minNotional под текущий entry
                 if (qty * entry < minNotional)
                 {
                     var needed = minNotional / entry;
                     qty = Math.Floor(needed / step) * step;
                 }

                 var order = isMarketEntry
                     ? await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                         symbol: signal.Symbol,
                         side: side,
                         type: FuturesOrderType.Market,
                         quantity: qty,
                         positionSide: positionSide)
                     : await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                         symbol: signal.Symbol,
                         side: side,
                         type: FuturesOrderType.Limit,
                         quantity: qty,
                         price: entry,
                         timeInForce: TimeInForce.GoodTillCanceled,
                         positionSide: positionSide);

                 if (order.Success && order.Data != null)
                 {
                     ConsoleReportFormatter.EntrySuccess(
                         _logger,
                         signal.Symbol,
                         qty,
                         entry,
                         attempt);

                     entryDone = true;
                     break;
                 }

                 var code = order.Error?.Code;
                 var msg = order.Error?.Message ?? "unknown";

                 ConsoleReportFormatter.EntryFallbackAttempt(
                     _logger,
                     signal.Symbol,
                     code,
                     msg,
                     qty,
                     attempt,
                     maxAttempts);

                 if (code is -2019 or -4164 or -1013 or -1111)
                 {
                     qty = Math.Floor((qty * qtyStepFactor) / step) * step;

                     if (attempt < maxAttempts)
                         await Task.Delay(chaseDelayMs, ct);

                     continue;
                 }

                 ConsoleReportFormatter.EntryFailedHard(
                     _logger,
                     signal.Symbol,
                     $"code={code}, msg={msg}");
                 return;
             }

             if (!entryDone)
             {
                 ConsoleReportFormatter.EntryFailedHard(
                     _logger,
                     signal.Symbol,
                     "ENTRY failed after max attempts");
                 return;
             }

             // =========================================================
             // 5) WAIT UNTIL POSITION IS OPEN
             // =========================================================
             decimal openedQty = 0m;

             for (int i = 0; i < 40; i++)
             {
                 ct.ThrowIfCancellationRequested();

                 var pos = await client.UsdFuturesApi.Account.GetPositionInformationAsync(signal.Symbol);

                 if (pos.Success && pos.Data != null)
                 {
                     var p = pos.Data.FirstOrDefault(x => x.PositionSide == positionSide);
                     if (p != null)
                     {
                         var q = Math.Abs(p.Quantity);
                         if (q > 0)
                         {
                             openedQty = q;
                             break;
                         }
                     }
                 }

                 await Task.Delay(500, ct);
             }

             if (openedQty <= 0)
             {
                 _logger.LogWarning(
                     "⚠ POSITION NOT OPENED YET for {symbol}. Supervisor will place SL/TP later.",
                     signal.Symbol);
                 return;
             }

             ConsoleReportFormatter.PositionOpened(
                 _logger,
                 signal.Symbol,
                 positionSide == PositionSide.Long ? "LONG" : "SHORT",
                 openedQty);

             // =========================================================
             // 6) STOP LOSS  (сильная защита)
             // =========================================================
             var slSideReal = signal.Side == SignalSide.Buy ? OrderSide.Sell : OrderSide.Buy;

             var slOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                 symbol: signal.Symbol,
                 side: slSideReal,
                 type: FuturesOrderType.Stop,
                 quantity: openedQty,
                 stopPrice: slTrig,
                 price: slLimit,
                 timeInForce: TimeInForce.GoodTillCanceled,
                 positionSide: positionSide);
             if (slOrder.Success && slOrder.Data != null)
             {
                 _logger.LogInformation($"[SUPERVISOR] SL CREATED {signal.Symbol} {signal.StopLoss} qty={qty}");
                 _orderTracer.Register(signal.Symbol, slOrder.Data.Id);    // <===
             }

             if (!slOrder.Success)
                 _logger.LogError("❌ SL ERROR {Symbol}: {Error}", signal.Symbol, slOrder.Error);
             else
             {
                 ConsoleReportFormatter.SLPlaced(_logger, slTrig, slLimit, openedQty);

                 if (slOrder.Data != null)
                     _orderTracer.Register(signal.Symbol, slOrder.Data.Id);   // <=== ВАЖНО
             }


             if (!slOrder.Success)
             {
                 _logger.LogError("❌ SL ERROR {Symbol}: {Error}", signal.Symbol, slOrder.Error);

                 // === SAFETY: если SL не поставился → закрываем позицию MARKET ===
                 var closed = await ClosePositionMarketAsync(
                     client,
                     signal.Symbol,
                     positionSide,
                     openedQty);

                 if (!closed)
                 {
                     _logger.LogError(
                         "🚨 [CRITICAL] Position {symbol} left WITHOUT SL and market close failed. CHECK MANUALLY!",
                         signal.Symbol);
                 }

                 // дальше TP не ставим, выходим
                 return;
             }
             else
             {
                 ConsoleReportFormatter.SLPlaced(_logger, slTrig, slLimit, openedQty);
             }

             // =========================================================
             // 7) TAKE PROFITS (Multi-TP)
             // =========================================================
             if (signal.TakeProfits != null && signal.TakeProfits.Count > 0)
             {
                 decimal totalPlanned = 0m;

                 for (int i = 0; i < signal.TakeProfits.Count; i++)
                 {
                     decimal tpPart = signal.GetTpPart(i);
                     if (tpPart <= 0) continue;

                     decimal tpQty = Math.Floor((openedQty * tpPart) / step) * step;
                     if (tpQty <= 0) continue;

                     if (totalPlanned + tpQty > openedQty)
                         tpQty = Math.Floor((openedQty - totalPlanned) / step) * step;

                     if (tpQty <= 0) continue;

                     decimal tpPrice = RoundToTick(signal.TakeProfits[i], tick);
                     var tpSide = signal.Side == SignalSide.Buy ? OrderSide.Sell : OrderSide.Buy;

                     var tpOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                         symbol: signal.Symbol,
                         side: tpSide,
                         type: FuturesOrderType.Limit,
                         quantity: tpQty,
                         price: tpPrice,
                         timeInForce: TimeInForce.GoodTillCanceled,
                         positionSide: positionSide);
                     if (!tpOrder.Success)
                     {
                         _logger.LogError("❌ TP{Index} ERROR {Symbol}: {Err}",
                             i + 1, signal.Symbol, tpOrder.Error);
                     }
                     else
                     {
                         totalPlanned += tpQty;
                         ConsoleReportFormatter.TPPlaced(_logger, i + 1, tpPrice, tpQty);

                         if (tpOrder.Data != null)
                             _orderTracer.Register(signal.Symbol, tpOrder.Data.Id);   // <=== ВАЖНО
                     }
                     if (!tpOrder.Success)
                     {
                         _logger.LogError("❌ TP{Index} ERROR {Symbol}: {Err}",
                             i + 1, signal.Symbol, tpOrder.Error);
                     }
                     else
                     {
                         totalPlanned += tpQty;
                         ConsoleReportFormatter.TPPlaced(_logger, i + 1, tpPrice, tpQty);
                     }

                     if (tpOrder.Success && tpOrder.Data != null)
                     {
                         totalPlanned += tpQty;
                         _logger.LogInformation($"[SUPERVISOR] TP CREATED {signal.Symbol} trigger={signal.TakeProfit} qty={qty}");
                         _orderTracer.Register(signal.Symbol, tpOrder.Data.Id);    // <===
                     }



                     if (totalPlanned >= openedQty)
                         break;
                 }
             }

             // =========================================================
             // 8) MONITOR → AI LEARN CLOSE
             // =========================================================
             await _tradeMonitor.CheckClosedPositionAsync(signal.Symbol, signal, ct);

             // =========================================================
             // 9) AI SELF-LEARNING → ENTRY EVENT
             // =========================================================
             _logger.LogInformation(
                 "[AI-LEARN] ENTRY RECORDED for {Symbol} entry≈{Entry}, sl={SL}",
                 signal.Symbol, initialEntry, slTrig);

             _logger.LogInformation(
     "[AI-LEARN] ENTRY RECORDED for {Symbol} entry≈{Entry}, sl={SL}",
     signal.Symbol, initialEntry, slTrig);

             // сохраняем последний сигнал для CheckAfterFill / Recover
             _signalMemory.Save(signal);

         }*/
        public async Task PlaceOrderAsync(TradeSignal signal, decimal quantity, CancellationToken ct = default)
        {
            _logger.LogInformation(
                "[ORDER][{Symbol}] PlaceOrderAsync side={Side} qty={Qty}",
                signal.Symbol, signal.Side, quantity);

            using var client = _factory.CreateRestClient();

            // ----------------------------------------------------
            // 0) Klines для AiLeverageService
            // ----------------------------------------------------
            var tf = KlineInterval.OneMinute; // TODO: можно привязать к signal.Timeframe

            IReadOnlyList<BinanceFuturesUsdtKline> klines;

            var kRes = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                signal.Symbol,
                tf,
                null,
                null,
                120,
                ct);

            if (kRes.Success && kRes.Data != null)
            {
                klines = (IReadOnlyList<BinanceFuturesUsdtKline>)kRes.Data.ToList();
            }
            else
            {
                _logger.LogWarning(
                    "[ORDER][{Symbol}] Failed to load klines for leverage calc: {Err}",
                    signal.Symbol, kRes.Error);

                klines = Array.Empty<BinanceFuturesUsdtKline>();
            }

            // ----------------------------------------------------
            // 1) Базовые параметры сигнала
            // ----------------------------------------------------
            var price = signal.EntryPrice;
            var atr = signal.Atr ?? 0m;

            // TODO: сюда потом подставим реальный баланс от RiskManager / AccountInfo
            decimal balance = 200m;

            // ----------------------------------------------------
            // 2) SAFE LEVERAGE (AI)
            // ----------------------------------------------------
            int leverage = _aiLeverage.CalculateSafeLeverage(
                symbol: signal.Symbol,
                price: price,
                atr: atr,
                balanceUSDT: balance,
                tf: tf,
                klines: klines);

            var levResult = await client.UsdFuturesApi.Account.ChangeInitialLeverageAsync(
                symbol: signal.Symbol,
                leverage: leverage,
                ct: ct);

            if (!levResult.Success)
            {
                _logger.LogError(
                    "[ORDER][{Symbol}] Failed to set leverage {Lev}x: {Msg}",
                    signal.Symbol,
                    leverage,
                    levResult.Error?.Message);
            }

            // ----------------------------------------------------
            // 3) Основной MARKET-ордер (пока без SL/TP — тестируем плечо)
            // ----------------------------------------------------
            var side = signal.Side == SignalSide.Buy ? OrderSide.Buy : OrderSide.Sell;

            var orderResult = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: signal.Symbol,
                side: side,
                type: FuturesOrderType.Market,
                quantity: quantity,
                positionSide: PositionSide.Both,
                reduceOnly: false,
                ct: ct);

            if (!orderResult.Success)
            {
                _logger.LogError(
                    "[ORDER][{Symbol}] MARKET ERROR: {Msg}",
                    signal.Symbol,
                    orderResult.Error?.Message);
                return;
            }

            _logger.LogInformation(
                "[ORDER][{Symbol}] OPENED with lev={Lev}x qty={Qty}",
                signal.Symbol,
                leverage,
                quantity);
        }


    }
}
