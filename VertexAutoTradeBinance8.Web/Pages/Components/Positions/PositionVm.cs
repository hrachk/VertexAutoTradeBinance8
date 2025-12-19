namespace VertexAutoTradeBinance8.Web.Models;

public record PositionVm
(
    string Symbol,
    string Side,          // LONG / SHORT
    int Leverage,
    string MarginMode,    // Cross / Isolated

    decimal PnlUsdt,
    decimal RoiPct,

    decimal SizeUsdt,
    decimal MarginUsdt,

    decimal EntryPrice,
    decimal MarkPrice,
    decimal LiquidationPrice,
    decimal MarginRatioPct,

    int AiScore,
    string Liquidity,     // LOW / MEDIUM / HIGH
    string Regime,        // StrongTrend / Chop / Squeeze
    int RiskPct           // 0..100 (визуальный бар)
);
