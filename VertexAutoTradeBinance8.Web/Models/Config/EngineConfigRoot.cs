using VertexAutoTradeBinance8.Web.Models.Config;

public class EngineConfigRoot
{
    public HedgeKillConfig HedgeKill { get; set; } = new();
    public TradingConfig Trading { get; set; } = new();
    public SymbolSelectionConfig SymbolSelection { get; set; } = new();
}
