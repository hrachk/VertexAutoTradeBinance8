using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services;

public enum AdaptiveMode
{
    Breakout,
    Pullback,
    LiquidityGrab,
    Disabled
}

public record AdaptiveDecision(
    AdaptiveMode Mode,
    decimal Confidence,
    string Reason);

public class AdaptiveStrategyService
{
    private readonly ILogger<AdaptiveStrategyService> _logger;

    public AdaptiveStrategyService(ILogger<AdaptiveStrategyService> logger)
    {
        _logger = logger;
    }

    public AdaptiveDecision Decide(
        MarketRegimeResult regime,
        LiquidityAnalysisResult? liquidity,
        TradeSignal? signal)
    {
        // ======================================================
        // 1. ОПАСНАЯ ЛИКВИДНОСТЬ — запрет входа
        // ======================================================
        if (liquidity != null && liquidity.IsDangerZone)
        {
            return new AdaptiveDecision(
                AdaptiveMode.Disabled,
                1.00m,
                $"Liquidity danger: {liquidity.Reason ?? "Unknown"}"
            );
        }

        // ======================================================
        // 2. БОКОВИК — ИГРАЕМ LIQUIDITY GRAB
        // ======================================================
        if (regime.Regime == MarketRegime.Range)
        {
            return new AdaptiveDecision(
                AdaptiveMode.LiquidityGrab,
                0.85m,
                "Market in RANGE → Using Liquidity Grab");
        }

        // ======================================================
        // 3. СИЛЬНЫЙ ТРЕНД — BREAKOUT
        // ======================================================
        if (regime.Regime == MarketRegime.StrongUpTrend ||
            regime.Regime == MarketRegime.StrongDownTrend)
        {
            return new AdaptiveDecision(
                AdaptiveMode.Breakout,
                0.95m,
                "Strong Trend → Breakout entry priority");
        }

        // ======================================================
        // 4. ВОЛАТИЛЬНЫЙ ХАОС — ЗАПРЕТ
        // ======================================================
        if (regime.Regime == MarketRegime.VolatileChop)
        {
            return new AdaptiveDecision(
                AdaptiveMode.Disabled,
                1.00m,
                "Volatile Chop → Trading Disabled");
        }

        // ======================================================
        // 5. УМЕРЕННЫЙ ТРЕНД — PULLBACK EMA21
        // ======================================================
        if (regime.Regime == MarketRegime.Unknown ||
            regime.Regime == MarketRegime.Unknown)
        {
            return new AdaptiveDecision(
                AdaptiveMode.Pullback,
                0.75m,
                "Moderate/Unknown trend → EMA Pullback");
        }

        // ======================================================
        // 6. СТАНДАРТНОЕ ПОВЕДЕНИЕ (на всякий случай)
        // ======================================================
        return new AdaptiveDecision(
            AdaptiveMode.Pullback,
            0.70m,
            "Fallback → EMA Pullback");
    }
}
