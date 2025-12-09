// ============================================================================
// PositionSupervisorService v6.6 (QUANT-REALTIME MAX, SAFE TP/SL + Dynamic Trend Hold)
// - Контроль Long / Short / Both
// - Авто-ремонт SL/TP
// - Многоуровневый трейлинг (ATR + EMA + SuperTrend + micro-structure)
// - Динамический "HOLD" при сильном тренде (не затягиваем SL слишком рано)
// - Безопасная защита от -2021 (order would immediately trigger)
// - Manual + AI позиции (через ManualPositionHandler)
// - QUANT-LEARN: фикс закрытий
// - v6.5: TP = LIMIT reduceOnly, Supervisor видит TP-LIMIT
// ============================================================================

using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using CryptoExchange.Net.Objects;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
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

        private MarketRegime _regimeNow;

        // --------------------------------------------------------------------
        // Внутренний уровень вероятности продолжения тренда
        // --------------------------------------------------------------------
        private enum TrendContinuationLevel
        {
            Low,
            Medium,
            High
        }

        public PositionSupervisorService(
            ILogger<PositionSupervisorService> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            AiStopLossOptimizer slOptimizer,
            AiSelfLearningService aiLearning,
            MarketDataService marketData,
            AiMarketRegimeService regime,
            ManualPositionHandler manualHandler)
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
            if (qty <= 0)
            {
                _logger.LogInformation("[SUPERVISOR] {symbol} {side}: no qty", symbol, side);
                return;
            }

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
            var orders = allOrders.Where(o => o.PositionSide == side || o.PositionSide == PositionSide.Both).ToList();

            var sl = orders.FirstOrDefault(o =>
                o.Side == closeSide &&
                (o.Type == FuturesOrderType.Stop || o.Type == FuturesOrderType.StopMarket));

            // v6.5: TP может быть TakeProfit / TakeProfitMarket / Limit reduceOnly
            var tp = orders.FirstOrDefault(o =>
                o.Side == closeSide &&
                (
                    o.Type == FuturesOrderType.TakeProfit ||
                    o.Type == FuturesOrderType.TakeProfitMarket ||
                    (o.Type == FuturesOrderType.Limit && o.ReduceOnly == true)
                ));

            decimal entry = pos.EntryPrice;
            if (entry <= 0 && signal != null && signal.Symbol == symbol)
                entry = signal.EntryPrice;

            // 1) Missing SL - Creating Emergency SL
            if (sl == null)
            {
                await CreateEmergencySLAsync(client, symbol, side, qty, entry, signal, ct);
                _logger.LogWarning("[SUPERVISOR][{symbol}] SL restored", symbol);
                return;
            }

            // 2) Missing TP - Creating Emergency TP
            if (tp == null)
            {
                await CreateEmergencyTPAsync(client, symbol, side, qty, entry, signal, ct);
                _logger.LogWarning("[SUPERVISOR][{symbol}] TP restored", symbol);
                return;
            }




            // 3) Trailing Logic
            if (klines != null && klines.Count >= 50)
            {
                // NEW: dynamic TP runner mode
                await ManageRunnerTpAsync(
                    client, symbol, side, qty, entry,
                    orders, signal, klines, ct);

                // original trailing SL
                await MultiLayerTrailingAsync(
                    client, symbol, side, qty, entry,
                    signal, orders, klines, ct);
            }

        }

        // =====================================================================
        // EMERGENCY SL
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

            decimal limit = side == PositionSide.Long ? sl - tick : sl + tick;

            var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol,
                side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                FuturesOrderType.Stop,
                qty,
                price: limit,
                stopPrice: sl,
                positionSide: side,
                timeInForce: TimeInForce.GoodTillCanceled,
                ct: ct);

            if (!res.Success)
            {
                _logger.LogError("[SUPERVISOR] ERROR SL create {symbol}: {err}", symbol, res.Error);
                return;
            }

            _logger.LogInformation("[SUPERVISOR] SL CREATED {symbol} sl={sl}", symbol, sl);
        }

        // =====================================================================
        // EMERGENCY TP (v6.5: LIMIT reduceOnly + tickSize)
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

            var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol,
                side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                FuturesOrderType.Limit,
                qty,
                price: trigger,
                positionSide: side,
                reduceOnly: true,
                timeInForce: TimeInForce.GoodTillCanceled,
                ct: ct);

            if (!res.Success)
            {
                _logger.LogError("[SUPERVISOR] ERROR create TP {symbol}: {err}", symbol, res.Error);
                return;
            }

            _logger.LogInformation("[SUPERVISOR] TP CREATED {symbol} tp={tp}", symbol, trigger);
        }


        // =====================================================================
        // RUNNER MODE — TP1 (70%) FIX + RUNNER 30% (TRAIL UNTIL EXIT)
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

            // Ищем существующий TP (старый одинарный или LIMIT reduceOnly)
            var tpOrder = orders.FirstOrDefault(o =>
                o.Side == closeSide &&
                (o.Type == FuturesOrderType.TakeProfit ||
                 o.Type == FuturesOrderType.TakeProfitMarket ||
                 (o.Type == FuturesOrderType.Limit && o.ReduceOnly == true)));

            if (tpOrder == null)
                return;

            // Если TP уже разделён — ничего не делаем
            if (tpOrder.Quantity < qty * 0.99m)
                return;

            decimal qtyTp1 = Math.Round(qty * 0.70m, 8);
            decimal qtyRunner = qty - qtyTp1;

            if (qtyTp1 <= 0 || qtyRunner <= 0)
                return;

            // ЦЕНА TP1 — берём старый TP
            decimal tp1Price = tpOrder?.Price ?? tpOrder?.StopPrice ?? 0m;
            if (tp1Price <= 0)
                return;

            // Отменяем старый TP
            await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, tpOrder?.Id, ct: ct);

            // 1) СОЗДАЕМ TP1 (70%) — фиксируем прибыль
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

            if (!resTp1.Success)
            {
                _logger.LogError("[RUNNER][{symbol}] ERROR creating TP1: {err}", symbol, resTp1.Error);
                return;
            }

            _logger.LogInformation("[RUNNER][{symbol}] TP1 CREATED qty={q} price={p}", symbol, qtyTp1, tp1Price);

            // 2) RUNNER (30%) — убираем TP, работаем только трейлинг-SL
            // Реализовано автоматически: runner не имеет TP, и выйдет по trailing SL
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
                (o.Type == FuturesOrderType.Stop || o.Type == FuturesOrderType.StopMarket));

            if (slOrder == null)
            {
                _logger.LogWarning("[SUPERVISOR] {symbol} {side}: TRAILING skipped — SL not found", symbol, side);
                return;
            }

            var last = klines.Last();
            decimal mark = last.ClosePrice;

            PositionSide realSide = side;
            if (side == PositionSide.Both)
                realSide = mark >= entryPrice ? PositionSide.Long : PositionSide.Short;

            decimal atr = CalculateAtr(klines);
            if (atr <= 0) return;

            // ---- NEW: оценка вероятности продолжения тренда ----
            var contLevel = EvaluateTrendContinuation(realSide, entryPrice, atr, klines);

            if (contLevel == TrendContinuationLevel.High)
            {
                // Сильный тренд, цена уже далеко от входа → не трогаем SL,
                // даём сделке "жить" и не затягиваем стоп слишком рано.
                _logger.LogInformation(
                    "[SUPERVISOR] {symbol} {side}: trend continuation HIGH → trailing HOLD (keep SL as is)",
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

            await UpdateSLAsync(
                client, symbol, realSide, qty,
                slOrder, entryPrice, targetSl,
                signal, ct);
        }

        // ---- Оценка вероятности продолжения тренда (очень лёгкий, безопасный фильтр) ----
        private TrendContinuationLevel EvaluateTrendContinuation(
            PositionSide side,
            decimal entryPrice,
            decimal atr,
            IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            // Если мало данных — обычный трейлинг
            if (klines.Count < 30 || atr <= 0)
                return TrendContinuationLevel.Medium;

            var last = klines[^1];

            // Берём цену N свечей назад для оценки импульса
            int lookback = Math.Min(20, klines.Count - 1);
            var past = klines[^lookback];

            if (past.ClosePrice <= 0)
                return TrendContinuationLevel.Medium;

            var movePct = (last.ClosePrice - past.ClosePrice) / past.ClosePrice;
            var rr = Math.Abs(last.ClosePrice - entryPrice) / atr; // сколько ATR цена прошла от входа

            // Базовые пороги:
            // rr >= 1.5  → цена прошла уже достаточно далеко от входа
            // movePct ≥ 1–1.5 % за последние ~20 минут → сильный импульс
            if (side == PositionSide.Long)
            {
                if (rr >= 1.5m && movePct >= 0.015m)
                    return TrendContinuationLevel.High;

                if (rr >= 0.8m && movePct >= 0.0075m)
                    return TrendContinuationLevel.Medium;

                return TrendContinuationLevel.Low;
            }
            else // Short
            {
                if (rr >= 1.5m && movePct <= -0.015m)
                    return TrendContinuationLevel.High;

                if (rr >= 0.8m && movePct <= -0.0075m)
                    return TrendContinuationLevel.Medium;

                return TrendContinuationLevel.Low;
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
        // UPDATE SL + LEARNING HOOK (без изменений)
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

            decimal oldSl = slOrder.StopPrice ?? slOrder?.Price ?? 0m;
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

            decimal limit = side == PositionSide.Long ? s - tick : s + tick;

            await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, slOrder.Id, ct: ct);

            var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol,
                side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                FuturesOrderType.Stop,
                qty,
                price: limit,
                stopPrice: s,
                positionSide: side,
                timeInForce: TimeInForce.GoodTillCanceled,
                ct: ct);

            if (!res.Success)
            {
                _logger.LogError("[SUPERVISOR] ERROR update SL {symbol}: {err}", symbol, res.Error);
                return;
            }

            _logger.LogInformation("[SUPERVISOR] TRAIL SL UPDATED {symbol} {old} → {ns}", symbol, oldSl, s);

            // -------- LEARNING HOOK ----------
            if (signal != null && signal.IsManual)
                return;

            bool win = side == PositionSide.Long ? s > entry : s < entry;
            var sigSide = side == PositionSide.Short ? SignalSide.Sell : SignalSide.Buy;

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
            catch { }

            return fallback > 0 ? fallback : 0m;
        }
    }
}
