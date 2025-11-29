using Binance.Net.Enums;
using System.Linq;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class PositionProtectorService
    {
        private readonly ILogger<PositionProtectorService> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly SymbolInfoService _symbolInfo;
        private readonly AiSelfLearningService _aiLearning;
        private readonly TradeResultMonitorService _tradeMonitor;

        public PositionProtectorService(
            ILogger<PositionProtectorService> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            AiSelfLearningService aiLearning,
            TradeResultMonitorService tradeMonitor)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
            _aiLearning = aiLearning;
            _tradeMonitor = tradeMonitor;
        }

        public async Task<bool> AutoExitIfDangerAsync(
            string symbol,
            decimal dangerPrice,
            PositionSide side)
        {
            using var client = _factory.CreateRestClient();

            var pos = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol);
            if (!pos.Success || pos.Data == null)
                return false;

            var p = pos.Data.FirstOrDefault(x => x.PositionSide == side);
            if (p == null || Math.Abs(p.Quantity) <= 0)
                return false;

            var qty = Math.Abs(p.Quantity);

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            var tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            decimal exitPrice;

            if (side == PositionSide.Long)
            {
                if (p.MarkPrice > dangerPrice)
                    return false;

                exitPrice = Math.Round(dangerPrice / tick) * tick;
            }
            else
            {
                if (p.MarkPrice < dangerPrice)
                    return false;

                exitPrice = Math.Round(dangerPrice / tick) * tick;
            }

            var exitSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var exitOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol,
                side: exitSide,
                type: FuturesOrderType.Limit,
                reduceOnly: true,
                timeInForce: TimeInForce.GoodTillCanceled,
                quantity: qty,
                price: exitPrice,
                positionSide: side);

            if (!exitOrder.Success)
                return false;

            bool isWin = side == PositionSide.Long
                ? exitPrice > p.EntryPrice
                : exitPrice < p.EntryPrice;

            // ================================
            //      🔥 AI SELF LEARNING (B-PRO)
            // ================================
            _aiLearning.RecordTrade(
                symbol: symbol,
                entryPrice: p.EntryPrice,
                exitPrice: exitPrice,
                liquidationPrice: p.LiquidationPrice,
                isWin: isWin,
                regime: MarketRegime.Range   // безопасный дефолт
            );

            _logger.LogWarning(
               "AUTO-EXIT {Symbol}: exit={Exit} win={Win}",
               symbol, exitPrice, isWin);



            return true;
        }
    }
}
