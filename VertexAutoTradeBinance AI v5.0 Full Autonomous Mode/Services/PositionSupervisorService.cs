// ==================================================================================
// PositionSupervisorService — REAL-CONTEXT SUPERVISOR (Binance.Net 11.11.0)
// ==================================================================================

using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using CryptoExchange.Net.Objects;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Контролирует реальные позиции: делает реконструкцию контекста по рынку,
    /// следит за SL/TP, восстанавливает их при удалении и двигает трейлинг.
    /// Работает и для позиций бота, и для ручных входов.
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

        private MarketRegime _regimeNow = MarketRegime.Range;

        /// <summary>
        /// Объект реального контекста вместо виртуального TradeSignal
        /// </summary>
        private sealed record RealContextSignal(
            string Symbol,
            PositionSide Side,
            decimal EntryPrice,
            decimal StopLoss,
            List<decimal> TakeProfits,
            decimal Atr,
            MarketRegime Regime,
            decimal TrendSlope,
            bool Manipulation,
            decimal CurrentPrice
        );

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
        }

        // ======================================================================
        // MAIN SUPERVISE
        // ======================================================================
        public async Task SuperviseAsync(string symbol, TradeSignal? lastSignal, CancellationToken ct)
        {
            using var client = _factory.CreateRestClient();

            // -------------------------------------------------------------
            // 0) Достаем фактические позиции с ретраями
            // -------------------------------------------------------------
            var posInfo = await GetPositionsWithRetryAsync(client, symbol, ct);
            if (posInfo == null || !posInfo.Success || posInfo.Data == null)
            {
                _logger.LogWarning("[SUPERVISOR] No positions loaded for {symbol}", symbol);
                return;
            }

            var longPos = posInfo.Data.FirstOrDefault(p => p.PositionSide == PositionSide.Long);
            var shortPos = posInfo.Data.FirstOrDefault(p => p.PositionSide == PositionSide.Short);
 
            // NEW: SUPPORT FOR ONE-WAY MODE (PositionSide.BOTH)

            var bothPos = posInfo.Data.FirstOrDefault(p => p.PositionSide == PositionSide.Both);

            if (bothPos != null && bothPos.Quantity != 0m)
            {
                if (bothPos.Quantity > 0)
                    longPos = bothPos;     // treat BOTH as LONG
                else if (bothPos.Quantity < 0)
                    shortPos = bothPos;    // treat BOTH as SHORT
            }

            bool hasLong = longPos != null && longPos.Quantity != 0m;
            bool hasShort = shortPos != null && shortPos.Quantity != 0m;

            if (!hasLong && !hasShort)
            {
                _logger.LogInformation("[SUPERVISOR] {symbol}: no open positions", symbol);
                return;
            }

            // -------------------------------------------------------------
            // 1) Если сигнал отсутствует, пробуем детектить ручной вход
            // (НО НЕ СОЗДАЁМ ВИРТУАЛЬНЫЙ TRADE-SIGNAL ДЛЯ ЛОГИКИ!)
            // -------------------------------------------------------------
            if (lastSignal == null)
            {
                var manualDetected = await _manualHandler.DetectManualAsync(client, symbol, ct);
                if (manualDetected != null)
                {
                    lastSignal = manualDetected;
                    _logger.LogWarning(
                        "[MANUAL][{symbol}] Обнаружен ручной вход. Используем данные только как hint для контекста.",
                        symbol);
                }
            }

            // -------------------------------------------------------------
            // 2) Загружаем открытые ордера
            // -------------------------------------------------------------
            var openOrders = await LoadOrders(client, symbol);

            // -------------------------------------------------------------
            // 3) Загружаем M1 свечи (можно 160 штук)
            // -------------------------------------------------------------
            IReadOnlyList<BinanceFuturesUsdtKline>? klines1m = null;
            try
            {
                klines1m = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 160);
                var regimeInfo = _regime.DetectRegime(symbol, KlineInterval.OneMinute, klines1m);
                if (regimeInfo != null)
                    _regimeNow = regimeInfo.Regime;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SUPERVISOR] Error loading 1m klines for {symbol}", symbol);
            }

            // -------------------------------------------------------------
            // 4) Обрабатываем LONG и SHORT отдельно с Real-Context
            // -------------------------------------------------------------
            if (hasLong)
            {
                var ctx = await BuildRealContextAsync(
                    symbol,
                    PositionSide.Long,
                    longPos!,
                    lastSignal,
                    klines1m,
                    ct);

                await HandleSideAsync(client, symbol, PositionSide.Long, longPos!, openOrders, ctx, klines1m, ct);
            }

            if (hasShort)
            {
                var ctx = await BuildRealContextAsync(
                    symbol,
                    PositionSide.Short,
                    shortPos!,
                    lastSignal,
                    klines1m,
                    ct);

                await HandleSideAsync(client, symbol, PositionSide.Short, shortPos!, openOrders, ctx, klines1m, ct);
            }
        }

        // ======================================================================
        // REAL CONTEXT BUILDER
        // ======================================================================
        private async Task<RealContextSignal> BuildRealContextAsync(
            string symbol,
            PositionSide side,
            BinancePositionDetailsUsdt pos,
            TradeSignal? signalHint,
            IReadOnlyList<BinanceFuturesUsdtKline>? klines1m,
            CancellationToken ct)
        {
            decimal entry = pos.EntryPrice;
            if (entry <= 0 && klines1m != null && klines1m.Count > 0)
                entry = klines1m.Last().ClosePrice;

            decimal currentPrice =
                klines1m != null && klines1m.Count > 0
                    ? klines1m.Last().ClosePrice
                    : entry;

            // --- ATR по M1 ---
            decimal atr = 0.0m;
            if (klines1m != null && klines1m.Count > 20)
                atr = CalculateAtr(klines1m);

            if (atr <= 0)
                atr = Math.Max(entry * 0.002m, 0.5m); // fallback: 0.2% или 0.5$

            // --- Режим рынка уже посчитан в SuperviseAsync ---
            var regime = _regimeNow;

            // --- Наклон (тренд) по последним 20 свечам ---
            decimal trendSlope = 0m;
            if (klines1m != null && klines1m.Count > 20)
            {
                var last20 = klines1m.Skip(Math.Max(0, klines1m.Count - 20)).ToList();
                var first = last20.First().ClosePrice;
                var last = last20.Last().ClosePrice;
                if (first > 0)
                    trendSlope = (last - first) / first * 100m;
            }

            // --- Простейший флаг манипуляции: длинные хвосты / резкие шпильки ---
            bool manipulation = false;
            if (klines1m != null && klines1m.Count > 10)
            {
                var last = klines1m.Last();
                var body = Math.Abs(last.ClosePrice - last.OpenPrice);
                var range = last.HighPrice - last.LowPrice;
                if (range > 0 && body / range < 0.2m && range > atr * 1.5m)
                    manipulation = true;
            }

            // --- StopLoss / TP: берём из сигнала, если есть, иначе строим от ATR ---
            decimal sl;
            var tps = new List<decimal>();

            if (signalHint != null && signalHint.Symbol == symbol && signalHint.StopLoss > 0)
            {
                sl = signalHint.StopLoss;
                if (signalHint.TakeProfits != null && signalHint.TakeProfits.Count > 0)
                    tps.AddRange(signalHint.TakeProfits);
            }
            else
            {
                // Emergency контекст без сигнала:
                // SL = 2 * ATR от входа, TP1 = 2.5*ATR, TP2 = 3.5*ATR
                if (side == PositionSide.Long)
                {
                    sl = entry - atr * 2m;
                    tps.Add(entry + atr * 2.5m);
                    tps.Add(entry + atr * 3.5m);
                }
                else
                {
                    sl = entry + atr * 2m;
                    tps.Add(entry - atr * 2.5m);
                    tps.Add(entry - atr * 3.5m);
                }
            }

            // Нормализуем TP: не даём им быть "по другую сторону" SL
            tps = tps
                .Where(tp => side == PositionSide.Long ? tp > entry : tp < entry)
                .OrderBy(tp => tp * (side == PositionSide.Long ? 1 : -1))
                .ToList();

            if (tps.Count == 0)
            {
                if (side == PositionSide.Long)
                    tps.Add(entry + atr * 3m);
                else
                    tps.Add(entry - atr * 3m);
            }

            // Фильтры рынка для округления SL/TP
            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            decimal tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            sl = Math.Round(sl / tick) * tick;
            for (int i = 0; i < tps.Count; i++)
                tps[i] = Math.Round(tps[i] / tick) * tick;

            return new RealContextSignal(
                Symbol: symbol,
                Side: side,
                EntryPrice: entry,
                StopLoss: sl,
                TakeProfits: tps,
                Atr: atr,
                Regime: regime,
                TrendSlope: trendSlope,
                Manipulation: manipulation,
                CurrentPrice: currentPrice);
        }

        // ======================================================================
        // RETRY POSITIONS — CORRECT FOR BinancePositionDetailsUsdt
        // ======================================================================
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

                var res = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol);
                last = res;

                if (res.Success && res.Data != null)
                {
                    var longPos = res.Data.FirstOrDefault(x => x.PositionSide == PositionSide.Long);
                    var shortPos = res.Data.FirstOrDefault(x => x.PositionSide == PositionSide.Short);

                    if ((longPos != null && longPos.Quantity != 0m) ||
                        (shortPos != null && shortPos.Quantity != 0m))
                    {
                        return res;
                    }
                }

                await Task.Delay(delay, ct);
            }

            return last;
        }

        private async Task<List<BinanceUsdFuturesOrder>> LoadOrders(BinanceRestClient client, string symbol)
        {
            var res = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol);
            return res.Success && res.Data != null
                ? res.Data.ToList()
                : new List<BinanceUsdFuturesOrder>();
        }

        // ======================================================================
        // PROCESS SIDE (LONG / SHORT)
        // ======================================================================
        private async Task HandleSideAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            BinancePositionDetailsUsdt pos,
            List<BinanceUsdFuturesOrder> allOrders,
            RealContextSignal ctx,
            IReadOnlyList<BinanceFuturesUsdtKline>? klines,
            CancellationToken ct)
        {
            decimal qty = Math.Abs(pos.Quantity);

            // FIX: BOTH-POSITION
            PositionSide realSide = pos.PositionSide;
            if (pos.PositionSide == PositionSide.Both)
            {
                realSide = pos.Quantity > 0 ? PositionSide.Long : PositionSide.Short;
            }


            if (qty <= 0)
            {
                _logger.LogInformation("[SUPERVISOR] {symbol} {side}: no position", symbol, side);
                return;
            }

            //var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

           // var orders = allOrders.Where(o => o.PositionSide == side).ToList();


            var closeSide = realSide == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var orders = allOrders.Where(o => o.PositionSide == realSide || o.PositionSide == PositionSide.Both).ToList();


            var slOrder = orders.FirstOrDefault(o =>
                o.Side == closeSide &&
                (o.Type == FuturesOrderType.Stop || o.Type == FuturesOrderType.StopMarket));

            var tpOrder = orders.FirstOrDefault(o =>
                o.Side == closeSide &&
                (o.Type == FuturesOrderType.TakeProfit || o.Type == FuturesOrderType.TakeProfitMarket));

            // 1) Нет SL → создаём аварийный на базе реального контекста
            if (slOrder == null)
            {
                slOrder = await CreateEmergencySL(client, symbol, side, qty, ctx);
                _logger.LogWarning("[SUPERVISOR][{symbol}] SL восстановлен (user removed / not placed)", symbol);
                if (slOrder == null)
                    return;
            }

            // 2) Нет TP → создаём разумный TP по контексту
            if (tpOrder == null)
            {
                tpOrder = await CreateEmergencyTP(client, symbol, side, qty, ctx);
                _logger.LogWarning("[SUPERVISOR][{symbol}] TP восстановлен (user removed / not placed)", symbol);
            }

            // 3) Только теперь — TRAILING SL
            await MultiLayerTrailing(client, symbol, side, qty, ctx, slOrder, orders, klines, ct);
        }

        // ======================================================================
        // EMERGENCY SL / TP
        // ======================================================================
        private async Task<BinanceUsdFuturesOrder?> CreateEmergencySL(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            RealContextSignal ctx)
        {
            if (qty <= 0)
                return null;

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            decimal tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            decimal sl = Math.Round(ctx.StopLoss / tick) * tick;
            decimal limit = side == PositionSide.Long ? sl - tick : sl + tick;

            var orderResult = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                type: FuturesOrderType.Stop,
                quantity: qty,
                price: limit,
                positionSide: side,
                timeInForce: TimeInForce.GoodTillCanceled,
                stopPrice: sl);

            if (!orderResult.Success || orderResult.Data == null)
            {
                _logger.LogError("[SUPERVISOR] ERROR creating SL for {symbol}: {err}", symbol, orderResult.Error);
                return null;
            }

            _logger.LogInformation("[SUPERVISOR] SL CREATED {symbol} sl={sl}", symbol, sl);
            return orderResult.Data;
        }

        private async Task<BinanceUsdFuturesOrder?> CreateEmergencyTP(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            RealContextSignal ctx)
        {
            if (qty <= 0 || ctx.TakeProfits.Count == 0)
                return null;

            decimal trigger = ctx.TakeProfits.First();

            var orderResult = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                type: FuturesOrderType.TakeProfitMarket,
                quantity: qty,
                positionSide: side,
                stopPrice: trigger,
                timeInForce: TimeInForce.GoodTillCanceled);

            if (!orderResult.Success || orderResult.Data == null)
            {
                _logger.LogError("[SUPERVISOR] ERROR creating TP for {symbol}: {err}", symbol, orderResult.Error);
                return null;
            }

            _logger.LogInformation("[SUPERVISOR] TP CREATED {symbol} tp={tp}", symbol, trigger);
            return orderResult.Data;
        }

        // ======================================================================
        // MULTI-LAYER TRAILING (на основе реального контекста)
        // ======================================================================
        private async Task MultiLayerTrailing(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            RealContextSignal ctx,
            BinanceUsdFuturesOrder slOrder,
            List<BinanceUsdFuturesOrder> allOrders,
            IReadOnlyList<BinanceFuturesUsdtKline>? klines,
            CancellationToken ct)
        {
            // ============================================================
            //   FIX BOTH → REAL SIDE
            // ============================================================
            PositionSide realSide = side;
            if (side == PositionSide.Both)
            {
                realSide = klines.Last().ClosePrice >= ctx.EntryPrice
                    ? PositionSide.Long
                    : PositionSide.Short;

                _logger.LogInformation(
                    "[SUPERVISOR][{symbol}] BOTH detected → resolved as {side}",
                    symbol, realSide);
            }

            if (klines == null || klines.Count < 50 || qty <= 0)
                return;

            decimal mark = klines.Last().ClosePrice;

            // ATR / EMA / SuperTrend / MicroTrail
            decimal atr = ctx.Atr > 0 ? ctx.Atr : CalculateAtr(klines);
            decimal ema21 = CalculateEma(klines, 21);
            decimal st = SuperTrend(klines, atr);

            var last = klines.Last();
            var prev = klines[klines.Count - 2];

            bool microUp = last.LowPrice > prev.LowPrice;
            bool microDn = last.HighPrice < prev.HighPrice;

            decimal slAtr = side == PositionSide.Long
                ? mark - atr * 0.9m
                : mark + atr * 0.9m;

            decimal slEma = side == PositionSide.Long
                ? ema21 - atr * 0.3m
                : ema21 + atr * 0.3m;

            decimal slSt = st;

            decimal slMicro = side == PositionSide.Long
                ? (microUp ? last.LowPrice - atr * 0.2m : last.LowPrice - atr * 0.6m)
                : (microDn ? last.HighPrice + atr * 0.2m : last.HighPrice + atr * 0.6m);

            decimal targetSl = side switch
            {
                PositionSide.Long => new[] { slAtr, slEma, slSt, slMicro, ctx.StopLoss }.Max(),
                PositionSide.Short => new[] { slAtr, slEma, slSt, slMicro, ctx.StopLoss }.Min(),
                _ => ctx.StopLoss
            };

            await UpdateSL(client, symbol, side, qty, slOrder, ctx.EntryPrice, targetSl, ctx);
        }

        private decimal CalculateAtr(IReadOnlyList<BinanceFuturesUsdtKline> kl)
        {
            decimal atr = 0;
            int start = Math.Max(1, kl.Count - 15); // чтобы i-1 был валиден
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
            decimal k = 2m / (period + 1);
            int start = kl.Count - period;
            if (start < 0) start = 0;

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

        // ======================================================================
        // UPDATE SL + AI SELF-LEARNING
        // ======================================================================
        private async Task UpdateSL(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            BinanceUsdFuturesOrder slOrder,
            decimal entry,
            decimal newSl,
            RealContextSignal ctx)
        {
            decimal oldSl = slOrder.StopPrice ?? slOrder?.Price ?? 0m;
            if (oldSl <= 0 || newSl <= 0)
                return;

            // Не ухудшаем SL
            if (side == PositionSide.Long && newSl <= oldSl) return;
            if (side == PositionSide.Short && newSl >= oldSl) return;

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            decimal tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            decimal s = Math.Round(newSl / tick) * tick;
            decimal limit = side == PositionSide.Long ? s - tick : s + tick;

            // Отменяем старый SL
            await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, slOrder.Id);

            // Ставим новый SL
            var slRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                type: FuturesOrderType.Stop,
                quantity: qty,
                price: limit,
                positionSide: side,
                timeInForce: TimeInForce.GoodTillCanceled,
                stopPrice: s);

            if (!slRes.Success || slRes.Data == null)
            {
                _logger.LogError("[SUPERVISOR] ERROR update SL for {symbol}: {err}", symbol, slRes.Error);
                return;
            }

            _logger.LogInformation("[SUPERVISOR] TRAIL SL UPDATED {symbol} {old} → {ns}", symbol, oldSl, s);

            // ============================================================
            // AI-Learning: записываем апдейт SL как потенциальный outcome
            // ============================================================
            var posRaw = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol);
            decimal liq = 0m;

            if (posRaw.Success && posRaw.Data != null)
            {
                var p = posRaw.Data.FirstOrDefault(x => x.PositionSide == side);
                if (p != null)
                    liq = p.LiquidationPrice;
            }

            bool win = side == PositionSide.Long ? s > entry : s < entry;

            _aiLearning.RecordTrade(
                symbol: symbol,
                entryPrice: entry,
                exitPrice: s,
                liquidationPrice: liq,
                isWin: win,
                regime: _regimeNow,
                signal: null); // тут уже REAL-CONTEXT, а не исходный TradeSignal
        }
    }
}
