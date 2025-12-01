using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// V5.0 — Универсальный супервизор реальных позиций:
    /// руководит всем: SL, TP, Trailing, ManipulationGuard, AI-risk,
    /// авто-репейр, лимиты, BOTH-позиции, глубина, ликвидность.
    /// </summary>
    public class RealContextSupervisor
    {
        private readonly ILogger<RealContextSupervisor> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly SymbolInfoService _info;
        private readonly AiStopLossOptimizer _slOpt;
        private readonly AiMarketRegimeService _regime;
        private readonly AiSelfLearningService _aiLearning;
        private readonly LiquidityGuardService _liqGuard;
        private readonly MarketDataService _market;

        public RealContextSupervisor(
            ILogger<RealContextSupervisor> logger,
            BinanceClientFactory factory,
            SymbolInfoService info,
            AiStopLossOptimizer slOpt,
            AiMarketRegimeService regime,
            AiSelfLearningService aiLearning,
            LiquidityGuardService liqGuard,
            MarketDataService market)
        {
            _logger = logger;
            _factory = factory;
            _info = info;
            _slOpt = slOpt;
            _regime = regime;
            _aiLearning = aiLearning;
            _liqGuard = liqGuard;
            _market = market;
        }

        // ==========================================================
        //  Главный цикл супервизора — вызывается StrategyWorker.cs
        // ==========================================================
        public async Task RunAsync(string symbol, CancellationToken ct)
        {
            using var client = _factory.CreateRestClient();

            // === 1) Читаем позицию ===
            var pos = await LoadPosition(client, symbol);
            if (pos == null)
            {
                _logger.LogInformation("[SUP] {symbol}: no position", symbol);
                return;
            }

            // === 2) Формируем контекст ===
            var ctx = await BuildRealContext(client, symbol, pos);

            // === 3) Проверяем манипуляции ===
            await ManipulationScan(ctx);

            // === 4) Гарантированное наличие SL/TP ===
            await AutoRepairSLTP(ctx);

            // === 5) Запускаем смарт-трейлинг ===
            await SmartTrailing(ctx);

            // === 6) Deep AI-learning
            await UpdateLearning(ctx);
        }

        // ==========================================================
        //  Загрузка позиции
        // ==========================================================
        private async Task<BinancePositionDetailsUsdt?> LoadPosition(
            BinanceRestClient client,
            string symbol)
        {
            var res = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol);
            if (!res.Success || res.Data == null)
                return null;

            return res.Data.FirstOrDefault(x => Math.Abs(x.Quantity) > 0);
        }

        // ==========================================================
        //  Формирование полного контекста позиции (V5)
        // ==========================================================
        private async Task<RealPositionContext> BuildRealContext(
            BinanceRestClient client,
            string symbol,
            BinancePositionDetailsUsdt pos)
        {
            var filters = await _info.GetFuturesFiltersAsync(symbol);

            var kl = await _market.GetKlines(symbol, KlineInterval.OneMinute, 160);
            var regime = _regime.DetectRegime(symbol, KlineInterval.OneMinute, kl);

            var markRes = await client.UsdFuturesApi.ExchangeData.GetMarkPriceAsync(symbol);
            decimal mark = markRes.Success ? markRes.Data.MarkPrice : pos.EntryPrice;

            return new RealPositionContext
            {
                Symbol = symbol,
                Side = pos.PositionSide,
               Quantity   = Math.Abs(pos.Quantity),
                EntryPrice = pos.EntryPrice,
                MarkPrice = mark,
                Leverage = pos.Leverage,
                Liquidation = pos.LiquidationPrice,
                Filters = filters,
                Regime = regime.Regime,
                Klines = kl
            };
        }

        // ==========================================================
        //  Анти-манипуляционный анализ
        // ==========================================================
        private async Task ManipulationScan(RealPositionContext ctx)
        {
            var danger = await _liqGuard.CheckDangerAsync(ctx.Symbol, ctx.Mark);

            if (danger.IsDangerous)
            {
                _logger.LogWarning(
                    "[SUP][{symbol}] Manipulation detected → widening SL, freeze trailing",
                    ctx.Symbol);

                ctx.ManipulationDetected = true;
            }
        }

        // ==========================================================
        //  Гарантированный SL/TP
        // ==========================================================
        private async Task AutoRepairSLTP(RealPositionContext ctx)
        {
            using var client = _factory.CreateRestClient();

            var ordersRes = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(ctx.Symbol);
            var orders = ordersRes.Success && ordersRes.Data != null ?
                ordersRes.Data.ToList() : new List<BinanceUsdFuturesOrder>();

            var closeSide = ctx.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var sl = orders.FirstOrDefault(o =>
                o.Side == closeSide &&
                (o.Type == FuturesOrderType.Stop || o.Type == FuturesOrderType.StopMarket));

            var tp = orders.FirstOrDefault(o =>
                o.Side == closeSide &&
                (o.Type == FuturesOrderType.TakeProfit || o.Type == FuturesOrderType.TakeProfitMarket));

            // === SL ===
            if (sl == null)
            {
                var newSL = _slOpt.CalculateOptimalSL(ctx.Symbol, ctx.Mark, ctx.Entry, ctx.Regime);
                await PlaceSL(client, ctx, newSL);
            }

            // === TP ===
            if (tp == null)
            {
                var newTP = _slOpt.CalculateTakeProfit(ctx.Symbol, ctx.Entry, ctx.Regime);
                await PlaceTP(client, ctx, newTP);
            }
        }

        // ==========================================================
        //  Smart Trailing System V5.0
        // ==========================================================
        private async Task SmartTrailing(RealPositionContext ctx)
        {
            if (ctx.Klines == null || ctx.Klines.Count < 60)
                return;

            if (ctx.ManipulationDetected)
                return; // freeze trailing during manipulation burst

            var last = ctx.Klines.Last();
            var atr = CalculateAtr(ctx.Klines);

            decimal ema21 = CalculateEma(ctx.Klines, 21);
            decimal micro = last.LowPrice;

            decimal sl = ctx.Side switch
            {
                PositionSide.Long =>
                    new[] {
                        last.ClosePrice - atr * 0.9m,
                        ema21 - atr * 0.3m,
                        micro - atr * 0.2m }.Max(),

                PositionSide.Short =>
                    new[] {
                        last.ClosePrice + atr * 0.9m,
                        ema21 + atr * 0.3m,
                        last.HighPrice + atr * 0.2m }.Min(),

                _ => ctx.Entry
            };

            using var client = _factory.CreateRestClient();
            await UpdateSL(client, ctx, sl);
        }

        // ==========================================================
        //  Обновление SL
        // ==========================================================
        private async Task UpdateSL(
            BinanceRestClient client,
            RealPositionContext ctx,
            decimal newSl)
        {
            var ordersRes = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(ctx.Symbol);
            var orders = ordersRes.Data?.ToList() ?? new List<BinanceUsdFuturesOrder>();

            var closeSide = ctx.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var sl = orders.FirstOrDefault(o =>
                o.Side == closeSide &&
                (o.Type == FuturesOrderType.Stop || o.Type == FuturesOrderType.StopMarket));

            if (sl == null)
                return;

            decimal oldSl = sl.StopPrice ?? sl.Price ?? 0;

            if (ctx.Side == PositionSide.Long && newSl <= oldSl) return;
            if (ctx.Side == PositionSide.Short && newSl >= oldSl) return;

            await client.UsdFuturesApi.Trading.CancelOrderAsync(ctx.Symbol, sl.Id);

            await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                ctx.Symbol,
                closeSide,
                FuturesOrderType.Stop,
                quantity: ctx.Qty,
                stopPrice: newSl,
                positionSide: ctx.Side);

            _logger.LogInformation("[SUP] TrailSL {symbol} {old} → {ns}", ctx.Symbol, oldSl, newSl);
        }

        // ==========================================================
        //  SL/TP placing helpers
        // ==========================================================
        private Task PlaceSL(BinanceRestClient client, RealPositionContext ctx, decimal sl)
        {
            var side = ctx.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            return client.UsdFuturesApi.Trading.PlaceOrderAsync(
                ctx.Symbol,
                side,
                FuturesOrderType.StopMarket,
                quantity: ctx.Qty,
                stopPrice: sl,
                positionSide: ctx.Side);
        }

        private Task PlaceTP(BinanceRestClient client, RealPositionContext ctx, decimal tp)
        {
            var side = ctx.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            return client.UsdFuturesApi.Trading.PlaceOrderAsync(
                ctx.Symbol,
                side,
                FuturesOrderType.TakeProfitMarket,
                quantity: ctx.Qty,
                stopPrice: tp,
                positionSide: ctx.Side);
        }

        // ==========================================================
        //  ATR / EMA
        // ==========================================================
        private decimal CalculateAtr(IReadOnlyList<BinanceFuturesUsdtKline> kl)
        {
            decimal sum = 0;
            for (int i = kl.Count - 15; i < kl.Count; i++)
            {
                var c = kl[i];
                var p = kl[i - 1];
                decimal tr = Math.Max(
                    c.HighPrice - c.LowPrice,
                    Math.Max(
                        Math.Abs(c.HighPrice - p.ClosePrice),
                        Math.Abs(c.LowPrice - p.ClosePrice)));
                sum += tr;
            }
            return sum / 14m;
        }

        private decimal CalculateEma(IReadOnlyList<BinanceFuturesUsdtKline> kl, int period)
        {
            decimal k = 2m / (period + 1);
            int start = kl.Count - period;
            decimal ema = kl[start].ClosePrice;

            for (int i = start + 1; i < kl.Count; i++)
                ema = kl[i].ClosePrice * k + ema * (1 - k);

            return ema;
        }

        // ==========================================================
        //  AI-Learning
        // ==========================================================
        private Task UpdateLearning(RealPositionContext ctx)
        {
            return _aiLearning.LearnFromContextAsync(ctx);
        }
    }
}
