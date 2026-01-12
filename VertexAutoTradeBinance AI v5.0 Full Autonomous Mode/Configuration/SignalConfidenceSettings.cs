namespace VertexAutoTradeBinance8.Configuration;

public class SignalConfidenceSettings
{
    public decimal MinEntry { get; set; }

    public BandsSettings Bands { get; set; } = new();
    public EarlyTpAtrSettings EarlyTpAtr { get; set; } = new();

    public class BandsSettings
    {
        public decimal MediumFrom { get; set; }
        public decimal HighFrom { get; set; }
    }

    public class EarlyTpAtrSettings
    {
        public decimal Medium { get; set; }
        public decimal High { get; set; }
    }
}
