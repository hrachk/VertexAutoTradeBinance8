using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services;

public class SymbolUniverseBuilder
{
    private readonly ILogger<SymbolUniverseBuilder> _logger;

    public SymbolUniverseBuilder(ILogger<SymbolUniverseBuilder> logger)
    {
        _logger = logger;
    }

    private static readonly HashSet<string> _blacklist =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "POWERUSDT",
        "QUSDT",
        "RIVERUSDT",
        "ARCUSDT",
        "BEATUSDT",
        "TANSSIUSDT",
        "OPUSDT",
        "ROBOUSDT",
        "MYXUSDT",
        "AIAUSDT",
        "FIOUSDT",
        "SAHARAUSDT",
        "HUMAUSDT",
        "SIRENUSDT",
        "DENTUSDT",
        "SIGNUSDT",
        "BANANAS31USDT"
    };

    public List<string> Build(
        List<SymbolMarketSnapshot> data,
        string[] pinned,
        int topVolumeCount,
        decimal min24hVolume,
        decimal minPrice,
        decimal momentumCapPercent)
    {
        if (data == null || data.Count == 0)
        {
            _logger.LogWarning("[SYMBOL] Market snapshot empty");
            return new List<string>();
        }

        pinned ??= Array.Empty<string>();

        // ------------------------------------------------
        // PINNED NORMALIZATION
        // ------------------------------------------------
        var pinnedNorm = pinned
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => !_blacklist.Contains(s))
            .Distinct()
            .ToList();

        // ------------------------------------------------
        // CORE MARKET FILTER
        // ------------------------------------------------
        var filteredMarket = data
            .Where(x => x.QuoteVolume24h >= min24hVolume)
            .Where(x => x.LastPrice >= minPrice)
            .Where(x => !_blacklist.Contains(x.Symbol))
            .ToList();

        // ------------------------------------------------
        // CORE LIQUIDITY FUNNEL
        // ------------------------------------------------
        var core = filteredMarket
            .OrderByDescending(x => x.QuoteVolume24h)
            .Take(80)
            .ToList();

        // ------------------------------------------------
        // LIQUIDITY SELECTION
        // ------------------------------------------------
        var liquidity = core
            .OrderByDescending(x => x.QuoteVolume24h)
            .Take(Math.Max(1, topVolumeCount))
            .Select(x => x.Symbol)
            .ToList();

        // ------------------------------------------------
        // MOMENTUM SELECTION (FULL MARKET)
        // ------------------------------------------------
        var momentumRaw = filteredMarket
            .OrderByDescending(x => Math.Abs(x.PriceChangePercent))
            .Select(x => x.Symbol)
            .ToList();

        int momentumCap = (int)Math.Ceiling(topVolumeCount * (momentumCapPercent / 100m));

        if (momentumCap < 1)
            momentumCap = 1;

        var momentum = momentumRaw
            .Take(momentumCap)
            .ToList();

        // ------------------------------------------------
        // FINAL LIST
        // ------------------------------------------------
        var final = pinnedNorm
            .Concat(liquidity)
            .Concat(momentum)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ------------------------------------------------
        // LOGGING
        // ------------------------------------------------
        _logger.LogInformation(
            "[SYMBOL] Blacklist active: {count}",
            _blacklist.Count);

        _logger.LogInformation(
            "[SYMBOL] Universe built pinned={Pinned} liquidity={Liquidity} momentum={Momentum} total={Total}",
            pinnedNorm.Count,
            liquidity.Count,
            momentum.Count,
            final.Count);

        return final;
    }
}