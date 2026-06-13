using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Models.HTF;

namespace VertexAutoTradeBinance8.Services;

public sealed class MarketContextService
{
    private readonly MarketDataFacade _market;
    private readonly AiMarketRegimeService _regime;
    private readonly ILogger<MarketContextService> _logger;

    private const int ContextBars = 120;

    public MarketContextService(
        MarketDataFacade market,
        AiMarketRegimeService regime,
        ILogger<MarketContextService> logger)
    {
        _market = market;
        _regime = regime;
        _logger = logger;
    }

    public async Task<MarketContextSnapshot> GetContextAsync(
        string symbol,
        CancellationToken ct)
    {
        var r1h = await Read(symbol, KlineInterval.OneHour, ct);
        var r4h = await Read(symbol, KlineInterval.FourHour, ct);
        var r1d = await Read(symbol, KlineInterval.OneDay, ct);

        var bias = ResolveBias(r1d, r4h);
        var aligned = r1h != MarketRegime.Unknown && r1h == r4h && r4h == r1d;

        var highVol =
            r1h == MarketRegime.VolatileChop ||
            r4h == MarketRegime.VolatileChop ||
            r1d == MarketRegime.VolatileChop;

        var manipRisk = highVol && !aligned;

        var confBoost = 1.0m
            + (aligned ? 0.15m : 0m)
            + (r4h != MarketRegime.Unknown && r4h == r1h ? 0.10m : 0m);

        var riskBias = manipRisk ? 0.85m : 1.0m;

        Log(symbol, r1h, r4h, r1d, bias, confBoost, riskBias, aligned, highVol, manipRisk);

        return new MarketContextSnapshot
        {
            Regime1H = r1h,
            Regime4H = r4h,
            Regime1D = r1d,
            Bias = bias,
            ConfidenceBoost = confBoost,
            RiskBias = riskBias,
            MultiTFAligned = aligned,
            HighVolatilityContext = highVol,
            ManipulationRisk = manipRisk
        };
    }

    private async Task<MarketRegime> Read(string symbol, KlineInterval tf, CancellationToken ct)
    {
        try
        {
            // Если таймфрейм в warmup — не блокируем, возвращаем Neutral (не Unknown)
            // Unknown ломает ResolveBias и aligned проверку
            if (_market.IsInWarmup(symbol, tf))
                return MarketRegime.Range; // Range = neutral, не блокирует

            var kl = await _market.GetKlinesAsync(symbol, tf, ContextBars, ct);
            if (kl == null || kl.Count < 20) // снижено с 50 до 20
                return MarketRegime.Range;

            return _regime.DetectRegime(symbol, tf, kl).Regime;
        }
        catch
        {
            return MarketRegime.Range;
        }
    }

    private static MarketBias ResolveBias(MarketRegime r1d, MarketRegime r4h)
    {
        // Unknown/Range = нейтральный, не блокируем
        var upRegimes   = new[]{ MarketRegime.StrongUpTrend };
        var downRegimes = new[]{ MarketRegime.StrongDownTrend };

        bool d1Up   = upRegimes.Contains(r1d);
        bool d1Down = downRegimes.Contains(r1d);
        bool h4Up   = upRegimes.Contains(r4h);
        bool h4Down = downRegimes.Contains(r4h);

        if (d1Up   && h4Up)   return MarketBias.LongOnly;
        if (d1Down && h4Down) return MarketBias.ShortOnly;

        // Всё остальное — нейтрально (включая Unknown, Range, conflicting)
        return MarketBias.Neutral;
    }


    private void Log(
        string s, MarketRegime r1h, MarketRegime r4h, MarketRegime r1d,
        MarketBias bias, decimal cb, decimal rb,
        bool aligned, bool hv, bool mr)
    {
        _logger.LogInformation(
$"""
────────────────────────────────────────────
🌍 Market Context: {s}
1H:{r1h}  4H:{r4h}  1D:{r1d}
Bias={bias}  ConfBoost=×{cb:F2}  RiskBias=×{rb:F2}
Aligned={aligned}  HighVol={hv}  ManipRisk={mr}
────────────────────────────────────────────
""");
    }
}
