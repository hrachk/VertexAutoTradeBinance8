namespace VertexAutoTradeBinance8.Services.News;

public interface INewsCatalystService
{
    NewsDirective? TryGetActiveDirective();
    bool IsEntryPaused(string symbol);
    decimal GetEntrySizeMult(string symbol, string? side = null);
    /// <summary>Full decision with reason codes (shadow + live).</summary>
    NewsEntryDecision EvaluateEntry(string symbol, string? side = null);
    void Ingest(NewsEvent ev);
    bool ShadowMode { get; }
    /// <summary>Optional ATR ratio for dynamic windows (1h ATR / 24h avg ATR).</summary>
    void SetAtrRatio(decimal atr1hOverAvg24h);
}
