namespace VertexAutoTradeBinance8.Models
{
    public class MarketSnapshot
    {
        public decimal TrendSlopePercent { get; set; }
        public decimal VolatilityPercent { get; set; }
        public decimal Atr { get; set; }
        public decimal Confidence { get; set; }
    }
}
