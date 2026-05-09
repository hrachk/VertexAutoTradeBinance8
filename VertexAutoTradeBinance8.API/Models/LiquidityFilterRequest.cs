namespace VertexAutoTradeBinance8.API.Models
{
    public class LiquidityFilterRequest
    {
        public string Symbol { get; set; } = string.Empty;
        public VertexAutoTradeBinance8.Models.SignalSide Side { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal StopLoss { get; set; }
        public string? Timeframe { get; set; } // опционально, чисто для инфо
    }
}
