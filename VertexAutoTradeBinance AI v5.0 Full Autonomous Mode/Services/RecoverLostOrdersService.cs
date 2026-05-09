using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services
{
    public class RecoverLostOrdersService : BackgroundService
    {
        private readonly ILogger<RecoverLostOrdersService> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly OrderTracerService _tracer;

        public RecoverLostOrdersService(
            ILogger<RecoverLostOrdersService> logger,
            BinanceClientFactory factory,
            OrderTracerService tracer)
        {
            _logger = logger;
            _factory = factory;
            _tracer = tracer;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ScanAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[RECOVER] Fatal error in RecoverLostOrdersService");
                }

                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); // каждые 10 сек
            }
        }

        private async Task ScanAsync(CancellationToken ct)
        {
            var symbols = _tracer.GetAllSymbols();
            if (symbols.Count == 0)
                return;

            using var client = _factory.CreateRestClient();

            // берём все позиции разом
            var posRes = await client.UsdFuturesApi.Account.GetPositionInformationAsync();
            if (!posRes.Success || posRes.Data == null)
            {
                _logger.LogWarning("[RECOVER] Failed to load positions: {err}", posRes.Error);
                return;
            }

            var allPos = posRes.Data.ToList();

            foreach (var symbol in symbols)
            {
                ct.ThrowIfCancellationRequested();

                var longPos = allPos.FirstOrDefault(p =>
                    p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) &&
                    p.PositionSide == PositionSide.Long);

                var shortPos = allPos.FirstOrDefault(p =>
                    p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) &&
                    p.PositionSide == PositionSide.Short);

                decimal longQty = longPos != null ? Math.Abs(longPos.Quantity) : 0m;
                decimal shortQty = shortPos != null ? Math.Abs(shortPos.Quantity) : 0m;

                var ordersRes = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol);
                if (!ordersRes.Success || ordersRes.Data == null)
                    continue;

                var openOrders = ordersRes.Data.ToList();
                var knownIds = _tracer.GetKnownOrderIds(symbol);

                foreach (var o in openOrders)
                {
                    bool isBotOrder = knownIds.Contains(o.Id);
                    if (!isBotOrder)
                        continue; // чужие/ручные не трогаем

                    bool noPosForSide =
                        (o.PositionSide == PositionSide.Long && longQty == 0m) ||
                        (o.PositionSide == PositionSide.Short && shortQty == 0m) ||
                        (o.PositionSide == PositionSide.Both && longQty == 0m && shortQty == 0m);

                    if (!noPosForSide)
                        continue;

                    // Наш ордер, но по стороне уже НЕТ позиции → отменяем.
                    var cancel = await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, o.Id);
                    if (cancel.Success)
                    {
                        _logger.LogWarning(
                            "[RECOVER] Canceled orphan order {symbol} id={id} side={side} type={type}",
                            symbol, o.Id, o.Side, o.Type);
                        _tracer.Remove(symbol, o.Id);
                    }
                    else
                    {
                        _logger.LogError(
                            "[RECOVER] Failed cancel orphan order {symbol} id={id}: {err}",
                            symbol, o.Id, cancel.Error);
                    }
                }

                // Если по символу уже нет ни позиций, ни ордеров бота — очищаем память
                if (longQty == 0m && shortQty == 0m)
                {
                    var openKnown = openOrders.Any(o => knownIds.Contains(o.Id));
                    if (!openKnown)
                        _tracer.ClearSymbol(symbol);
                }
            }
        }
    }
}
