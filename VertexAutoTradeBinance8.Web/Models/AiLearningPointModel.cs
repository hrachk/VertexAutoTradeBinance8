namespace VertexAutoTradeBinance8.Web.Models;

public class AiLearningPointModel
{
    public DateTime Time { get; set; }
    public string Symbol { get; set; } = string.Empty;

    public decimal Score { get; set; }          // 0–100
    public decimal Slope { get; set; }          // процентовое изменение, как у тебя
    public decimal Volatility { get; set; }     // VolatilityPercent (0.01 = 1 %)
    public bool LiquidityDanger { get; set; }   // true/false
    public decimal Confidence { get; set; }     // 0–1 от SmartRegime
}
