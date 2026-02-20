using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;

public sealed class TradingOptionsResolver
{
    private readonly IOptionsMonitor<TradingOptions> _options;
    private readonly ILogger<TradingOptionsResolver> _logger;
    public TradingOptionsResolver(IOptionsMonitor<TradingOptions> options, ILogger<TradingOptionsResolver> logger)
    {
        _options = options;
        _logger = logger;
    }

    public TradingOptions Resolve(string symbol)
    {
        var baseAsset = ExtractBaseAsset(symbol).ToUpperInvariant();

        TradingOptions result;

        switch (baseAsset)
        {
            case "BTC":
                result = _options.Get("BTC");
                break;

            case "ETH":
                result = _options.Get("ETH");
                break;

            default:
                result = _options.CurrentValue; // БАЗОВЫЙ Trading
                break;
        }

        _logger.LogInformation(
            "[TRADING CONFIG] {symbol} → PROFILE={profile} LEVERAGE={lev}",
            symbol,
            baseAsset == "BTC" || baseAsset == "ETH" ? baseAsset : "BASE",
            result.Leverage);

        return result;
    }



    private static string ExtractBaseAsset(string symbol)
    {
        // BTCUSDT → BTC
        // ETHUSDT → ETH
        // XRPUSDT → XRP

        if (symbol.EndsWith("USDT"))
            return symbol[..^4];

        if (symbol.EndsWith("USDC"))
            return symbol[..^4];

        if (symbol.EndsWith("BUSD"))
            return symbol[..^4];

        return symbol;
    }
}
