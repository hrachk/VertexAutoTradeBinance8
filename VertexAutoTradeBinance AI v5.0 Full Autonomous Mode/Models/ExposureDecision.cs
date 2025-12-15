namespace VertexAutoTradeBinance8.Models
{
    public class ExposureDecision
    {
        public bool AllowAdd { get; set; }
        public bool UseProfitBucket { get; set; }
        public decimal AllowedAddUsd { get; set; }
        public string Reason { get; set; } = "";
        public decimal SymbolCapPct { get; set; }
    }
}
