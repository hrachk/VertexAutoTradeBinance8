namespace VertexAutoTradeBinance8.Models
{
    public class MarketSnapshot
    {
        public decimal TrendSlopePercent { get; set; }
        public decimal VolatilityPercent { get; set; }
        public decimal Atr { get; set; }
        public decimal Confidence { get; set; }
        // Efficiency Ratio: 1.0 — идеальный тренд, 0.0 — хаотичный шум
        public decimal EfficiencyRatio { get; set; }
    }
}
