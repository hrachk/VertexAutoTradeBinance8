using VertexAutoTradeBinance8.Models;

public sealed class SymbolPulseSnapshot
{
    public string Symbol { get; init; } = "";
    public DateTime Time { get; init; }

    public decimal Pulse { get; init; }
    public decimal SmoothedPulse { get; init; }

    public MarketRegime Regime { get; init; }
    public decimal Confidence { get; init; }
}
