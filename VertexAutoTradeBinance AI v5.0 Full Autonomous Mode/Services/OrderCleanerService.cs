using Binance.Net.Enums;
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
                await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct);

            if (!openOrdersResult.Success || openOrdersResult.Data == null)
            {
                _logger.LogWarning("[CLEANER] Failed to load open orders for {symbol}: {err}", symbol, openOrdersResult.Error);
                return;
            }

            var openOrders = openOrdersResult.Data;
            if (!openOrders.Any())
                return;

            _logger.LogInformation("[CLEANER] START {symbol}: {count} active orders found", symbol, openOrders.Length);

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
                // 3) Удаляем TP если они устарели
                // В Hedge mode TP ордера НЕ используют reduceOnly —
                // они определяются по type + positionSide.
                // Проверяем оба варианта.
                // =============================================================
                if (order.Type is FuturesOrderType.TakeProfit
                               or FuturesOrderType.TakeProfitMarket)
                {
                    bool isOurTp = order.ReduceOnly == true  // One-way mode
                        || order.PositionSide != PositionSide.Both; // Hedge mode

                    if (isOurTp && !tpRounded.Contains(order.Price) && !tpRounded.Contains((decimal)order.StopPrice))
                        shouldDelete = true;
                }

                // =============================================================
                // Удаляем ордер
                // =============================================================
                if (shouldDelete)
                {
                    var cancelResult = await client.UsdFuturesApi.Trading.CancelOrderAsync(
                        symbol: symbol,
                        orderId: order.Id,
                        ct: ct);

                    if (cancelResult.Success)
                        _logger.LogInformation("[CLEANER] Canceled stale order [{id}] type={type} price={price}", order.Id, order.Type, order.Price);
                    else
                        _logger.LogWarning("[CLEANER] Failed to cancel {symbol} order [{id}]: {err}", symbol, order.Id, cancelResult.Error);
                }
            }

            _logger.LogInformation("[CLEANER] END {symbol}", symbol);
        }

        private static decimal Round(decimal x, decimal tick)
        {
            if (tick <= 0) return x;
            return Math.Round(x / tick) * tick;
        }
    }
}
