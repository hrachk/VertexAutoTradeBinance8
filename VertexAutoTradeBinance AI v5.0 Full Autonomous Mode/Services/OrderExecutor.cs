using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class OrderExecutor
    {
        private readonly ILogger<OrderExecutor> _logger;
        private readonly BinanceClientFactory _factory;

        // --- анти-манип / логика входа ---
        private const decimal MaxSlipForLimitPercent = 0.0008m;   // 0.08% допуск для LIMIT
        private const decimal MaxSlipHardRejectPercent = 0.003m;  // >0.3% – считаем, что цена уже убежала
        private const decimal MaxAtrSpikePercent = 0.02m;         // ATR >2% от цены – подозрение на шпиль

        public OrderExecutor(ILogger<OrderExecutor> logger, BinanceClientFactory factory)
        {
            _logger = logger;
            _factory = factory;
        }

        public async Task PlaceOrderAsync(TradeSignal signal, decimal quantity, CancellationToken ct = default)
        {
            if (signal == null) throw new ArgumentNullException(nameof(signal));
            if (quantity <= 0m)
            {
                _logger.LogWarning("[ORDER][{Symbol}] Quantity <= 0, skip", signal.Symbol);
                return;
            }

            using var client = _factory.CreateRestClient();

            var side = signal.Side == SignalSide.Buy ? OrderSide.Buy : OrderSide.Sell;
            var posSide = signal.Side == SignalSide.Buy ? PositionSide.Long : PositionSide.Short;

            // === 1) Читаем текущую цену (anti-manip, mix MARKET/LIMIT) ===
            decimal markPrice = signal.EntryPrice;
            try
            {
                var priceResult = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(signal.Symbol, ct: ct);
                if (priceResult.Success && priceResult.Data != null && priceResult.Data.Price > 0)
                    markPrice = priceResult.Data.Price;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ORDER][{Symbol}] Failed to load mark price, use signal.EntryPrice", signal.Symbol);
            }

            var slip = Math.Abs(markPrice - signal.EntryPrice) / markPrice; // относительное отклонение
            var atrSpike = signal.Atr.HasValue
                           ? (signal.Atr.Value / signal.EntryPrice) >= MaxAtrSpikePercent
                           : false;

            // === 2) Анти-манипуляция ===
            if (atrSpike)
            {
                _logger.LogWarning(
                    "[ORDER][{Symbol}] Anti-Manip: ATR spike (ATR={Atr}, EP={EntryPrice}), skip entry",
                    signal.Symbol, signal.Atr, signal.EntryPrice);
                return;
            }

            // === 3) Выбор LIMIT или MARKET ===
            FuturesOrderType entryType;
            decimal? limitPrice = null;
            TimeInForce? tif = null;

            if (slip <= MaxSlipForLimitPercent)
            {
                // Цена рядом с желаемой – ставим LIMIT по сигналу
                entryType = FuturesOrderType.Limit;
                limitPrice = signal.EntryPrice;
                tif = TimeInForce.GoodTillCanceled;

                _logger.LogInformation(
                    "[ORDER][{Symbol}] ENTRY: use LIMIT @ {Price} (slip={Slip:P4})",
                    signal.Symbol, limitPrice, slip);
            }
            else if (slip <= MaxSlipHardRejectPercent)
            {
                // Цена чуть ушла – MARKET, но допускаем
                entryType = FuturesOrderType.Market;
                _logger.LogInformation(
                    "[ORDER][{Symbol}] ENTRY: use MARKET (slip={Slip:P4})",
                    signal.Symbol, slip);
            }
            else
            {
                // Цена уже улетела – лучше не догонять
                _logger.LogWarning(
                    "[ORDER][{Symbol}] ENTRY rejected: price slipped too much (slip={Slip:P4}), anti-chase",
                    signal.Symbol, slip);
                return;
            }

            // === 4) Открываем позицию ===
            var entryOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: signal.Symbol,
                side: side,
                type: entryType,
                quantity: quantity,
                price: limitPrice,
                positionSide: posSide,
                timeInForce: tif,
                reduceOnly: false,
                ct: ct);

            if (!entryOrder.Success || entryOrder.Data == null)
            {
                _logger.LogError("[ORDER][{Symbol}] ENTRY ERROR: {Error}", signal.Symbol, entryOrder.Error);
                return;
            }

            _logger.LogInformation(
                "[ORDER][{Symbol}] ENTRY OK type={Type} qty={Qty} price={Price}",
                signal.Symbol, entryType, quantity, limitPrice ?? markPrice);

            // === 5) После входа – ставим защиту (SL/TP) с ретраями ===
            var protectionOk = await EnsureProtectionAsync(client, signal, quantity, side, posSide, ct);

            if (!protectionOk)
            {
                // Если не удалось поставить SL/TP – аварийно закрываем позицию
                _logger.LogError(
                    "[ORDER][{Symbol}] PROTECTION FAILED – emergency close position", signal.Symbol);

                var closeSide = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

                var closeOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: signal.Symbol,
                    side: closeSide,
                    type: FuturesOrderType.Market,
                    quantity: quantity,
                    positionSide: posSide,
                    reduceOnly: true,
                    ct: ct);

                if (!closeOrder.Success)
                {
                    _logger.LogError(
                        "[ORDER][{Symbol}] EMERGENCY CLOSE ERROR: {Error}",
                        signal.Symbol, closeOrder.Error);
                }
                else
                {
                    _logger.LogWarning("[ORDER][{Symbol}] EMERGENCY CLOSE OK", signal.Symbol);
                }
            }
        }

        /// <summary>
        /// Ставит/переставляет SL и TP c несколькими попытками.
        /// Возвращает true, если всё ок, иначе false.
        /// </summary>
        private async Task<bool> EnsureProtectionAsync(
            BinanceRestClient client,
            TradeSignal signal,
            decimal quantity,
            OrderSide side,
            PositionSide posSide,
            CancellationToken ct)
        {
            const int maxAttempts = 3;

            // === SL ===
            var slOk = await TryPlaceWithRetryAsync(
                maxAttempts,
                async attempt =>
                {
                    if (signal.StopLoss <= 0)
                        return true; // нет SL в сигнале – считаем нормой

                    var slSide = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

                    var sl = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: signal.Symbol,
                        side: slSide,
                        type: FuturesOrderType.StopMarket,
                        quantity: quantity,
                        stopPrice: signal.StopLoss,
                        positionSide: posSide,
                        reduceOnly: true,
                        workingType: WorkingType.Mark,
                        closePosition: false,
                        ct: ct);

                    if (!sl.Success)
                    {
                        _logger.LogWarning(
                            "[ORDER][{Symbol}] SL attempt {Attempt}/{Max} ERROR: {Error}",
                            signal.Symbol, attempt, maxAttempts, sl.Error);
                        return false;
                    }

                    _logger.LogInformation(
                        "[ORDER][{Symbol}] SL placed @ {Price} (attempt {Attempt})",
                        signal.Symbol, signal.StopLoss, attempt);
                    return true;
                });

            if (!slOk) return false;

            // === TP (multi-TP) ===
            if (signal.TakeProfits == null || signal.TakeProfits.Count == 0)
                return true; // только SL – тоже ок

            var tpSide = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
            var partQty = quantity / signal.TakeProfits.Count;

            for (var i = 0; i < signal.TakeProfits.Count; i++)
            {
                var tpPrice = signal.TakeProfits[i];

                var tpOk = await TryPlaceWithRetryAsync(
                    maxAttempts,
                    async attempt =>
                    {
                        var tp = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                            symbol: signal.Symbol,
                            side: tpSide,
                            type: FuturesOrderType.TakeProfitMarket,
                            quantity: partQty,
                            stopPrice: tpPrice,
                            positionSide: posSide,
                            reduceOnly: true,
                            workingType: WorkingType.Mark,
                            closePosition: false,
                            ct: ct);

                        if (!tp.Success)
                        {
                            _logger.LogWarning(
                                "[ORDER][{Symbol}] TP#{Idx} attempt {Attempt}/{Max} ERROR: {Error}",
                                signal.Symbol, i + 1, attempt, maxAttempts, tp.Error);
                            return false;
                        }

                        _logger.LogInformation(
                            "[ORDER][{Symbol}] TP#{Idx} placed @ {Price} qty={Qty} (attempt {Attempt})",
                            signal.Symbol, i + 1, tpPrice, partQty, attempt);
                        return true;
                    });

                if (!tpOk)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Универсальный helper для ретраев.
        /// </summary>
        private static async Task<bool> TryPlaceWithRetryAsync(
            int maxAttempts,
            Func<int, Task<bool>> action)
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var ok = await action(attempt);
                if (ok) return true;

                // небольшая пауза перед повтором
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
            }

            return false;
        }
    }
}
