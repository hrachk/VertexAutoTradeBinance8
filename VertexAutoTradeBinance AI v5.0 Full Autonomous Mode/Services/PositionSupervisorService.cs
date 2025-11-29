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

        public PositionSupervisorService(
            ILogger<PositionSupervisorService> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            AiStopLossOptimizer slOptimizer,
            AiSelfLearningService aiLearning,
            MarketDataService marketData,
            AiMarketRegimeService regime)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
            _slOptimizer = slOptimizer;
            _aiLearning = aiLearning;
            _marketData = marketData;
            _regime = regime;
        }

        // ======================================================================
        // MAIN SUPERVISE
        // ======================================================================
        public async Task SuperviseAsync(string symbol, TradeSignal lastSignal, CancellationToken ct)
        {
            using var client = _factory.CreateRestClient();

            // --- 1) Позиции с RETRY (чтобы не сносить ордера до появления позиции)
            var posInfo = await GetPositionsWithRetryAsync(client, symbol, ct);
            if (posInfo == null || posInfo.Data == null)
            {
                _logger.LogWarning("[SUPERVISOR] Failed to load positions for {symbol}", symbol);
                return;
            }

            var longPos = posInfo.Data.FirstOrDefault(p => p.PositionSide == PositionSide.Long);
            var shortPos = posInfo.Data.FirstOrDefault(p => p.PositionSide == PositionSide.Short);

            // --- 2) Все открытые ордера
            var openOrders = await LoadOrders(client, symbol);

            // --- 3) Свечи М1 для трейлинга
            IReadOnlyList<BinanceFuturesUsdtKline>? klines1m = null;
            try
            {
                klines1m = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 200);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SUPERVISOR] GetKlines failed for {symbol}", symbol);
            }

            await HandleSideAsync(client, symbol, PositionSide.Long, longPos, openOrders, lastSignal, klines1m, ct);
            await HandleSideAsync(client, symbol, PositionSide.Short, shortPos, openOrders, lastSignal, klines1m, ct);
        }

        // RETRY-обёртка для позиций: даём бирже время зафиксировать факт открытия
        // RETRY-обёртка для позиций: даём бирже время зафиксировать факт открытия
        private async Task<WebCallResult<BinancePositionDetailsUsdt[]>>
        GetPositionsWithRetryAsync(
            BinanceRestClient client,
            string symbol,
            CancellationToken ct)
        {
            const int maxAttempts = 5;
            var delay = TimeSpan.FromMilliseconds(200);

            WebCallResult<BinancePositionDetailsUsdt[]> last = null!;

            for (int i = 0; i < maxAttempts; i++)
            {
                ct.ThrowIfCancellationRequested();

                var res = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol);
                last = res;

                if (res.Success && res.Data != null)
                {
                    var arr = res.Data; // <-- массив BinancePositionDetailsUsdt[]

                    var longPos = arr.FirstOrDefault(x => x.PositionSide == PositionSide.Long);
                    var shortPos = arr.FirstOrDefault(x => x.PositionSide == PositionSide.Short);

                    // позиция появилась → возвращаем сразу
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
        // SIDE PROCESSOR
        // ======================================================================
        private async Task HandleSideAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            BinancePositionDetailsUsdt? pos,
            List<BinanceUsdFuturesOrder> allOrders,
            TradeSignal? signal,
            IReadOnlyList<BinanceFuturesUsdtKline>? klines,
            CancellationToken ct)
        {
            decimal qty = pos != null ? Math.Abs(pos.Quantity) : 0m;

            var orders = allOrders.Where(o => o.PositionSide == side).ToList();
            var oppositeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            // === НЕТ ПОЗИЦИИ: ЧИСТИМ ТОЛЬКО ЗАЩИТНЫЕ SL/TP, НЕ ТРОГАЕМ ВХОДНЫЕ ОРДЕРА ===
            if (qty <= 0)
            {
                await CleanupSideOrders(client, symbol, side, orders);
                _logger.LogInformation("[SUPERVISOR] No position for {symbol} {side} → only SL/TP cleanup", symbol, side);
                return;
            }

            bool hasSl = orders.Any(o => o.Side == oppositeSide &&
                                         (o.Type == FuturesOrderType.Stop ||
                                          o.Type == FuturesOrderType.StopMarket));

            bool hasTp = orders.Any(o => o.Side == oppositeSide &&
                                         (o.Type == FuturesOrderType.TakeProfit ||
                                          o.Type == FuturesOrderType.TakeProfitMarket));

            if (!hasSl)
                await CreateSL(client, symbol, side, qty, signal);

            if (!hasTp)
                await CreateTP(client, symbol, side, qty, signal);

            await MultiLayerTrailing(client, symbol, side, qty, signal, orders, klines);
        }

        /// <summary>
        /// Чистим ТОЛЬКО защитные ордера (SL/TP) по стороне, не трогая входные лимит/маркет.
        /// </summary>
        private async Task CleanupSideOrders(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            List<BinanceUsdFuturesOrder> orders)
        {
            var oppositeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var toCancel = orders.Where(o =>
                    o.PositionSide == side &&
                    o.Side == oppositeSide &&
                    (o.Type == FuturesOrderType.Stop ||
                     o.Type == FuturesOrderType.StopMarket ||
                     o.Type == FuturesOrderType.TakeProfit ||
                     o.Type == FuturesOrderType.TakeProfitMarket))
                .ToList();

            foreach (var o in toCancel)
            {
                await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, o.Id);
                _logger.LogInformation("[SUPERVISOR] cleanup {symbol} side={side} cancel {id} type={type}",
                    symbol, side, o.Id, o.Type);
            }
        }

        // ======================================================================
        // CREATE STOP LOSS
        // ======================================================================
        private async Task CreateSL(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            TradeSignal? signal)
        {
            if (signal == null || qty <= 0) return;

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            decimal tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            decimal sl = Math.Round(signal.StopLoss / tick) * tick;
            decimal limit = side == PositionSide.Long ? sl - tick : sl + tick;

            var sideOrder = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol,
                sideOrder,
                FuturesOrderType.Stop,
                quantity: qty,
                stopPrice: sl,
                price: limit,
                positionSide: side,
                timeInForce: TimeInForce.GoodTillCanceled);

            if (res.Success)
                _logger.LogInformation($"[SUPERVISOR] SL CREATED {symbol} {sl} qty={qty}");
            else
                _logger.LogError($"[SUPERVISOR] SL ERROR {symbol}: {res.Error}");
        }

        // ======================================================================
        // CREATE TAKE PROFIT (MARKET)
        // ======================================================================
        private async Task CreateTP(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            TradeSignal? signal)
        {
            if (signal == null || signal.TakeProfits == null || signal.TakeProfits.Count == 0 || qty <= 0)
                return;

            decimal trigger = signal.TakeProfits.First();
            var tpSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol,
                tpSide,
                FuturesOrderType.TakeProfitMarket,
                quantity: qty,
                stopPrice: trigger,
                positionSide: side);

            if (res.Success)
                _logger.LogInformation($"[SUPERVISOR] TP CREATED {symbol} trigger={trigger} qty={qty}");
            else
                _logger.LogError($"[SUPERVISOR] TP ERROR {symbol}: {res.Error}");
        }

        // ======================================================================
        // MULTI-LAYER TRAILING SYSTEM (PRO LEVEL)
        // ======================================================================
        private async Task MultiLayerTrailing(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            TradeSignal? signal,
            List<BinanceUsdFuturesOrder> orders,
            IReadOnlyList<BinanceFuturesUsdtKline>? klines)
        {
            if (signal == null || klines == null || klines.Count < 50)
                return;

            var slOrder = orders.FirstOrDefault(o =>
                o.Type == FuturesOrderType.Stop || o.Type == FuturesOrderType.StopMarket);

            if (slOrder == null) return;

            decimal mark = klines.Last().ClosePrice;

            // 1) ATR Trailing
            decimal atr = CalculateAtr(klines);
            decimal slAtr = side == PositionSide.Long
                ? mark - atr * 0.9m
                : mark + atr * 0.9m;

            // 2) EMA21 Trailing
            decimal ema21 = CalculateEma(klines, 21);
            decimal slEma = side == PositionSide.Long
                ? ema21 - atr * 0.3m
                : ema21 + atr * 0.3m;

            // 3) SuperTrend
            decimal st = SuperTrend(klines, atr);
            decimal slSt = st;

            // 4) MicroTrail
            var last = klines.Last();
            var prev = klines[klines.Count - 2];

            bool microUp = last.LowPrice > prev.LowPrice;
            bool microDn = last.HighPrice < prev.HighPrice;

            decimal slMicro = side == PositionSide.Long
                ? (microUp ? last.LowPrice - atr * 0.2m : last.LowPrice - atr * 0.6m)
                : (microDn ? last.HighPrice + atr * 0.2m : last.HighPrice + atr * 0.6m);

            // FINAL SL
            decimal targetSl = side switch
            {
                PositionSide.Long => new[] { slAtr, slEma, slSt, slMicro }.Max(),
                PositionSide.Short => new[] { slAtr, slEma, slSt, slMicro }.Min(),
                _ => signal.StopLoss
            };

            await UpdateSL(client, symbol, side, qty, slOrder, signal.EntryPrice, targetSl);
        }

        private decimal CalculateAtr(IReadOnlyList<BinanceFuturesUsdtKline> kl)
        {
            decimal atr = 0;
            int start = kl.Count - 15; // чтобы i-1 был валиден
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
        // UPDATE SL
        // ======================================================================
        private async Task UpdateSL(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            BinanceUsdFuturesOrder slOrder,
            decimal entry,
            decimal newSl)
        {
            decimal oldSl = slOrder.StopPrice ?? slOrder?.Price ?? 0m;
            if (oldSl <= 0 || newSl <= 0)
                return;

            if (side == PositionSide.Long && newSl <= oldSl) return;
            if (side == PositionSide.Short && newSl >= oldSl) return;

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            decimal tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            decimal s = Math.Round(newSl / tick) * tick;
            decimal limit = side == PositionSide.Long ? s - tick : s + tick;

            await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, slOrder.Id);

            await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol,
                side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                FuturesOrderType.Stop,
                quantity: qty,
                stopPrice: s,
                price: limit,
                positionSide: side,
                timeInForce: TimeInForce.GoodTillCanceled);

            _logger.LogInformation("[SUPERVISOR] TRAIL SL UPDATED {symbol} {old} → {ns}",
                symbol, oldSl, s);

            bool win = side == PositionSide.Long
                ? s > entry
                : s < entry;

            _aiLearning.RecordTrade(symbol, entry, s, 0m, win);
        }
    }
}
