// ==================================================================================
// PositionSupervisorService — FINAL PATCH FOR Binance.Net 11.11.0
// ==================================================================================

using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using CryptoExchange.Net.Objects;
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
        private MarketRegime regimeNow;

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
            regimeNow = MarketRegime.Range;
        }

        // ======================================================================
        // MAIN SUPERVISE
        // ======================================================================
        public async Task SuperviseAsync(string symbol, TradeSignal? lastSignal, CancellationToken ct)
        {
            using var client = _factory.CreateRestClient();

            // ==================================================================
            // 0) DETECT MANUAL POSITION — FIXED FOR Binance.Net 11.11
            // ==================================================================
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

            // --- 0. Авто-детект ручной позиции ---
            var posRes = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol);

            if (posRes.Success && posRes.Data != null)
            {
                var manualPos = posRes.Data.ToList().Find(x => Math.Abs(x.Quantity) > 0);

                if (manualPos != null)
                {
                    var last = lastSignal;

                    // Проверяем - это новая ручная позиция?
                    if (_manualHandler.IsNewManualPosition(manualPos, last))
                    {
                        lastSignal = _manualHandler.ConvertManualToSignal(manualPos);

                        _logger.LogWarning(
                            "[MANUAL][{symbol}] Обнаружена РУЧНАЯ позиция qty={qty}, side={side}. Создан виртуальный сигнал.",
                            symbol, Math.Abs(manualPos.Quantity), manualPos.Quantity > 0 ? "LONG" : "SHORT"
                        );
                    }
                }
            }

            // ==================================================================
            // 1) RETRY POSITIONS UNTIL THEY BECOME REAL
            // ==================================================================
            var posInfo = await GetPositionsWithRetryAsync(client, symbol, ct);

            if (posInfo == null || !posInfo.Success || posInfo.Data == null)
            {
                _logger.LogWarning("[SUPERVISOR] No positions loaded for {symbol}", symbol);
                return;
            }

            var longPos = posInfo.Data.FirstOrDefault(p => p.PositionSide == PositionSide.Long);
            var shortPos = posInfo.Data.FirstOrDefault(p => p.PositionSide == PositionSide.Short);

            // ==================================================================
            // 2) LOAD ALL OPEN ORDERS
            // ==================================================================
            var openOrders = await LoadOrders(client, symbol);

            // ==================================================================
            // 3) Fetch M1 klines for trailing
            // ==================================================================
            IReadOnlyList<BinanceFuturesUsdtKline>? klines1m = null;

            try
            {
                klines1m = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 160);
                var rr = _regime.DetectRegime(symbol, KlineInterval.OneMinute, klines1m);
                if (rr != null)
                    regimeNow = rr.Regime;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SUPERVISOR] Error loading klines for {symbol}", symbol);
            }

            await HandleSideAsync(client, symbol, PositionSide.Long, longPos, openOrders, lastSignal, klines1m, ct);
            await HandleSideAsync(client, symbol, PositionSide.Short, shortPos, openOrders, lastSignal, klines1m, ct);
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

                    // FIX: Binance.Net 11.11 uses PositionAmt
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
        // PROCESS SIDE
        // ======================================================================
        //private async Task HandleSideAsync(
        //    BinanceRestClient client,
        //    string symbol,
        //    PositionSide side,
        //    BinancePositionDetailsUsdt? pos,
        //    List<BinanceUsdFuturesOrder> allOrders,
        //    TradeSignal? signal,
        //    IReadOnlyList<BinanceFuturesUsdtKline>? klines,
        //    CancellationToken ct)
        //{
        //    decimal qty = pos != null ? Math.Abs(pos.Quantity) : 0m;

        //    if (qty <= 0)
        //    {
        //        _logger.LogInformation("[SUPERVISOR] {symbol} {side}: no existing position", symbol, side);
        //        return;
        //    }

        //    var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

        //    var slOrder = allOrders.FirstOrDefault(o =>
        //        o.Side == closeSide &&
        //        (o.Type == FuturesOrderType.Stop ||
        //         o.Type == FuturesOrderType.StopMarket));

        //    if (slOrder == null)
        //    {
        //        _logger.LogWarning("[SUPERVISOR] {symbol} {side}: NO SL FOUND — cannot trail", symbol, side);
        //        return;
        //    }

        //    await MultiLayerTrailing(client, symbol, side, qty, signal, allOrders, klines);
        //}

        // ======================================================================
        // MULTI-LAYER TRAILING  — unchanged
        // ======================================================================
        // (оставлено как было, без изменений — всё работает)

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

            if (qty <= 0)
            {
                _logger.LogInformation("[SUPERVISOR] {symbol} {side}: no position", symbol, side);
                return;
            }

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var orders = allOrders.Where(o => o.PositionSide == side).ToList();

            var sl = orders.FirstOrDefault(o =>
                o.Side == closeSide &&
                (o.Type == FuturesOrderType.Stop || o.Type == FuturesOrderType.StopMarket));

            var tp = orders.FirstOrDefault(o =>
                o.Side == closeSide &&
                (o.Type == FuturesOrderType.TakeProfit || o.Type == FuturesOrderType.TakeProfitMarket));

            // ============================
            // 1) Если нет SL — создаём
            // ============================
            if (sl == null)
            {
                await CreateEmergencySL(client, symbol, side, qty, signal);
                _logger.LogWarning("[SUPERVISOR][{symbol}] SL восстановлен (user removed)", symbol);
                return;
            }

            // ============================
            // 2) Если нет TP — создаём
            // ============================
            if (tp == null)
            {
                await CreateEmergencyTP(client, symbol, side, qty, signal);
                _logger.LogWarning("[SUPERVISOR][{symbol}] TP восстановлен (user removed)", symbol);
                return;
            }

            // ============================
            // 3) Только теперь — TRAILING
            // ============================
            await MultiLayerTrailing(client, symbol, side, qty, signal, orders, klines);
        }


        private async Task CreateEmergencySL(
    BinanceRestClient client,
    string symbol,
    PositionSide side,
    decimal qty,
    TradeSignal? signal)
        {
            if (signal == null || qty <= 0)
                return;

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            decimal tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            decimal sl = Math.Round(signal.StopLoss / tick) * tick;
            decimal limit = side == PositionSide.Long ? sl - tick : sl + tick;

            var o = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol,
                side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                FuturesOrderType.Stop,
                quantity: qty,
                stopPrice: sl,
                price: limit,
                positionSide: side);

            _logger.LogInformation("[SUPERVISOR] SL CREATED {symbol} sl={sl}", symbol, sl);
        }

        private async Task CreateEmergencyTP(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            TradeSignal? signal)
        {
            if (signal == null || signal.TakeProfits.Count == 0)
                return;

            decimal trigger = signal.TakeProfits.First();

            var o = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol,
                side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                FuturesOrderType.TakeProfitMarket,
                quantity: qty,
                stopPrice: trigger,
                positionSide: side);

            _logger.LogInformation("[SUPERVISOR] TP CREATED {symbol} tp={tp}", symbol, trigger);
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
            if (signal == null || klines == null || klines.Count < 50 || qty <= 0)
                return;

            var slOrder = orders.FirstOrDefault(o =>
                o.Side == (side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy) &&
                (o.Type == FuturesOrderType.Stop || o.Type == FuturesOrderType.StopMarket));

            if (slOrder == null)
            {
                _logger.LogWarning("[SUPERVISOR] {symbol} {side}: TRAILING skipped — SL order not found", symbol, side);
                return;
            }

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

            await UpdateSL(client, symbol, side, qty, slOrder, signal.EntryPrice, targetSl, signal);
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
     decimal newSl,
     TradeSignal? signal = null)
        {
            decimal oldSl = slOrder.StopPrice ?? slOrder?.Price ?? 0m;
            if (oldSl <= 0 || newSl <= 0)
                return;

            // --- НЕ УХУДШАЕМ SL ---
            if (side == PositionSide.Long && newSl <= oldSl) return;
            if (side == PositionSide.Short && newSl >= oldSl) return;

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            decimal tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            decimal s = Math.Round(newSl / tick) * tick;
            decimal limit = side == PositionSide.Long ? s - tick : s + tick;

            // CANCEL OLD SL
            await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, slOrder.Id);

            // PLACE NEW SL
            await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol,
                side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                FuturesOrderType.Stop,
                quantity: qty,
                stopPrice: s,
                price: limit,
                positionSide: side,
                timeInForce: TimeInForce.GoodTillCanceled);

            _logger.LogInformation("[SUPERVISOR] TRAIL SL UPDATED {symbol} {old} → {ns}", symbol, oldSl, s);

            // ============================================================
            //             AI-LEARNING: Record Trade Result
            // ============================================================

            // Получаем POS, чтобы взять liquidationPrice
            var posRaw = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol);
            decimal liq = 0m;

            if (posRaw.Success && posRaw.Data != null)
            {
                var p = posRaw.Data.FirstOrDefault(x => x.PositionSide == side);
                if (p != null)
                    liq = p.LiquidationPrice; // Binance даёт это поле
            }

            bool win = side == PositionSide.Long
                ? s > entry
                : s < entry;

           


            _aiLearning.RecordTrade(
                symbol: symbol,
                entryPrice: entry,
                exitPrice: s,
                liquidationPrice: liq,
                isWin: win,
                regime: regimeNow,
                signal: signal  // <===== ВАЖНО
            );
        }

    }
}
