using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class OrderExecutor
    {
        private readonly ILogger<OrderExecutor> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly SymbolInfoService _symbolInfo;
        private readonly TradeResultMonitorService _tradeMonitor;

        public OrderExecutor(
            ILogger<OrderExecutor> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            TradeResultMonitorService tradeMonitor)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
            _tradeMonitor = tradeMonitor;
        }

        private decimal RoundToTick(decimal price, decimal tick)
        {
            if (tick <= 0) return price;
            return Math.Round(price / tick) * tick;
        }

        public async Task PlaceOrderAsync(TradeSignal signal, decimal quantity, CancellationToken ct = default)
        {
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

            // --- пороги для "старого" сигнала
            const decimal maxLateEntryPct = 0.35m;   // если ушло дальше 0.35% — сигнал уже старый
            const decimal superSignalMarketDiff = 0.03m; // для SUPER-сигната >0.03% — чистый маркет

            // если обычный сигнал и рынок уже улетел далеко — пропускаем
            if (signal.IsSuperSignal != true && rawDiffPct > maxLateEntryPct)
            {
                _logger.LogWarning(
                    "[ENTRY] Skip {symbol}: diff={diff:F4}% > {maxDiff:F2}% (late signal)",
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

            // Консольный репорт заполним после вычисления стартового entry
            decimal initialEntry = RoundToTick(signal.EntryPrice, tick);

            // =========================================================
            // 4) EXECUTION LOOP (умный вход v4.3)
            // =========================================================
            const int maxAttempts = 7;
            const decimal qtyStepFactor = 0.80m;          // при ошибках маржи режем объём до 80%
            const decimal chaseMaxSlippagePct = 0.20m;    // максимум 0.20 % от исходного entry
            const decimal chaseRecenterTicks = 2m;        // смещение на несколько тиков вокруг mark
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

                // --- обновляем MARK каждый цикл, чтобы не входить "вслепую"
                markResult = await client.UsdFuturesApi.ExchangeData.GetMarkPriceAsync(signal.Symbol);
                if (markResult.Success)
                    mark = markResult.Data.MarkPrice;

                // --- считаем отклонение от ИСХОДНОГО уровня входа сигнала
                decimal totalDiffPct = Math.Abs(mark - signal.EntryPrice) / mark * 100m;

                if (!isSuper && totalDiffPct > maxLateEntryPct)
                {
                    ConsoleReportFormatter.EntryFailedHard(
                        _logger,
                        signal.Symbol,
                        $"late entry: diff={totalDiffPct:F4}% > {maxLateEntryPct:F2}%");
                    return;
                }

                // =====================================================
                // ВЫБОР ФАКТИЧЕСКОГО ENTRY
                // =====================================================
                decimal entry;

                if (isMarketEntry)
                {
                    // SUPER-SIGNAL: чистый MARKET, только контролируем notional
                    entry = mark;
                }
                else
                {
                    // Умный лимитник: если цена ещё не дала наш уровень — ждём.
                    // Если уже проскочила за уровень, но в пределах chaseMaxSlippagePct —
                    // ставим лимит ближе к mark, чтобы не "улетал" без нас.
                    bool wantShort = side == OrderSide.Sell;

                    if (wantShort)
                    {
                        if (mark >= signal.EntryPrice)
                        {
                            // цена ещё выше/рядом — ждём лимитом у уровня сигнала
                            entry = RoundToTick(signal.EntryPrice, tick);
                        }
                        else
                        {
                            // цена уже ниже нашего уровня → проскальзывание
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
                        // LONG
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

                // --- проверка minNotional под новый entry
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

                // --- типичные проблемы маржи / фильтров → просто уменьшаем объём и пробуем снова
                if (code is -2019 or -4164 or -1013 or -1111)
                {
                    qty = Math.Floor((qty * qtyStepFactor) / step) * step;

                    if (attempt < maxAttempts)
                        await Task.Delay(chaseDelayMs, ct);

                    continue;
                }

                // остальные ошибки — не пытаемся бесконечно биться
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
            // 6) STOP LOSS
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

            if (!slOrder.Success)
                _logger.LogError("❌ SL ERROR {Symbol}: {Error}", signal.Symbol, slOrder.Error);
            else
                ConsoleReportFormatter.SLPlaced(_logger, slTrig, slLimit, openedQty);

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
        }
    }
}
