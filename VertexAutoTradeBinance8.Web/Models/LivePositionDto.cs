namespace VertexAutoTradeBinance8.Web.Models
{
    public sealed class LivePositionDto
    {
        public string Symbol { get; set; } = "";
        public string Side { get; set; } = ""; // LONG / SHORT

        public decimal Quantity { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal MarkPrice { get; set; }

        public decimal UnrealizedPnl { get; set; }
        public decimal PnlPercent { get; set; }

        // Protection / supervisor state
        public bool IsProtected { get; set; }
        public DateTime? LastProtectionUtc { get; set; }

        // Harvest / bucket
        public decimal ProfitBucketUsd { get; set; }
        public int HarvestsToday { get; set; }
    }
}
