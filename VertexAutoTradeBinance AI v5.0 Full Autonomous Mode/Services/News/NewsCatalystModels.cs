namespace VertexAutoTradeBinance8.Services.News;

public enum NewsVector { Neutral = 0, Bullish = 1, Bearish = -1 }

public sealed class NewsEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Utc { get; set; } = DateTime.UtcNow;
    public string Source { get; set; } = "";
    public string Headline { get; set; } = "";
    public string? Body { get; set; }
    public decimal Credibility { get; set; }
    public decimal Impact { get; set; }
    public NewsVector Vector { get; set; } = NewsVector.Neutral;
    public List<string> RelatedSymbols { get; set; } = new();
    public string? OfficialSpeaker { get; set; }
}

public sealed class NewsDirective
{
    public DateTime Utc { get; set; } = DateTime.UtcNow;
    public NewsVector Vector { get; set; }
    public decimal Strength { get; set; }
    public IReadOnlyList<string> Symbols { get; set; } = Array.Empty<string>();
    public string Reason { get; set; } = "";
    public int PauseNewEntriesSeconds { get; set; }
}
