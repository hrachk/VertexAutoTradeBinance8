namespace VertexAutoTradeBinance8.API.Models
{
    public class ApiLiquidityGuardDto
    {
        public string Symbol { get; set; } = string.Empty;
        public string Timeframe { get; set; } = string.Empty;

        public bool Block { get; set; }

        public string Reason { get; set; } = string.Empty;

        // Details = строка, которую вернул LiquidityGuardResult
        public string Details { get; set; } = string.Empty;
    }
}
