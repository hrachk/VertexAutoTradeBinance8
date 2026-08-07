using Binance.Net.Enums;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public sealed record StopResult(bool Success, decimal StopPrice, string Reason);

    /// <summary>
    /// ProtectionOrderService — ЕДИНСТВЕННАЯ точка постановки защитного стопа.
    ///
    /// Ключевые отличия от старого кода в PositionSupervisorService:
    ///   1) СИНХРОННО и с await → вызывающий знает результат (раньше всё уходило в fire-and-forget очередь).
    ///   2) БЕЗ reduceOnly. В Hedge Mode Binance отклоняет reduceOnly с -1106.
    ///      Старый CreateEmergencySLAsync слал reduceOnly:true + positionSide → стоп падал ВСЕГДА,
    ///      а -1106 не проходит проверку IsAlgoRequired(-4120) → фолбэка не было, только LogError.
    ///   3) closePosition:true вместо quantity → стоп всегда закрывает ВЕСЬ остаток позиции.
    ///      Это снимает рассинхрон qty после Early-TP / Harvest частичных закрытий.
    ///   4) Идемпотентность: перед постановкой читаем открытые ордера по нужному positionSide.
    ///   5) Валидация относительно текущей цены → нет -2021 "Order would immediately trigger".
    ///   6) Ретраи + фолбэк на ALGO RAW (-4120).
    /// </summary>
    public sealed class ProtectionOrderService
    {
        private readonly ILogger<ProtectionOrderService> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly SymbolInfoService _symbolInfo;
        private readonly IAlgoOrderRawClient _algoRaw;

        private const int MaxAttempts = 3;
        private const int RetryDelayMs = 400;

        /// <summary>Минимальный зазор между стопом и текущей ценой, чтобы не словить -2021.</summary>
        private const decimal MinGapPct = 0.0008m; // 0.08%

        public ProtectionOrderService(
            ILogger<ProtectionOrderService> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            IAlgoOrderRawClient algoRaw)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
            _algoRaw = algoRaw;
        }

        // =================================================================
        // ЕСТЬ ЛИ УЖЕ СТОП НА ЭТОЙ СТОРОНЕ
        // =================================================================
        public async Task<bool> HasStopAsync(string symbol, PositionSide side, CancellationToken ct)
        {
            using var client = _factory.CreateRestClient();

            var res = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct);
            if (!res.Success || res.Data == null)
                return false;

            return res.Data.Any(o =>
                o.PositionSide == side &&
                (o.Type == FuturesOrderType.StopMarket || o.Type == FuturesOrderType.Stop));
        }

        // =================================================================
        // ГЛАВНЫЙ МЕТОД: гарантировать наличие SL
        // =================================================================
        /// <param name="replaceExisting">
        /// true — режим обновления (BE / трейлинг): существующий стоп будет отменён
        /// НЕПОСРЕДСТВЕННО перед постановкой нового, а не заранее. Окно без защиты
        /// сжимается до одного round-trip вместо неограниченного, как было в
        /// UpdateSL_ProAsync (cancel → fire-and-forget place → catch {}).
        /// </param>
        public async Task<StopResult> EnsureStopAsync(
            string symbol,
            PositionSide side,
            decimal desiredStop,
            CancellationToken ct,
            bool replaceExisting = false)
        {
            if (desiredStop <= 0)
                return new StopResult(false, 0m, "InvalidStopPrice");

            // 1) Идемпотентность
            if (!replaceExisting && await HasStopAsync(symbol, side, ct))
            {
                _logger.LogInformation("[PROTECT][{s}][{side}] SL уже существует → skip", symbol, side);
                return new StopResult(true, desiredStop, "AlreadyExists");
            }

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            decimal tick = filters.tickSize > 0 ? filters.tickSize : 0.0001m;

            string lastReason = "Unknown";

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                using var client = _factory.CreateRestClient();

                // 2) Текущая цена → санитизация стопа
                decimal last = 0m;
                var priceRes = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(symbol, ct: ct);
                if (priceRes.Success && priceRes.Data != null)
                    last = priceRes.Data.Price;

                decimal stop = Math.Round(desiredStop / tick) * tick;

                if (last > 0)
                {
                    decimal minGap = Math.Max(tick * 2m, last * MinGapPct);

                    if (side == PositionSide.Long && stop >= last - minGap)
                    {
                        stop = Math.Floor((last - minGap) / tick) * tick;
                        _logger.LogWarning(
                            "[PROTECT][{s}][LONG] SL был выше рынка → сдвинут: {old} → {new} (last={last})",
                            symbol, desiredStop, stop, last);
                    }
                    else if (side == PositionSide.Short && stop <= last + minGap)
                    {
                        stop = Math.Ceiling((last + minGap) / tick) * tick;
                        _logger.LogWarning(
                            "[PROTECT][{s}][SHORT] SL был ниже рынка → сдвинут: {old} → {new} (last={last})",
                            symbol, desiredStop, stop, last);
                    }
                }

                if (stop <= 0)
                {
                    lastReason = "StopBelowZero";
                    continue;
                }

                var orderSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

                // 2b) Режим замены: снимаем старые стопы прямо перед постановкой.
                //     С closePosition:true две STOP_MARKET на одной стороне биржа не примет.
                if (replaceExisting)
                {
                    var open = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct);
                    if (open.Success && open.Data != null)
                    {
                        foreach (var o in open.Data.Where(o =>
                                     o.PositionSide == side &&
                                     (o.Type == FuturesOrderType.StopMarket || o.Type == FuturesOrderType.Stop)))
                        {
                            await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, o.Id, ct: ct);
                        }
                    }
                }

                // 3) NORMAL endpoint. БЕЗ reduceOnly (Hedge!), closePosition = true.
                var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: orderSide,
                    type: FuturesOrderType.StopMarket,
                    quantity: null,
                    positionSide: side,
                    stopPrice: stop,
                    closePosition: true,
                    workingType: WorkingType.Mark,
                    priceProtect: true,
                    ct: ct);

                if (res.Success)
                {
                    _logger.LogWarning("[PROTECT][{s}][{side}] SL SET (NORMAL) @ {stop}", symbol, side, stop);
                    return new StopResult(true, stop, "Placed");
                }

                int code = res.Error?.Code ?? 0;
                lastReason = $"{code}:{res.Error?.Message}";

                _logger.LogWarning(
                    "[PROTECT][{s}][{side}] attempt {n}/{max} SL FAILED: {err}",
                    symbol, side, attempt, MaxAttempts, lastReason);

                // 4) -4120 → биржа требует ALGO endpoint
                if (code == -4120)
                {
                    var ok = await _algoRaw.PlaceConditionalAsync(
                        symbol: symbol,
                        side: orderSide,
                        positionSide: side,
                        type: "STOP_MARKET",
                        quantity: null,
                        triggerPrice: stop,
                        workingType: "MARK_PRICE",
                        reduceOnly: null,
                        closePosition: true,
                        ct: ct);

                    if (ok)
                    {
                        _logger.LogWarning("[PROTECT][{s}][{side}] SL SET (ALGO-RAW) @ {stop}", symbol, side, stop);
                        return new StopResult(true, stop, "PlacedAlgo");
                    }

                    lastReason = "AlgoRawFailed";
                }

                // 5) -2021 (immediately trigger) → цена уже пробила стоп.
                //    Ретрай бессмысленен: сигнализируем вызывающему, он закроет позицию.
                if (code == -2021)
                    return new StopResult(false, stop, "ImmediateTrigger");

                // 6) -4045 / -1015: лимит ордеров или частота → подождать подольше
                await Task.Delay(RetryDelayMs * attempt, ct);
            }

            _logger.LogError("[PROTECT][{s}][{side}] SL NOT SET после {n} попыток: {reason}",
                symbol, side, MaxAttempts, lastReason);

            return new StopResult(false, 0m, lastReason);
        }

        // =================================================================
        // УДАЛЕНИЕ ДУБЛЕЙ SL
        // Старый Supervisor при hasMultipleSL просто писал warning и пропускал
        // создание — дубли жили до закрытия позиции.
        // =================================================================
        public async Task<int> CancelDuplicateStopsAsync(
            string symbol,
            PositionSide side,
            CancellationToken ct)
        {
            using var client = _factory.CreateRestClient();

            var res = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct);
            if (!res.Success || res.Data == null)
                return 0;

            var stops = res.Data
                .Where(o => o.PositionSide == side &&
                            (o.Type == FuturesOrderType.StopMarket || o.Type == FuturesOrderType.Stop))
                .OrderByDescending(o => o.CreateTime)
                .ToList();

            if (stops.Count <= 1)
                return 0;

            int removed = 0;
            foreach (var dup in stops.Skip(1)) // оставляем самый свежий
            {
                var c = await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, dup.Id, ct: ct);
                if (c.Success) removed++;
            }

            _logger.LogWarning("[PROTECT][{s}][{side}] удалено дублей SL: {n}", symbol, side, removed);
            return removed;
        }

        // =================================================================
        // АВАРИЙНОЕ ЗАКРЫТИЕ (fail-closed): нет стопа → нет позиции
        // =================================================================
        public async Task<bool> ForceClosePositionAsync(
            string symbol,
            PositionSide side,
            CancellationToken ct)
        {
            using var client = _factory.CreateRestClient();

            var posRes = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, ct: ct);
            if (!posRes.Success || posRes.Data == null)
                return false;

            var pos = posRes.Data.FirstOrDefault(p =>
                p.Symbol == symbol && p.PositionSide == side && p.Quantity != 0m);

            if (pos == null)
                return true; // позиции уже нет

            var qty = Math.Abs(pos.Quantity);
            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: closeSide,
                type: FuturesOrderType.Market,
                quantity: qty,
                positionSide: side,
                ct: ct);

            if (res.Success)
            {
                _logger.LogError(
                    "[PROTECT][{s}][{side}] FAIL-CLOSED: позиция закрыта по рынку qty={qty} (SL поставить не удалось)",
                    symbol, side, qty);
                return true;
            }

            _logger.LogCritical(
                "[PROTECT][{s}][{side}] НЕЗАЩИЩЁННАЯ ПОЗИЦИЯ: SL не поставлен И закрыть не удалось: {err}",
                symbol, side, res.Error);

            return false;
        }
    }
}
