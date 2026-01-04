using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services.MarketState
{
    public sealed class MarketStateSnapshot
    {
        public string Symbol { get; set; } = default!;
        public string Timeframe { get; set; } = default!;

        public DateTime LastCloseTimeUtc { get; set; }

        public decimal Ema21 { get; set; }
        public decimal Ema55 { get; set; }
        public decimal Atr14 { get; set; }

        public decimal TrendSlope { get; set; }
        public decimal Volatility { get; set; }

        public MarketRegime Regime { get; set; }

        public decimal LastPrice { get; set; }
    }
}
