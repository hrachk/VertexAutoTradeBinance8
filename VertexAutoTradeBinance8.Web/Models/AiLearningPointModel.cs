namespace VertexAutoTradeBinance8.Web.Models;

public class AiLearningPointModel
{
    public DateTime Time { get; set; }
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Display quality score 0–100 (for UI, not a trade gate).</summary>
    public decimal Score { get; set; }

    public decimal Slope { get; set; }
    public decimal Volatility { get; set; }
    public bool LiquidityDanger { get; set; }

    /// <summary>0–1 confidence from engine MarketState.</summary>
    public decimal Confidence { get; set; }

    /// <summary>Engine regime label (StrongUpTrend, Range, …).</summary>
    public string Regime { get; set; } = "Unknown";

    public decimal PulseValue { get; set; }
}
