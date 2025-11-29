using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Helpers;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;

public class MarketRegimeFacade
{
    private readonly MarketDataService _data;
    private readonly AiMarketRegimeService _regime;
    private readonly TradingOptions _opts;

    public MarketRegimeFacade(
        MarketDataService data,
        AiMarketRegimeService regime,
        IOptions<TradingOptions> opts)
    {
        _data = data;
        _regime = regime;
        _opts = opts.Value;
    }

    public async Task<MarketRegimeResult> GetRegimeAsync(string symbol, string? tf)
    {
        var tfStr = tf ?? _opts.TimeframeMinutes.ToTimeframeString();
        var interval = tfStr.ToKlineInterval();

        var klines = await _data.GetKlines(symbol, interval, 200);
        if (klines.Count == 0)
            throw new Exception("No klines");

        return _regime.DetectRegime(symbol, interval, klines);
    }
}
