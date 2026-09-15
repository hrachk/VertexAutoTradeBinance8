namespace VertexAutoTradeBinance8.Services.News;

public interface INewsCatalystService
{
    NewsDirective? TryGetActiveDirective();
    /// <summary>Hard block only when ShadowMode=false and policy says skip. Macro calendar uses separate path.</summary>
    bool IsEntryPaused(string symbol);
    /// <summary>1.0 = full size; 0.5 = half; used even in shadow for logging intended scale.</summary>
    decimal GetEntrySizeMult(string symbol, string? side = null);
    void Ingest(NewsEvent ev);
    bool ShadowMode { get; }
}
