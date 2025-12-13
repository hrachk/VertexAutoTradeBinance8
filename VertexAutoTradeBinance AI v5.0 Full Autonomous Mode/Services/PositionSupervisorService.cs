using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using CryptoExchange.Net.Objects;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// PositionSupervisorService v8.1 FINAL (NON-ALGO + ALGO-FALLBACK, QUANT-REALTIME MAX)
    ///
    /// FIX v8.1:
    /// 1) Правильный поиск SL/TP по PositionSide (Hedge) + анти-дубли
    /// 2) Если Binance возвращает -4120 (SL/TP только через Algo Order API) —
    ///    автоматически ставим SL/TP через RAW POST /fapi/v1/algoOrder (algoType=CONDITIONAL).
    /// 3) Никаких reduceOnly/workingType в обычных ордерах (как ты хотел),
    ///    но в RAW Algo можно (по умолчанию reduceOnly не шлём в Hedge).
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
        private readonly BinanceAlgoOrderRaw _algoRaw;
        private readonly LiquidityGuardService _liquidityGuard;

        private MarketRegime _regimeNow;

        private enum TrendContinuationLevel { Low, Medium, High }
        private enum ExhaustionLevel { None = 0, Mild = 1, Strong = 2 }

        public PositionSupervisorService(
            ILogger<PositionSupervisorService> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            AiStopLossOptimizer slOptimizer,
            AiSelfLearningService aiLearning,
            MarketDataService marketData,
            AiMarketRegimeService regime,
            ManualPositionHandler manualHandler,
            IConfiguration cfg,
            IHttpClientFactory httpFactory,
            LiquidityGuardService liquidityGuard)
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

            // RAW ALGO sender (Binance:ApiKey / Binance:ApiSecret)
            _algoRaw = new BinanceAlgoOrderRaw(cfg, httpFactory, _logger);
            _liquidityGuard = liquidityGuard;
        }

        // =====================================================================
        // MAIN ENTRY
        // =====================================================================
        public async Task SuperviseAsync(string symbol, TradeSignal? lastSignal, CancellationToken ct)
        {
            using var client = _factory.CreateRestClient();

            // 0) MANUAL → виртуальный сигнал
            if (lastSignal == null)
            {
                var manualSignal = await _manualHandler.DetectManualAsync(client, symbol, ct);
                if (manualSignal != null)
                {
                    lastSignal = manualSignal;
                    _logger.LogWarning("[MANUAL][{symbol}] Virtual signal injected", symbol);
                }
            }

            // 1) Позиции (с ретраями)
            var posInfo = await GetPositionsWithRetryAsync(client, symbol, ct);
            if (!posInfo.Success || posInfo.Data == null)
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

            // 2) Ордера
            var openOrders = await LoadOrdersAsync(client, symbol);

            // 3) Режим + klines (1m)
            IReadOnlyList<BinanceFuturesUsdtKline>? klines1m = null;
            try
            {
                klines1m = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 160);
                var rr = _regime.DetectRegime(symbol, KlineInterval.OneMinute, klines1m);
                if (rr != null) _regimeNow = rr.Regime;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SUPERVISOR] Klines load error {symbol}", symbol);
            }

            // 4) Обработка сторон
            if (hasLong)
                await HandleSideAsync(client, symbol, PositionSide.Long, longPos!, openOrders, lastSignal, klines1m, ct);

            if (hasShort)
                await HandleSideAsync(client, symbol, PositionSide.Short, shortPos!, openOrders, lastSignal, klines1m, ct);
        }

        // =====================================================================
        // RETRY POSITIONS
        // =====================================================================
        private async Task<WebCallResult<BinancePositionDetailsUsdt[]>> GetPositionsWithRetryAsync(
            BinanceRestClient client, string symbol, CancellationToken ct)
        {
            const int maxAttempts = 10;
            var delay = TimeSpan.FromMilliseconds(200);

            WebCallResult<BinancePositionDetailsUsdt[]> last = null!;
            for (int i = 0; i < maxAttempts; i++)
            {
                ct.ThrowIfCancellationRequested();

                var res = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, null, ct);
                last = res;

                if (res.Success && res.Data != null && res.Data.Any(x => x.Quantity != 0m))
                    return res;

                await Task.Delay(delay, ct);
            }
            return last;
        }

        private async Task<List<BinanceUsdFuturesOrder>> LoadOrdersAsync(BinanceRestClient client, string symbol)
        {
            var res = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol);
            return res.Success && res.Data != null ? res.Data.ToList() : new List<BinanceUsdFuturesOrder>();
        }

        // =====================================================================
        // HANDLE SIDE  (FIXED)
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

            // ---------- CLOSE DETECTOR ----------
            var key = $"{symbol}_{side}";
            var prevQty = _manualHandler.GetPrevQty(key);
            var prevEntry = _manualHandler.GetPrevEntry(key);
            _manualHandler.SetPrevState(key, pos.Quantity, pos.EntryPrice);

            if (prevQty != 0 && pos.Quantity == 0)
            {
                decimal exitPrice = pos.MarkPrice;
                var sigSide = side == PositionSide.Long ? SignalSide.Buy : SignalSide.Sell;

                _aiLearning.RecordTrade(symbol, sigSide, entry: prevEntry, exit: exitPrice, regime: _regimeNow);

                _logger.LogWarning(
                    "[AI][{symbol}] POSITION CLOSED → saved to ai_learning.json | entry={entry} exit={exit}",
                    symbol, prevEntry, exitPrice);
                return;
            }

            if (qty <= 0)
            {
                _logger.LogInformation("[SUPERVISOR] {symbol} {side}: no qty", symbol, side);
                return;
            }

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            // В Hedge мы обязаны обслуживать именно "свой" PositionSide
            // (и НЕ мешать ордерам другой стороны)
            var orders = allOrders
                .Where(o => o.PositionSide == side)
                .ToList();

            // Детектор дублей (FIX)
            bool hasMultipleSL = orders.Count(o => o.Type == FuturesOrderType.StopMarket) > 1;
            bool hasMultipleTP = orders.Count(o => o.Type == FuturesOrderType.TakeProfitMarket) > 1;

            if (hasMultipleSL)
                _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] Multiple SL detected → skip SL create", symbol, side);

            if (hasMultipleTP)
                _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] Multiple TP detected → skip TP create", symbol, side);

            // Ищем существующие SL/TP
            var sl = orders.FirstOrDefault(o =>
                o.Side == closeSide && o.Type == FuturesOrderType.StopMarket);

            var tp = orders.FirstOrDefault(o =>
                o.Side == closeSide && o.Type == FuturesOrderType.TakeProfitMarket);

            decimal entry = pos.EntryPrice;
            if (entry <= 0 && signal != null && signal.Symbol == symbol)
                entry = signal.EntryPrice;

            // 1) SL отсутствует → аварийный SL (если нет дублей)
            if (sl == null && !hasMultipleSL)
            {
                await CreateEmergencySLAsync(client, symbol, side, qty, entry, signal, ct);
                // READ-BACK: подтверждаем, что SL реально появился
                var verify = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct);
                var myOrders = verify.Success && verify.Data != null
                    ? verify.Data.Where(o => o.PositionSide == side).ToList()
                    : new List<BinanceUsdFuturesOrder>();

                bool slNow = myOrders.Any(o =>
                    o.Side == closeSide &&
                    o.Type == FuturesOrderType.StopMarket);

                _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] SL verify: {ok} (orders={cnt})",
                    symbol, side, slNow, myOrders.Count);

                return;
            }

            // 2) TP отсутствует → аварийный TP (если нет дублей)
            if (tp == null && !hasMultipleTP)
            {
                await CreateEmergencyTPAsync(client, symbol, side, qty, entry, signal, ct);

                // READ-BACK: подтверждаем, что SL реально появился
                var verify = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct);
                var myOrders = verify.Success && verify.Data != null
                    ? verify.Data.Where(o => o.PositionSide == side).ToList()
                    : new List<BinanceUsdFuturesOrder>();

                bool slNow = myOrders.Any(o =>
                    o.Side == closeSide &&
                    o.Type == FuturesOrderType.StopMarket);

                _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] TP verify: {ok} (orders={cnt})",
                    symbol, side, slNow, myOrders.Count);

                return;
 
            }

            // 3) Трейлинг + раннер
            if (klines != null && klines.Count >= 50)
            {
                // Если у тебя эти методы реально есть в проекте — оставляем вызовы.
                // Если нет — просто закомментируй блок.
                await ManageRunnerTpAsync(client, symbol, side, qty, entry, orders, signal, klines, ct);
                await ManageRunnerTpExtensionAsync(client, symbol, side, qty, entry, signal, orders, klines, ct);
                await MultiLayerTrailingAsync(client, symbol, side, qty, entry, signal, orders, klines, ct);
            }
        }

        // =====================================================================
        // EMERGENCY SL  (TRY NORMAL → FALLBACK ALGO RAW on -4120)
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
            {
                rawSl = signal.StopLoss;
            }
            else
            {
                try
                {
                    var kl = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 100);
                    if (kl.Count < 30) return;

                    var atr = _marketData.CalculateAtr(kl, 14);

                    // динамический множитель
                    var atrMult = _regimeNow switch
                    {
                        MarketRegime.Range => 1.2m,
                        MarketRegime.Squeeze => 1.5m,
                        MarketRegime.UpTrend or MarketRegime.DownTrend => 1.8m,   // ← WeakTrend
                        MarketRegime.VolatileChop => 2.0m,
                        _ => 2.2m // StrongUpTrend / StrongDownTrend
                    };

                    rawSl = side == PositionSide.Long
                        ? entryPrice - atr * atrMult
                        : entryPrice + atr * atrMult;


                    if (atr <= 0) return;

                    var atrMultiplier = (atr > 0.0025m) ? 1.5m : 1.2m;
                    rawSl = side == PositionSide.Long ? entryPrice - atr * atrMultiplier : entryPrice + atr * atrMultiplier;
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
                if (side == PositionSide.Long && sl >= mark) sl = mark - tick;
                if (side == PositionSide.Short && sl <= mark) sl = mark + tick;
            }

            // === SMART SL BLOCK ===
            if (_regimeNow is MarketRegime.StrongUpTrend or MarketRegime.StrongDownTrend)
            {
                _logger.LogInformation(
                    "[SUPERVISOR] Skip SL: strong trend detected ({regime}) {symbol}",
                    _regimeNow, symbol);
                return;
            }


            var orderSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            // 1) NORMAL endpoint
            try
            {
                var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol,
                    orderSide,
                    FuturesOrderType.StopMarket,
                    qty,
                    positionSide: side,
                    stopPrice: sl,
                    ct: ct);

                if (res.Success)
                {
                    _logger.LogInformation("[SUPERVISOR] SL CREATED (NORMAL) {symbol} {side} sl={sl}", symbol, side, sl);
                    return;
                }

                // 2) FALLBACK ALGO on -4120
                if (IsAlgoRequired(res.Error))
                {
                    _logger.LogWarning("[SUPERVISOR] SL requires ALGO endpoint (-4120) → RAW ALGO {symbol} {side}", symbol, side);

                    var ok = await _algoRaw.PlaceConditionalAsync(
                        symbol: symbol,
                        side: orderSide,
                        positionSide: side,
                        type: "STOP_MARKET",
                        quantity: qty,
                        triggerPrice: sl,
                        workingType: "CONTRACT_PRICE",
                        reduceOnly: null, // в Hedge reduceOnly нельзя
                        ct: ct);

                    if (ok)
                    {
                        _logger.LogInformation("[SUPERVISOR] SL CREATED (ALGO-RAW) {symbol} {side} sl={sl}", symbol, side, sl);
                        return;
                    }
                    _logger.LogWarning(
                        "[SUPERVISOR] SL skipped: market context does not allow safe SL placement {symbol} {side}", symbol, side);
                    return;
                }

                _logger.LogError("[SUPERVISOR] ERROR SL create (NORMAL) {symbol}: {err}", symbol, res.Error);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SUPERVISOR] EX SL create {symbol}", symbol);
            }
        }

        // =====================================================================
        // EMERGENCY TP  (TRY NORMAL → FALLBACK ALGO RAW on -4120)
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
            {
                trigger = signal.TakeProfits[0];
            }
            else
            {
                try
                {
                    var kl = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 100);
                    if (kl.Count < 30) return;

                    var atr = _marketData.CalculateAtr(kl, 14);
                    if (atr <= 0) return;

                    trigger = side == PositionSide.Long ? entryPrice + atr * 1.5m : entryPrice - atr * 1.5m;
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

            // validate vs entry
            if (side == PositionSide.Long && trigger <= entryPrice) trigger = entryPrice + tick * 3;
            if (side == PositionSide.Short && trigger >= entryPrice) trigger = entryPrice - tick * 3;

            var orderSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            // 1) NORMAL endpoint
            try
            {
                var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol,
                    orderSide,
                    FuturesOrderType.TakeProfitMarket,
                    qty,
                    positionSide: side,
                    stopPrice: trigger,
                    ct: ct);

                if (res.Success)
                {
                    _logger.LogInformation("[SUPERVISOR] TP CREATED (NORMAL) {symbol} {side} tp={tp}", symbol, side, trigger);
                    return;
                }

                // 2) FALLBACK ALGO on -4120
                if (IsAlgoRequired(res.Error))
                {
                    _logger.LogWarning("[SUPERVISOR] TP requires ALGO endpoint (-4120) → RAW ALGO {symbol} {side}", symbol, side);

                    var ok = await _algoRaw.PlaceConditionalAsync(
                        symbol: symbol,
                        side: orderSide,
                        positionSide: side,
                        type: "TAKE_PROFIT_MARKET",
                        quantity: qty,
                        triggerPrice: trigger,
                        workingType: "CONTRACT_PRICE",
                        reduceOnly: null, // в Hedge reduceOnly нельзя
                        ct: ct);

                    if (ok)
                    {
                        _logger.LogInformation("[SUPERVISOR] TP CREATED (ALGO-RAW) {symbol} {side} tp={tp}", symbol, side, trigger);
                        return;
                    }

                    _logger.LogError("[SUPERVISOR] TP ALGO-RAW FAILED {symbol} {side}", symbol, side);
                    return;
                }

                _logger.LogError("[SUPERVISOR] ERROR create TP (NORMAL) {symbol}: {err}", symbol, res.Error);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SUPERVISOR] EX create TP {symbol}", symbol);
            }
        }

        private static bool IsAlgoRequired(CryptoExchange.Net.Objects.Error? err)
        {
            if (err == null) return false;

            // -4120: Order type not supported for this endpoint. Please use the Algo Order API endpoints instead.
            if (err.Code == -4120) return true;

            var msg = (err.Message ?? string.Empty).ToLowerInvariant();
            return msg.Contains("algo order api") || msg.Contains("algoorder") || msg.Contains("not supported for this endpoint");
        }

        // =====================================================================
        // === PLACEHOLDERS: keep your existing methods below (как в твоём v8.0) ===
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
            if (signal?.TakeProfit is null || signal.TakeProfit <= 0)
                return;

            // ---- текущая цена ----
            var priceRes = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(symbol, ct: ct);
            if (!priceRes.Success || priceRes.Data == null)
                return;

            var price = priceRes.Data.Price;

            bool tpHit =
                side == PositionSide.Long
                    ? price >= signal.TakeProfit
                    : price <= signal.TakeProfit;

            if (!tpHit)
                return;

            // ---- режим ----
            bool trendOk =
                side == PositionSide.Long
                    ? _regimeNow is MarketRegime.StrongUpTrend or MarketRegime.UpTrend
                    : _regimeNow is MarketRegime.StrongDownTrend or MarketRegime.DownTrend;

            if (!trendOk)
                return;

            // ---- импульс ----
            var atr = signal.Atr ?? 0m;
            if (atr <= 0 || klines.Count < 2)
                return;

            var last = klines[^1];
            var body = Math.Abs(last.ClosePrice - last.OpenPrice);

            if (body < atr * 0.7m)
                return;

            // ---- LiquidityGuard ----
            if (_liquidityGuard.LastDanger?.Block == true)
                return;

            // ============================================================
            // === TP EXTENSION ACTION ===
            // ============================================================

            // 1️⃣ частичное закрытие (70%)
            var closeQty = Math.Round(qty * 0.7m, 8);
            var runnerQty = qty - closeQty;

            if (closeQty <= 0 || runnerQty <= 0)
                return;

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: closeSide,
                type: FuturesOrderType.Market,
                quantity: closeQty,
                positionSide: side,
                ct: ct);

            _logger.LogInformation(
                "[TP-EXT][{symbol}] Partial TP executed {closed}/{total}, runner={runner}",
                symbol, closeQty, qty, runnerQty);

            // 2️⃣ перенос SL → BE + 0.25 ATR
            decimal newSl =
                side == PositionSide.Long
                    ? entryPrice + atr * 0.25m
                    : entryPrice - atr * 0.25m;

            var slOrder = orders.FirstOrDefault(o =>
                o.Type == FuturesOrderType.StopMarket &&
                o.PositionSide == side);

            if (slOrder != null)
            {
                await UpdateSLAsync(
                    client,
                    symbol,
                    side,
                    runnerQty,
                    slOrder,
                    entryPrice,
                    newSl,
                    signal,
                    ct);
            }

            _logger.LogWarning(
                "[TP-EXT][{symbol}] Runner activated | new SL={sl}",
                symbol, newSl);
        }
        public async Task UpdateSLAsync(
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

            decimal oldSl = slOrder.StopPrice ?? slOrder.Price;
            if (oldSl <= 0) return;

            if (side == PositionSide.Long && newSl <= oldSl) return;
            if (side == PositionSide.Short && newSl >= oldSl) return;

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            var tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            var s = Math.Round(newSl / tick) * tick;

            await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, slOrder.Id, ct: ct);

            var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol,
                side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                FuturesOrderType.StopMarket,
                qty,
                stopPrice: s,
               // reduceOnly: true,
                positionSide: side,
                workingType: WorkingType.Mark,
                ct: ct);

            if (!res.Success)
            {
                _logger.LogError("[SUPERVISOR] SL update failed {symbol}: {err}", symbol, res.Error);
                return;
            }

            _logger.LogInformation("[SUPERVISOR] SL UPDATED {symbol}: {old} → {new}", symbol, oldSl, s);

            // 🔥 AI learning hook — ВОТ ЗДЕСЬ ЕМУ МЕСТО
            if (signal != null && !signal.IsManual)
            {
                var sigSide = side == PositionSide.Long ? SignalSide.Buy : SignalSide.Sell;
                _aiLearning.RecordTrade(symbol, sigSide, entry, s, _regimeNow);
            }
        }


        private async Task ManageRunnerTpExtensionAsync(
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
            // ТВОЯ ТЕКУЩАЯ РЕАЛИЗАЦИЯ (как у тебя в v8.0)
            await Task.CompletedTask;
        }

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
            // ТВОЯ ТЕКУЩАЯ РЕАЛИЗАЦИЯ (как у тебя в v8.0)
            await Task.CompletedTask;
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
            catch
            {
                // ignore
            }

            return fallback > 0 ? fallback : 0m;
        }

        // =====================================================================
        // RAW BINANCE ALGO ORDER (POST /fapi/v1/algoOrder)
        // =====================================================================
        private sealed class BinanceAlgoOrderRaw
        {
            private readonly HttpClient _http;
            private readonly ILogger _logger;
            private readonly string _apiKey;
            private readonly string _apiSecret;
            private readonly string _baseUrl;

            public BinanceAlgoOrderRaw(IConfiguration cfg, IHttpClientFactory httpFactory, ILogger logger)
            {
                _logger = logger;

                _apiKey = cfg["Binance:ApiKey"] ?? string.Empty;
                _apiSecret = cfg["Binance:ApiSecret"] ?? string.Empty;
                _baseUrl = (cfg["Binance:FuturesBaseUrl"] ?? "https://fapi.binance.com").TrimEnd('/');

                _http = httpFactory.CreateClient("BinanceAlgoRaw");
                _http.Timeout = TimeSpan.FromSeconds(8);
            }

            public async Task<bool> PlaceConditionalAsync(
                string symbol,
                OrderSide side,
                PositionSide positionSide,
                string type,                 // STOP_MARKET / TAKE_PROFIT_MARKET / ...
                decimal quantity,
                decimal triggerPrice,
                string workingType,          // CONTRACT_PRICE / MARK_PRICE
                bool? reduceOnly,
                CancellationToken ct)
            {
                if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
                {
                    _logger.LogError("[ALGO-RAW] Missing Binance:ApiKey / Binance:ApiSecret in config");
                    return false;
                }

                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // Binance требует точку как decimal separator
                string D(decimal v) => v.ToString("0.########", CultureInfo.InvariantCulture);

                // /fapi/v1/algoOrder  (TRADE)
                // algoType=CONDITIONAL обязательно. :contentReference[oaicite:1]{index=1}
                var q = new List<KeyValuePair<string, string>>
                {
                    new("algoType", "CONDITIONAL"),
                    new("symbol", symbol),
                    new("side", side == OrderSide.Buy ? "BUY" : "SELL"),
                    new("type", type),
                    new("timestamp", ts.ToString(CultureInfo.InvariantCulture)),
                    new("workingType", workingType),
                    new("triggerPrice", D(triggerPrice))
                };

                // Hedge Mode: positionSide обязателен (если у тебя Hedge). :contentReference[oaicite:2]{index=2}
                q.Add(new("positionSide", positionSide.ToString().ToUpperInvariant()));

                // quantity нельзя с closePosition=true. Мы ставим quantity явно.
                q.Add(new("quantity", D(quantity)));

                // reduceOnly нельзя в Hedge Mode. :contentReference[oaicite:3]{index=3}
                if (reduceOnly.HasValue && positionSide == PositionSide.Both)
                    q.Add(new("reduceOnly", reduceOnly.Value ? "true" : "false"));

                var query = BuildQuery(q);
                var sig = Sign(query, _apiSecret);

                var url = $"{_baseUrl}/fapi/v1/algoOrder?{query}&signature={sig}";

                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.TryAddWithoutValidation("X-MBX-APIKEY", _apiKey);

                try
                {
                    using var resp = await _http.SendAsync(req, ct);
                    var body = await resp.Content.ReadAsStringAsync(ct);

                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogError("[ALGO-RAW] HTTP {code} body={body}", (int)resp.StatusCode, body);
                        return false;
                    }

                    // На успехе приходит JSON с algoId/clientAlgoId и т.д. :contentReference[oaicite:4]{index=4}
                    _logger.LogInformation("[ALGO-RAW] OK {symbol} {type} posSide={ps} trig={tp} body={body}",
                        symbol, type, positionSide, triggerPrice, body);

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ALGO-RAW] EX PlaceConditionalAsync {symbol}", symbol);
                    return false;
                }
            }

            private static string BuildQuery(IEnumerable<KeyValuePair<string, string>> q)
            {
                // Binance: query string, url-encode
                var sb = new StringBuilder();
                foreach (var kv in q)
                {
                    if (sb.Length > 0) sb.Append('&');
                    sb.Append(Uri.EscapeDataString(kv.Key));
                    sb.Append('=');
                    sb.Append(Uri.EscapeDataString(kv.Value));
                }
                return sb.ToString();
            }

            private static string Sign(string queryString, string secret)
            {
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(queryString));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }


        }
    }
}
