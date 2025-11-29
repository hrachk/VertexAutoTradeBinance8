using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Helpers;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.API.Models;

namespace VertexAutoTradeBinance8.API.Facades
{
    public class MarketRegimeFacade
    {
        private readonly MarketDataService _market;
        private readonly AiMarketRegimeService _regime;
        private readonly BinanceOptions _binance;
        private readonly TradingOptions _trade;

        public MarketRegimeFacade(
            MarketDataService market,
            AiMarketRegimeService regime,
            IOptions<BinanceOptions> binance,
            IOptions<TradingOptions> trading)
        {
            _market = market;
            _regime = regime;
            _binance = binance.Value;
            _trade = trading.Value;
        }

        public async Task<ApiMarketRegimeDto?> GetRegimeAsync(string? symbol, string? timeframe)
        {
            symbol ??= _binance.Symbols.FirstOrDefault() ?? "BTCUSDT";

            var tfStr = timeframe ?? _trade.TimeframeMinutes.ToTimeframeString();
            var tf = tfStr.ToKlineInterval();

            var klines = await _market.GetKlines(symbol, tf, 200);
            if (klines.Count == 0)
                return null;

            var r = _regime.DetectRegime(symbol, tf, klines);

            return new ApiMarketRegimeDto
            {
                Symbol = r.Symbol,
                Timeframe = r.Timeframe,
                Regime = r.Regime.ToString(),
                SlopePercent = r.TrendSlopePercent,
                DeviationScore = r.DeviationScore
            };
        }
    }
}
