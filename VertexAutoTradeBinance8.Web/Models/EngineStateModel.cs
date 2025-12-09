namespace VertexAutoTradeBinance8.Web.Models;

public class EngineStateModel
{
    public string Status { get; set; } = "";
    public string Mode { get; set; } = "";

    public decimal BalanceUsdt { get; set; }

    public string Symbol { get; set; } = "";
    public string Timeframe { get; set; } = "";

    public string MarketRegime { get; set; } = "";
    public string SmartRegime { get; set; } = "";

    public decimal Slope { get; set; }
    public decimal Volatility { get; set; }

    // В JSON decimal → значит decimal
    public decimal Confidence { get; set; }

    public bool LiquidityDanger { get; set; }
    public string LiquidityReason { get; set; } = "";

    public bool SoftEntry { get; set; }
    public bool BlockedByLiquidity { get; set; }

    public DateTime LastUpdate { get; set; }
}
