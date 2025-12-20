using VertexAutoTradeBinance8.Models;

public sealed class DecisionTraceAggregate
{
    public string Symbol { get; set; } = "";

    // Regime → GateStats
    public Dictionary<MarketRegime, DecisionGateRegimeStats> ByRegime
        = new();
}
