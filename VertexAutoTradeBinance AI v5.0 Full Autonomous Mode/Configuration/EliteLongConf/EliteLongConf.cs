namespace VertexAutoTradeBinance8.Configuration.EliteLongConf
{
    class ElitePosition
    {
        public string Symbol;
        public decimal AvgEntry;
        public decimal Quantity;
        public decimal Atr;

        public int AddsDone;

        public List<bool> TpHit;
    }
    public class EliteLongConfig
    {
        public bool Enabled { get; set; }
        public HashSet<string> Symbols { get; set; } = new();

        public int MaxAdds { get; set; }

        public DcaConfig Dca { get; set; } = new();
        public TpConfig TakeProfit { get; set; } = new();
        public RiskConfig Risk { get; set; } = new();
    }

    public class DcaConfig
    {
        public List<decimal> AtrMultipliers { get; set; } = new();
        public List<decimal> SizeMultipliers { get; set; } = new();
        public decimal MaxPositionMultiplier { get; set; }
    }

    public class TpConfig
    {
        public List<decimal> Levels { get; set; } = new();
        public List<decimal> SizePercents { get; set; } = new();
        public bool UseLimitOrders { get; set; }
        public decimal PrePlaceDistancePct { get; set; }
    }

    public class RiskConfig
    {
        public decimal MaxDrawdownPercent { get; set; }
        public decimal MinTrendStrength { get; set; }
        public decimal MinConfidence { get; set; }
        public decimal LiquidationBufferPct { get; set; }
    }
}
