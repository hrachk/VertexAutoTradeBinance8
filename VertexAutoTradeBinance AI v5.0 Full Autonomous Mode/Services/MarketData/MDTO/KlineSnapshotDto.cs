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

        // Binance's own kline response already includes this directly
        // (no extra API call needed) — the portion of Volume that was
        // bought by takers (market buys hitting the ask). Sell-side
        // volume is simply Volume - TakerBuyBaseVolume. Nullable so
        // older already-written snapshot files (without this field)
        // still deserialize correctly.
        public decimal? TakerBuyBaseVolume { get; set; }
    }

}
