using Binance.Net.Enums;

namespace VertexAutoTradeBinance8.Models
{
    public sealed class LivePositionState
    {
        public string Symbol { get; set; } = "";
        public PositionSide Side { get; set; } = PositionSide.Both;

        public decimal Qty { get; set; }                 // signed or abs? (мы храним signed как Binance)
        public decimal EntryPrice { get; set; }
        public decimal MarkPrice { get; set; }
        public decimal UnrealizedPnl { get; set; }
        public decimal Notional { get; set; }

        public decimal? LiquidationPrice { get; set; }
        public decimal? IsolatedMargin { get; set; }
        public int? Leverage { get; set; }

        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        public static string Key(string symbol, PositionSide side)
            => $"{symbol}|{side}".ToUpperInvariant();
    }
}
