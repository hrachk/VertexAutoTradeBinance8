using Microsoft.Extensions.Options;

namespace VertexAutoTradeBinance8.Configuration;

public sealed class SignalConfidenceSettings
{
    public ConfidenceProfile Default { get; set; } = new();
    public ConfidenceProfile BTC { get; set; } = new();
    public ConfidenceProfile ETH { get; set; } = new();

    public class ConfidenceProfile
    {
        public decimal MinEntry { get; set; }
        public BandsSettings Bands { get; set; } = new();
        public EarlyTpAtrSettings EarlyTpAtr { get; set; } = new();
    }

    public class BandsSettings
    {
        public decimal MediumFrom { get; set; }
        public decimal HighFrom { get; set; }
    }

    public class EarlyTpAtrSettings
    {
        public decimal Medium { get; set; }
        public decimal High { get; set; }
    }
}

public sealed class ConfidenceResolver
{
    private readonly IOptionsMonitor<SignalConfidenceSettings> _options;

    public ConfidenceResolver(IOptionsMonitor<SignalConfidenceSettings> options)
    {
        _options = options;
    }

    public SignalConfidenceSettings.ConfidenceProfile Resolve(string symbol)
    {
        symbol = symbol.ToUpperInvariant();
        var cfg = _options.CurrentValue;

        if (symbol.StartsWith("BTC"))
            return cfg.BTC;

        if (symbol.StartsWith("ETH"))
            return cfg.ETH;

        return cfg.Default;
    }
}
