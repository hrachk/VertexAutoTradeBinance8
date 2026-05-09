using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Helpers;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.API.Models;

namespace VertexAutoTradeBinance8.API.Facades
{
    public class LiquidityFacade
    {
        private readonly MarketDataService _market;
        private readonly LiquidityGuardService _guard;
        private readonly AiLiquidityClusterService _cluster;
        private readonly BinanceOptions _binance;
        private readonly TradingOptions _trade;

        public LiquidityFacade(
            MarketDataService market,
            LiquidityGuardService guard,
            AiLiquidityClusterService cluster,
            IOptions<BinanceOptions> binance,
            IOptions<TradingOptions> trading)
        {
            _market = market;
            _guard = guard;
            _cluster = cluster;
            _binance = binance.Value;
            _trade = trading.Value;
        }

        public async Task<ApiLiquidityGuardDto?> GuardAsync(string? symbol, string? timeframe, SignalSide side)
        {
            symbol ??= _binance.Symbols.First();
            var tfStr = timeframe ?? _trade.TimeframeMinutes.ToTimeframeString();
            var tf = tfStr.ToKlineInterval();

            var klines = await _market.GetKlines(symbol, tf, 200);
            if (klines.Count == 0)
                return null;

            var g = _guard.Analyze(symbol, tf, klines, side, false);

            return new ApiLiquidityGuardDto
            {
                Symbol = symbol,
                Timeframe = tf.ToString(),
                Block = g.Block,
                Reason = g.Reason.ToString(),
                Details = g.Details
            };
        }

        public ApiClusterFilterResultDto FilterEntry(TradeSignal signal)
        {
            var filtered = _cluster.FilterAndAdjust(signal);

            if (filtered == null)
            {
                return new ApiClusterFilterResultDto
                {
                    Symbol = signal.Symbol,
                    Blocked = true,
                    Adjusted = false
                };
            }

            return new ApiClusterFilterResultDto
            {
                Symbol = filtered.Symbol,
                Blocked = false,
                Adjusted = filtered.EntryPrice != signal.EntryPrice ||
                           filtered.StopLoss != signal.StopLoss,
                EntryOld = signal.EntryPrice,
                EntryNew = filtered.EntryPrice,
                SlOld = signal.StopLoss,
                SlNew = filtered.StopLoss
            };
        }
    }
}
