using static VertexAutoTradeBinance8.Web.Services.PositionsLiveService;

namespace VertexAutoTradeBinance8.Web.Models;

public class PositionVm
{
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = ""; // LONG / SHORT

    public int Leverage { get; set; }

    public decimal SizeUsdt { get; set; }
    public decimal Margin { get; set; }

    public decimal Entry { get; set; }
    public decimal Mark { get; set; }
    public decimal LiqPrice { get; set; }

    public decimal Pnl { get; set; }
    public decimal Roi { get; set; }
    public decimal MarginRatio { get; set; }

    // AI
    public int AiScore { get; set; }
    public string Regime { get; set; } = "";
    public string AiHint { get; set; } = "";

    // Expand
    public decimal StopLoss { get; set; }
    public decimal TakeProfit { get; set; }

    // 🔑 Ключ
    public string Key => $"{Symbol}:{Side}";

    // 🔥 NEW
    public PositionChange ChangeMask { get; set; }
    public DateTime LastUpdate { get; set; }
}
