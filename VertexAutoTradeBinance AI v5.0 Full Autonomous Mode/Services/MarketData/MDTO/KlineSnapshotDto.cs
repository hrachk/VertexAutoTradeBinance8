namespace VertexAutoTradeBinance8.Services.MarketData.MDTO
{
    public class KlineSnapshotDto
    {
        public DateTime OpenTime { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
    }

}
