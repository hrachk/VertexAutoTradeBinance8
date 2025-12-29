using static VertexAutoTradeBinance8.Web.Services.PositionsLiveService;

namespace VertexAutoTradeBinance8.Web.Models;

public sealed class LivePositionModel
{
    public string Symbol { get; set; } = "";
    public string PositionSide { get; set; } = ""; // LONG / SHORT

    public decimal PositionAmt { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal MarkPrice { get; set; }
    public decimal UnrealizedPnl { get; set; }
    public decimal Notional { get; set; }

    public decimal? LiquidationPrice { get; set; }
    public decimal? IsolatedMargin { get; set; }
    public int? Leverage { get; set; }

    public DateTime TimeUtc { get; set; }

    // =====================================
    // 🔥 ADD: UI Change Mask (NON-BREAKING)
    // =====================================
    public PositionChange ChangeMask { get; set; } = PositionChange.None;
}
