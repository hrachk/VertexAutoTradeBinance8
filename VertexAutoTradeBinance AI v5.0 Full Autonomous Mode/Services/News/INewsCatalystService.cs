namespace VertexAutoTradeBinance8.Services.News;

public interface INewsCatalystService
{
    NewsDirective? TryGetActiveDirective();
    bool IsEntryPaused(string symbol);
    void Ingest(NewsEvent ev);
}
