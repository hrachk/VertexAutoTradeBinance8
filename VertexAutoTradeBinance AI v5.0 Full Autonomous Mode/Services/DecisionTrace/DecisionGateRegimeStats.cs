using VertexAutoTradeBinance8.Models;

public sealed class DecisionGateRegimeStats
{
    public MarketRegime Regime { get; set; }

    // GateName → Stats
    public Dictionary<string, DecisionGateStats> Gates { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
}
