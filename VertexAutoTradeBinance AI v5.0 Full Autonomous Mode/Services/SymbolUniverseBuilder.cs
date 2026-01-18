using Microsoft.Win32;
using System.Runtime.CompilerServices;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services;


/// <summary>
/// 
/// Builder не режет финальный список до 20 “втихаря”

//Builder строит кандидатов, а лимит/side-policy решает Registry

//topVolumeCount реально используется

//pinned нормализуются

//AIAUSDT фильтр — “навсегда” здесь тоже можно поставить
 
/// </summary>
public class SymbolUniverseBuilder
{
    private readonly ILogger<SymbolUniverseBuilder> _logger;

    public SymbolUniverseBuilder(ILogger<SymbolUniverseBuilder> logger)
    {
        _logger = logger;
    }

    public List<string> Build(
        List<SymbolMarketSnapshot> data,
        string[] pinned,
        int topVolumeCount,
        decimal min24hVolume,
        decimal minPrice)
    {
        pinned ??= Array.Empty<string>();

        var pinnedNorm = pinned
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => s != "TANSSIUSDT") // 🚫 forever
            .ToList();

        // === CORE LIQUIDITY (base filter) ===
        var core = data
            .Where(x => x.QuoteVolume24h >= min24hVolume && x.LastPrice >= minPrice)
            .Where(x => !string.Equals(x.Symbol, "TANSSIUSDT", StringComparison.OrdinalIgnoreCase)) // 🚫 forever
            .OrderByDescending(x => x.QuoteVolume24h)
            .Take(80) // widen funnel a bit (safe)
            .ToList();

        // === MOMENTUM (up+down) ===
        var momentum = core
            .OrderByDescending(x => Math.Abs(x.PriceChangePercent))
            .Take(30)
            .Select(x => x.Symbol)
            .ToList();

        // === LIQUIDITY top N ===
        var liquidity = core
            .OrderByDescending(x => x.QuoteVolume24h)
            .Take(Math.Max(1, topVolumeCount))
            .Select(x => x.Symbol)
            .ToList();

        // FINAL: candidates (do NOT hard-cap here)
        var final = pinnedNorm
            .Concat(momentum)
            .Concat(liquidity)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation(
            "[SYMBOL] Universe candidates built: pinned={Pinned}, momentum={Momentum}, liquidity={Liquidity}, total={Total}",
            pinnedNorm.Count,
            momentum.Count,
            liquidity.Count,
            final.Count);

        return final;
    }
}
