using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class OrderCleanerService
    {
        private readonly ILogger<OrderCleanerService> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly SymbolInfoService _symbolInfo;

        public OrderCleanerService(
            ILogger<OrderCleanerService> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
        }

        // =============================================================
        // 🔥 Основной метод — авто-очистка мусорных/старых ордеров
        // =============================================================
        public async Task CleanupOutdatedOrdersAsync(
            string symbol,
            TradeSignal signal,
            CancellationToken ct = default)
        {
            using var client = _factory.CreateRestClient();

            var openOrdersResult =
                await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol);

            if (!openOrdersResult.Success || openOrdersResult.Data == null)
            {
                _logger.LogWarning($"[Cleaner] Не удалось загрузить открытые ордера: {openOrdersResult.Error}");
                return;
            }

            var openOrders = openOrdersResult.Data;
            if (!openOrders.Any())
                return;

            _logger.LogInformation(
                $"🧹 CLEANER START {symbol}: найдено {openOrders.Length} активных ордеров");

            // Биржевые фильтры
            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            decimal tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            decimal entryRounded = Round(signal.EntryPrice, tick);
            decimal slRounded = Round(signal.StopLoss, tick);

            var tpRounded = signal.TakeProfits?
                .Select(p => Round(p, tick))
                .ToList() ?? new();

            foreach (var order in openOrders)
            {
                bool shouldDelete = false;

                // =============================================================
                // 1) Удаляем старые ENTRY, если цена убежала слишком далеко
                // =============================================================
                if (order.Type == FuturesOrderType.Limit)
                {
                    if (order.Side == OrderSide.Buy && order.Price < entryRounded * 0.97m)
                    {
                        shouldDelete = true;
                    }
                    else if (order.Side == OrderSide.Sell && order.Price > entryRounded * 1.03m)
                    {
                        shouldDelete = true;
                    }
                }

                // =============================================================
                // 2) Удаляем старый SL (если target SL != сигналу)
                // =============================================================
                if (order.Type == FuturesOrderType.Stop)
                {
                    if (Math.Abs((decimal)(order.StopPrice - slRounded)) > tick * 3)
                    {
                        shouldDelete = true;
                    }
                }

                // =============================================================
                // 3) Удаляем TP, если они устарели
                // =============================================================
                if (order.Type == FuturesOrderType.Limit &&
                    order.ReduceOnly == true) // TP всегда reduceOnly
                {
                    if (!tpRounded.Contains(order.Price))
                        shouldDelete = true;
                }

                // =============================================================
                // Удаляем ордер
                // =============================================================
                if (shouldDelete)
                {
                    var cancelResult = await client.UsdFuturesApi.Trading.CancelOrderAsync(
                        symbol: symbol,
                        orderId: order.Id);

                    if (cancelResult.Success)
                    {
                        _logger.LogInformation(
                            $"🧹 CLEANER: удалён старый ордер [{order.Id}] price={order.Price}");
                    }
                    else
                    {
                        _logger.LogWarning(
                            $"CLEANER FAIL {symbol}: {cancelResult.Error}");
                    }
                }
            }

            _logger.LogInformation($"🧹 CLEANER END {symbol}");
        }


        // =============================================================
        // 🔥 Полная очистка ВСЕХ ордеров (regular + algo) по монете
        //    Использовать после полного закрытия позиции / перед вторым входом
        // =============================================================
        public async Task CancelAllOrdersForSymbolAsync(
            string symbol,
            CancellationToken ct = default)
        {
            using var client = _factory.CreateRestClient();

            int cleared = 0;

            try
            {
                var openRes = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct);
                if (openRes.Success && openRes.Data != null)
                {
                    foreach (var o in openRes.Data)
                    {
                        try
                        {
                            var c = await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, o.Id, ct: ct);
                            if (c.Success)
                            {
                                cleared++;
                                _logger.LogInformation($"🧹 FULL-CLEAN {symbol}: cancelled order {o.Id} type={o.Type}");
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"[Cleaner] GetOpenOrders {symbol} failed");
            }

            try
            {
                var algoRes = await client.UsdFuturesApi.Trading.GetOpenConditionalOrdersAsync(symbol: symbol, ct: ct);
                if (algoRes.Success && algoRes.Data != null)
                {
                    foreach (var a in algoRes.Data)
                    {
                        try
                        {
                            var c = await client.UsdFuturesApi.Trading.CancelConditionalOrderAsync(orderId: a.Id, ct: ct);
                            if (c.Success)
                            {
                                cleared++;
                                _logger.LogInformation($"🧹 FULL-CLEAN {symbol}: cancelled ALGO {a.Id} type={a.Type}");
                            }
                            else
                            {
                                try { await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, a.Id, ct: ct); } catch { }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"[Cleaner] GetOpenConditionalOrders {symbol} failed");
            }

            _logger.LogInformation($"🧹 FULL-CLEAN END {symbol}: cleared={cleared}");
        }

        private static decimal Round(decimal x, decimal tick)
        {
            if (tick <= 0) return x;
            return Math.Round(x / tick) * tick;
        }
    }
}
