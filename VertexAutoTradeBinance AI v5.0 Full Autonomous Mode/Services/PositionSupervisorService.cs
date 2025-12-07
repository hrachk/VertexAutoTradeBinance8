// ============================================================================
// PositionSupervisorService v6.0 (QUANT-REALTIME MAX)
// - Реальный контроль позиций (Long / Short / Both)
// - Авто-ремонт SL/TP
// - Многоуровневый трейлинг (ATR + EMA + SuperTrend + micro-structure)
// - Безопасная защита от -2021 (order would immediately trigger)
// - Manual + AI позиции (через ManualPositionHandler)
// - QUANT-LEARN: запись сделок в AiSelfLearningService.RecordTrade(...)
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

        // Текущий режим, который мы считаем при записи обучающих трейдов
        private MarketRegime _regimeNow;

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
        // MAIN ENTRY: главный супервизор по символу
        // =====================================================================
        public async Task SuperviseAsync(string symbol, TradeSignal? lastSignal, CancellationToken ct)
        {
            using var client = _factory.CreateRestClient();

            // 0) Попытка авто-детекта ручной позиции, если сигнала нет
            if (lastSignal == null)
            {
                var manualSignal = await _manualHandler.DetectManualAsync(client, symbol, ct);
                if (manualSignal != null)
                {
                    lastSignal = manualSignal;
                    _logger.LogWarning(
                        "[MANUAL][{symbol}] Virtual signal injected → SL/TP control enabled",
                        symbol);
                }
            }

            // 1) Позиции с ретраем (чтобы маркет успел стать позицией)
            var posInfo = await GetPositionsWithRetryAsync(client, symbol, ct);
            if (posInfo == null || !posInfo.Success || posInfo.Data == null)
            {
                _logger.LogWarning("[SUPERVISOR] No positions loaded for {symbol}", symbol);
                return;
            }

            var longPos = posInfo.Data.FirstOrDefault(p => p.PositionSide == PositionSide.Long);
            var shortPos = posInfo.Data.FirstOrDefault(p => p.PositionSide == PositionSide.Short);

            var hasLong = longPos != null && longPos.Quantity != 0m;
            var hasShort = shortPos != null && shortPos.Quantity != 0m;

            if (!hasLong && !hasShort)
            {
                _logger.LogInformation("[SUPERVISOR] {symbol}: no positions (Long/Short are 0)", symbol);
                return;
            }

            // 2) Открытые ордера (TP/SL)
            var openOrders = await LoadOrdersAsync(client, symbol);

            // 3) Загружаем M1-клайны для трейлинга + режим рынка
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
                _logger.LogWarning(ex, "[SUPERVISOR] Error loading klines for {symbol}", symbol);
            }

            // 4) Обрабатываем Long / Short отдельными ветками
            if (hasLong)
            {
                await HandleSideAsync(
                    client,
                    symbol,
                    PositionSide.Long,
                    longPos!,
                    openOrders,
                    lastSignal,
                    klines1m,
                    ct);
            }

            if (hasShort)
            {
                await HandleSideAsync(
                    client,
                    symbol,
                    PositionSide.Short,
                    shortPos!,
                    openOrders,
                    lastSignal,
                    klines1m,
                    ct);
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

                var res = await client.UsdFuturesApi.Account
                    .GetPositionInformationAsync(symbol, null, ct);
                last = res;

                if (res.Success && res.Data != null)
                {
                    var hasAny = res.Data.Any(x => x.Quantity != 0m);
                    if (hasAny)
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
            return res.Success && res.Data != null
                ? res.Data.ToList()
                : new List<BinanceUsdFuturesOrder>();
        }

        // =====================================================================
        // HANDLE SIDE: логика для отдельной стороны (Long / Short)
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
                _logger.LogInformation("[SUPERVISOR] {symbol} {side}: no position", symbol, side);
                return;
            }

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var orders = allOrders
                .Where(o => o.PositionSide == side || o.PositionSide == PositionSide.Both)
                .ToList();

            // SL
            var sl = orders.FirstOrDefault(o =>
                o.Side == closeSide &&
                (o.Type == FuturesOrderType.Stop || o.Type == FuturesOrderType.StopMarket));

            // TP (любой первый TP)
            var tp = orders.FirstOrDefault(o =>
                o.Side == closeSide &&
                (o.Type == FuturesOrderType.TakeProfit || o.Type == FuturesOrderType.TakeProfitMarket));

            // Entry price для трайлинга / BOTH-логики
            decimal entry = pos.EntryPrice;
            if (entry <= 0 && signal != null && signal.Symbol == symbol)
                entry = signal.EntryPrice;

            // 1) Если нет SL → создаём аварийный
            if (sl == null)
            {
                await CreateEmergencySLAsync(client, symbol, side, qty, entry, signal, ct);
                _logger.LogWarning("[SUPERVISOR][{symbol}] SL восстановлен (user removed / not placed)", symbol);
                // Дальше в этот тик не лезем — подождём следующий проход
                return;
            }

            // 2) Если нет TP → создаём базовый (по TP1)
            if (tp == null)
            {
                await CreateEmergencyTPAsync(client, symbol, side, qty, entry, signal, ct);
                _logger.LogWarning("[SUPERVISOR][{symbol}] TP восстановлен (user removed / not placed)", symbol);
                return;
            }

            // 3) MULTI-LAYER TRAILING (если есть klines)
            if (klines != null && klines.Count >= 50)
            {
                await MultiLayerTrailingAsync(
                    client,
                    symbol,
                    side,
                    qty,
                    entry,
                    signal,
                    orders,
                    klines,
                    ct);
            }

            // =======================================================
            // 10) DETECT CLOSE → AI LEARNING (QUANT-REALTIME)
            // =======================================================
            try
            {
                var posNow = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, null, ct);
                if (posNow.Success && posNow.Data != null)
                {
                    var lp2 = posNow.Data.FirstOrDefault(p => p.PositionSide == PositionSide.Long);
                    var sp2 = posNow.Data.FirstOrDefault(p => p.PositionSide == PositionSide.Short);

                    decimal qLongNow = lp2 != null ? Math.Abs(lp2.Quantity) : 0m;
                    decimal qShortNow = sp2 != null ? Math.Abs(sp2.Quantity) : 0m;

                    // позиция БЫЛА (qty) → теперь стала 0 → сделка ЗАКРЫТА
                    if (qty > 0 && qLongNow == 0 && qShortNow == 0)
                    {
                        decimal exitPrice =
                            lp2?.MarkPrice > 0 ? lp2.MarkPrice :
                            sp2?.MarkPrice > 0 ? sp2.MarkPrice :
                            0m;

                        if (exitPrice > 0m)
                        {
                            var learnedSide = side == PositionSide.Short
                                ? SignalSide.Sell
                                : SignalSide.Buy;

                            _aiLearning.RecordTrade(
                                symbol,
                                learnedSide,
                                entry,
                                exitPrice,
                                _regimeNow);

                            _logger.LogInformation(
                                "[AI-LEARN][{symbol}] TRADE CLOSED → entry={entry}, exit={exit}, side={side}, regime={reg}",
                                symbol, entry, exitPrice, learnedSide, _regimeNow);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AI-LEARN] Close detection error for {symbol}", symbol);
            }

        }

        // =====================================================================
        // EMERGENCY SL / TP
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
            if (qty <= 0)
                return;

            decimal rawSl;

            if (signal != null && signal.StopLoss > 0)
            {
                rawSl = signal.StopLoss;
            }
            else
            {
                // Если нет SL в сигнале — ставим базовый отступ по ATR
                try
                {
                    var kl = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 100);
                    if (kl.Count < 30)
                        return;

                    var atr = _marketData.CalculateAtr(kl, 14);
                    if (atr <= 0)
                        return;

                    rawSl = side == PositionSide.Long
                        ? entryPrice - atr * 1.2m
                        : entryPrice + atr * 1.2m;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[SUPERVISOR] Emergency SL ATR calc failed for {symbol}", symbol);
                    return;
                }
            }

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            var tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            // поджимаем под тик
            decimal sl = Math.Round(rawSl / tick) * tick;

            // Берём текущую цену, чтобы не получить -2021
            decimal mark = await GetMarkPriceSafeAsync(client, symbol, entryPrice, ct);

            if (mark > 0)
            {
                if (side == PositionSide.Long && sl >= mark)
                    sl = mark - tick;
                if (side == PositionSide.Short && sl <= mark)
                    sl = mark + tick;
            }

            // маленькая защита — если всё равно пересекаем цену, не ставим
            if (mark > 0)
            {
                if (side == PositionSide.Long && sl >= mark)
                {
                    _logger.LogWarning("[SUPERVISOR] Skip SL create for {symbol}: sl {sl} >= mark {mark}", symbol, sl, mark);
                    return;
                }
                if (side == PositionSide.Short && sl <= mark)
                {
                    _logger.LogWarning("[SUPERVISOR] Skip SL create for {symbol}: sl {sl} <= mark {mark}", symbol, sl, mark);
                    return;
                }
            }

            decimal limit = side == PositionSide.Long ? sl - tick : sl + tick;

            var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                type: FuturesOrderType.Stop,
                quantity: qty,
                price: limit,
                stopPrice: sl,
                positionSide: side,
                timeInForce: TimeInForce.GoodTillCanceled,
                ct: ct);

            if (!res.Success)
            {
                _logger.LogError("[SUPERVISOR] ERROR create SL for {symbol}: {err}", symbol, res.Error);
                return;
            }

            _logger.LogInformation("[SUPERVISOR] SL CREATED {symbol} sl={sl}", symbol, sl);
        }

        private async Task CreateEmergencyTPAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entryPrice,
            TradeSignal? signal,
            CancellationToken ct)
        {
            if (qty <= 0)
                return;

            decimal trigger;

            if (signal != null && signal.TakeProfits != null && signal.TakeProfits.Count > 0)
            {
                trigger = signal.TakeProfits[0];
            }
            else
            {
                try
                {
                    var kl = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 100);
                    if (kl.Count < 30)
                        return;

                    var atr = _marketData.CalculateAtr(kl, 14);
                    if (atr <= 0)
                        return;

                    trigger = side == PositionSide.Long
                        ? entryPrice + atr * 1.5m
                        : entryPrice - atr * 1.5m;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[SUPERVISOR] Emergency TP ATR calc failed for {symbol}", symbol);
                    return;
                }
            }

            var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                type: FuturesOrderType.TakeProfitMarket,
                quantity: qty,
                stopPrice: trigger,
                positionSide: side,
                reduceOnly: true,
                ct: ct);

            if (!res.Success)
            {
                _logger.LogError("[SUPERVISOR] ERROR create TP for {symbol}: {err}", symbol, res.Error);
                return;
            }

            _logger.LogInformation("[SUPERVISOR] TP CREATED {symbol} tp={tp}", symbol, trigger);
        }

        // =====================================================================
        // MULTI-LAYER TRAILING
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
                _logger.LogWarning("[SUPERVISOR] {symbol} {side}: TRAILING skipped — SL order not found", symbol, side);
                return;
            }

            var last = klines.Last();
            decimal mark = last.ClosePrice;

            // BOTH → решаем динамически, в какую сторону трейлить
            PositionSide realSide = side;
            if (side == PositionSide.Both)
            {
                realSide = mark >= entryPrice ? PositionSide.Long : PositionSide.Short;
            }

            decimal atr = CalculateAtr(klines);
            if (atr <= 0)
                return;

            decimal ema21 = CalculateEma(klines, 21);
            decimal st = SuperTrend(klines, atr);

            // 1) ATR Trailing
            decimal slAtr = realSide == PositionSide.Long
                ? mark - atr * 0.9m
                : mark + atr * 0.9m;

            // 2) EMA21 Trailing
            decimal slEma = realSide == PositionSide.Long
                ? ema21 - atr * 0.3m
                : ema21 + atr * 0.3m;

            // 3) SuperTrend Trailing
            decimal slSt = st;

            // 4) MicroTrail (микро-поджатие)
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
                client,
                symbol,
                realSide,
                qty,
                slOrder,
                entryPrice,
                targetSl,
                signal,
                ct);
        }

        private decimal CalculateAtr(IReadOnlyList<BinanceFuturesUsdtKline> kl)
        {
            if (kl.Count < 16)
                return 0;

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
            if (kl.Count < period + 1)
                return 0;

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
            return (last.HighPrice + last.LowPrice) / 2 + (atr * 1.5m);
        }

        // =====================================================================
        // UPDATE SL (с защитой от -2021) + QUANT LEARNING HOOK
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
            if (qty <= 0 || newSl <= 0)
                return;

            decimal oldSl = slOrder.StopPrice ?? slOrder?.Price ?? 0m;
            if (oldSl <= 0)
                return;

            // Не ухудшаем SL
            if (side == PositionSide.Long && newSl <= oldSl) return;
            if (side == PositionSide.Short && newSl >= oldSl) return;

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            decimal tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            decimal s = Math.Round(newSl / tick) * tick;

            // Безопасный mark для фикса -2021
            decimal mark = await GetMarkPriceSafeAsync(client, symbol, entry, ct);

            if (mark > 0)
            {
                if (side == PositionSide.Long && s >= mark)
                    s = mark - tick;
                if (side == PositionSide.Short && s <= mark)
                    s = mark + tick;

                // если после правки всё равно задеваем цену — не трогаем
                if (side == PositionSide.Long && s >= mark)
                {
                    _logger.LogWarning("[SUPERVISOR] TRAIL SKIP {symbol}: new SL {s} >= mark {mark}", symbol, s, mark);
                    return;
                }
                if (side == PositionSide.Short && s <= mark)
                {
                    _logger.LogWarning("[SUPERVISOR] TRAIL SKIP {symbol}: new SL {s} <= mark {mark}", symbol, s, mark);
                    return;
                }
            }

            decimal limit = side == PositionSide.Long ? s - tick : s + tick;

            // Сначала отменяем старый SL
            await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, slOrder.Id, ct: ct);

            var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                type: FuturesOrderType.Stop,
                quantity: qty,
                stopPrice: s,
                price: limit,
                positionSide: side,
                timeInForce: TimeInForce.GoodTillCanceled,
                ct: ct);

            if (!res.Success)
            {
                _logger.LogError("[SUPERVISOR] ERROR update SL for {symbol}: {err}", symbol, res.Error);
                return;
            }

            _logger.LogInformation("[SUPERVISOR] TRAIL SL UPDATED {symbol} {old} → {ns}", symbol, oldSl, s);

            // --- QUANT REALTIME LEARNING HOOK --------------------------------
            // Если это ручная сделка — можно не учить модель
            if (signal != null && signal.IsManual)
            {
                _logger.LogInformation("[AI-LEARN] Manual trade → skip learning for {symbol}", symbol);
                return;
            }

            // Вычисляем win/lose по направлению
            bool win = side == PositionSide.Long ? s > entry : s < entry;
            var sigSide = side == PositionSide.Short ? SignalSide.Sell : SignalSide.Buy;

            _aiLearning.RecordTrade(
                symbol: symbol,
                side: sigSide,
                entry: entry,
                exit: s,
                regime: _regimeNow);
        }

        // =====================================================================
        // MARK PRICE HELPER
        // =====================================================================
        private static async Task<decimal> GetMarkPriceSafeAsync(
            BinanceRestClient client,
            string symbol,
            decimal fallback,
            CancellationToken ct)
        {
            try
            {
                var res = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(symbol, ct: ct);
                if (res.Success && res.Data != null && res.Data.Price > 0)
                    return res.Data.Price;
            }
            catch
            {
                // ignore
            }

            return fallback > 0 ? fallback : 0m;
        }
    }
}
