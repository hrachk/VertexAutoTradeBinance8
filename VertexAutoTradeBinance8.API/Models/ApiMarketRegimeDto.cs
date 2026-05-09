namespace VertexAutoTradeBinance8.API.Models
{
    public class ApiMarketRegimeDto
    {
        public string Symbol { get; set; } = string.Empty;
        public string Timeframe { get; set; } = string.Empty;

        public string Regime { get; set; } = string.Empty;

        public decimal SlopePercent { get; set; }
        public decimal VolatilityPercent { get; set; }
        public decimal DeviationScore { get; set; }
    }
}
