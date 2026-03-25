namespace VertexAutoTradeBinance8.Configuration
{
    public class TradingSettings
    {
        public List<string> StrongTrendAssets { get; set; } = new();
        public List<string> ToxicSymbols { get; set; } = new();
    }
}
