using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class CheckAfterFillService : BackgroundService
    {
        private readonly ILogger<CheckAfterFillService> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly SymbolInfoService _symbolInfo;
        private readonly TradeSignalMemoryService _memory; // хранит сигналы

        public CheckAfterFillService(
            ILogger<CheckAfterFillService> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            TradeSignalMemoryService memory)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
            _memory = memory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ScanAllSymbols(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "CheckAfterFillService fatal error");
                }

                await Task.Delay(3000, stoppingToken); // 3 сек
            }
        }

        private async Task ScanAllSymbols(CancellationToken ct)
        {
            using var client = _factory.CreateRestClient();
            var positions = await client.UsdFuturesApi.Account.GetPositionInformationAsync();

            if (!positions.Success || positions.Data == null)
                return;

            foreach (var pos in positions.Data)
            {
                if (ct.IsCancellationRequested)
                    return;

                var qty = Math.Abs(pos.Quantity);
                if (qty <= 0) continue;

                var symbol = pos.Symbol;
                var side = pos.PositionSide;

                // ищем последний сигнал
                var signal = _memory.GetLastSignal(symbol);
                if (signal == null) continue;

                // проверяем, есть ли активные SL/TP
                var openOrders = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol);
                if (!openOrders.Success) continue;

                bool hasSL = openOrders.Data.Any(o => o.Type == FuturesOrderType.Stop || o.Type == FuturesOrderType.StopMarket);
                bool hasTP = openOrders.Data.Any(o => o.Type == FuturesOrderType.Limit);

                if (hasSL && hasTP)
                    continue;

                _logger.LogWarning($"[FIX-TP/SL] {symbol}: POSITION OPEN but SL/TP missing → creating...");

                await PlaceMissingOrders(symbol, qty, side, signal, ct);
            }
        }

        private async Task PlaceMissingOrders(
            string symbol,
            decimal qty,
            PositionSide side,
            TradeSignal signal,
            CancellationToken ct)
        {
            using var client = _factory.CreateRestClient();

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            decimal step = filters.step <= 0 ? 0.001m : filters.step;
            decimal tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            decimal Round(decimal x, decimal t) =>
                t <= 0 ? x : Math.Round(x / t) * t;

            // ================================
            // STOP LOSS
            // ================================
            decimal slTrig = Round(signal.StopLoss, tick);
            decimal slLimit = side == PositionSide.Long
                ? Round(slTrig - tick, tick)
                : Round(slTrig + tick, tick);

            var slSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var slOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol,
                slSide,
                FuturesOrderType.Stop,
                qty,
                stopPrice: slTrig,
                price: slLimit,
                timeInForce: TimeInForce.GoodTillCanceled,
                positionSide: side
            );

            if (!slOrder.Success)
            {
                _logger.LogError($"❌ [FIX SL ERROR] {symbol}: {slOrder.Error?.Message}");
            }
            else
            {
                _logger.LogInformation($"✔ FIX SL placed: {symbol} sl={slTrig}");
            }

            // ================================
            // TAKE PROFITS
            // ================================
            if (signal.TakeProfits == null || signal.TakeProfits.Count == 0)
                return;

            decimal totalPlanned = 0m;

            for (int i = 0; i < signal.TakeProfits.Count; i++)
            {
                decimal tpPart = signal.GetTpPart(i);
                if (tpPart <= 0) continue;

                decimal tpQty = Math.Floor((qty * tpPart) / step) * step;
                if (tpQty <= 0) continue;

                if (totalPlanned + tpQty > qty)
                    tpQty = Math.Floor((qty - totalPlanned) / step) * step;

                if (tpQty <= 0) continue;

                decimal tpPrice = Round(signal.TakeProfits[i], tick);

                var tpSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

                var tpOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol,
                    tpSide,
                    FuturesOrderType.Limit,
                    tpQty,
                    price: tpPrice,
                    timeInForce: TimeInForce.GoodTillCanceled,
                    positionSide: side
                );

                if (!tpOrder.Success)
                {
                    _logger.LogError($"❌ FIX TP{i + 1} ERROR {symbol}: {tpOrder.Error?.Message}");
                }
                else
                {
                    totalPlanned += tpQty;
                    _logger.LogInformation($"✔ FIX TP{i + 1}: {tpPrice} qty={tpQty}");
                }

                if (totalPlanned >= qty) break;
            }
        }
    }
}
