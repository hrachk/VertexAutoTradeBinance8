using static VertexAutoTradeBinance8.Services.AiSelfLearningService;

public interface IAiPulseEngine
{
    void Update(IReadOnlyList<MarketState> states);
    void UpdateSingle(MarketState s);

    IReadOnlyDictionary<string, SymbolPulseSnapshot> Symbols { get; }
    MarketPulseSnapshot Market { get; }
}
