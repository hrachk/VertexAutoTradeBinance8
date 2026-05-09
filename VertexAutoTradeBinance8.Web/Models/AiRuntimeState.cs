using VertexAutoTradeBinance8.Services;

public sealed class AiRuntimeState
{
    public AiLearningSnapshot Snapshot { get; init; } = default!;
    public IReadOnlyList<AiSelfLearningService.MarketState> MarketStates { get; init; } = [];
    public IReadOnlyList<AiSelfLearningService.TradeHistoryEntry> Trades { get; init; } = [];

    public DateTime LoadedAtUtc { get; init; }
}
