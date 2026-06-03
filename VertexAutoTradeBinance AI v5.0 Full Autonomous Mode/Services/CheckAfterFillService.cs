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
        private readonly TradeSignalMemoryService _memory;

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
                    _logger.LogError(ex, "[CHECK-FILL] Fatal error");
                }

                await Task.Delay(3000, stoppingToken);
            }
        }

        private async Task ScanAllSymbols(CancellationToken ct)
        {
            // Получаем символы из памяти сигналов — не грузим ВСЕ позиции аккаунта
            var trackedSymbols = _memory.GetTrackedSymbols();
            if (trackedSymbols == null || !trackedSymbols.Any())
                return;

            using var client = _factory.CreateRestClient();

            foreach (var symbol in trackedSymbols)
            {
                if (ct.IsCancellationRequested) return;

                // ✅ Запрашиваем позицию по конкретному символу (weight=1)
                var posRes = await client.UsdFuturesApi.Account
                    .GetPositionInformationAsync(symbol: symbol, ct: ct);

                if (!posRes.Success || posRes.Data == null)
                    continue;

                foreach (var pos in posRes.Data)
                {
                    var qty = Math.Abs(pos.Quantity);
                    if (qty <= 0) continue;

                    var side = pos.PositionSide;

                    var signal = _memory.GetLastSignal(symbol);
                    if (signal == null) continue;

                    var openOrders = await client.UsdFuturesApi.Trading
                        .GetOpenOrdersAsync(symbol, ct: ct);
                    if (!openOrders.Success) continue;

                    bool hasSL = openOrders.Data.Any(o =>
                        o.Type is FuturesOrderType.Stop or FuturesOrderType.StopMarket &&
                        o.PositionSide == side);

                    // ✅ TP определяем по Type + PositionSide (корректно для Hedge mode)
                    bool hasTP = openOrders.Data.Any(o =>
                        o.Type is FuturesOrderType.TakeProfit
                               or FuturesOrderType.TakeProfitMarket
                               or FuturesOrderType.Limit &&
                        o.PositionSide == side);

                    if (hasSL && hasTP)
                        continue;

                    _logger.LogWarning(
                        "[CHECK-FILL] {symbol} {side}: position open but SL={hasSL} TP={hasTP} → placing missing orders",
                        symbol, side, hasSL, hasTP);

                    await PlaceMissingOrders(symbol, qty, side, signal, hasSL, hasTP, ct);
                }
            }
        }

        private async Task PlaceMissingOrders(
            string symbol,
            decimal qty,
            PositionSide side,
            TradeSignal signal,
            bool hasSL,
            bool hasTP,
            CancellationToken ct)
        {
            using var client = _factory.CreateRestClient();

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol, ct: ct);
            decimal step = filters.step     <= 0 ? 0.001m  : filters.step;
            decimal tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            decimal Round(decimal x, decimal t) =>
                t <= 0 ? x : Math.Round(x / t) * t;

            var slSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            // ================================
            // STOP LOSS
            // ================================
            if (!hasSL)
            {
                decimal slTrig  = Round(signal.StopLoss, tick);
                decimal slLimit = side == PositionSide.Long
                    ? Round(slTrig - tick, tick)
                    : Round(slTrig + tick, tick);

                var slOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol:                   symbol,
                    side:                     slSide,
                    type:                     FuturesOrderType.Stop,
                    quantity:                 qty,
                    stopPrice:                slTrig,
                    price:                    slLimit,
                    timeInForce:              TimeInForce.GoodTillCanceled,
                    positionSide:             side,
                    selfTradePreventionMode:  SelfTradePreventionMode.ExpireMaker,
                    ct:                       ct);

                if (!slOrder.Success)
                    _logger.LogError("[CHECK-FILL] SL place FAILED {symbol} {side}: {err}", symbol, side, slOrder.Error?.Message);
                else
                    _logger.LogInformation("[CHECK-FILL] SL placed OK {symbol} {side} sl={sl}", symbol, side, slTrig);
            }

            // ================================
            // TAKE PROFITS
            // ================================
            if (!hasTP)
            {
                if (signal.TakeProfits == null || signal.TakeProfits.Count == 0)
                    return;

                var tpSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
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

                    var tpOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol:                   symbol,
                        side:                     tpSide,
                        type:                     FuturesOrderType.Limit,
                        quantity:                 tpQty,
                        price:                    tpPrice,
                        timeInForce:              TimeInForce.GoodTillCanceled,
                        positionSide:             side,
                        selfTradePreventionMode:  SelfTradePreventionMode.ExpireMaker,
                        ct:                       ct);

                    if (!tpOrder.Success)
                        _logger.LogError("[CHECK-FILL] TP{n} place FAILED {symbol}: {err}", i + 1, symbol, tpOrder.Error?.Message);
                    else
                    {
                        totalPlanned += tpQty;
                        _logger.LogInformation("[CHECK-FILL] TP{n} placed OK {symbol} price={price} qty={qty}", i + 1, symbol, tpPrice, tpQty);
                    }

                    if (totalPlanned >= qty) break;
                }
            }
        }
    }
}
