using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;

public sealed class TradingOptionsResolver
{
    private readonly IOptionsMonitor<TradingOptions> _options;

    public TradingOptionsResolver(IOptionsMonitor<TradingOptions> options)
    {
        _options = options;
    }

    public TradingOptions Resolve(string symbol)
    {
        if (symbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase))
            return _options.Get("BTC");

        if (symbol.StartsWith("ETH", StringComparison.OrdinalIgnoreCase))
            return _options.Get("ETH");

        return _options.Get("default");
    }
}
