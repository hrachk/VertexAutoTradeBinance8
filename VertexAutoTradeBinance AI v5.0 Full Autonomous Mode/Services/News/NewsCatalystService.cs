using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services.News;

public sealed class NewsCatalystService : INewsCatalystService
{
    private readonly ILogger<NewsCatalystService> _log;
    private readonly object _gate = new();
    private readonly List<NewsEvent> _recent = new();
    private NewsDirective? _active;
    private DateTime _activeUntilUtc;
    private const decimal MinCredibility = 0.75m;
    private const decimal MinImpact = 0.55m;

    public NewsCatalystService(ILogger<NewsCatalystService> log) => _log = log;

    public void Ingest(NewsEvent ev)
    {
        if (ev == null) return;
        lock (_gate)
        {
            _recent.Add(ev);
            if (_recent.Count > 200) _recent.RemoveRange(0, _recent.Count - 200);
            if (ev.Credibility < MinCredibility || ev.Impact < MinImpact) return;
            if (ev.Vector == NewsVector.Neutral) return;
            int pauseSec = (int)Math.Clamp((double)(ev.Impact * 900m), 120, 1800);
            _active = new NewsDirective
            {
                Vector = ev.Vector,
                Strength = ev.Credibility * ev.Impact,
                Symbols = ev.RelatedSymbols?.ToList() ?? new List<string>(),
                Reason = $"news:{ev.Source}:{ev.Headline}",
                PauseNewEntriesSeconds = pauseSec
            };
            _activeUntilUtc = DateTime.UtcNow.AddSeconds(pauseSec);
            _log.LogWarning("[NEWS] directive {vec} strength={s:F2} pause={sec}s | {h}",
                ev.Vector, _active.Strength, pauseSec, ev.Headline);
        }
    }

    public NewsDirective? TryGetActiveDirective()
    {
        lock (_gate)
        {
            if (_active == null || DateTime.UtcNow > _activeUntilUtc) { _active = null; return null; }
            return _active;
        }
    }

    public bool IsEntryPaused(string symbol)
    {
        var d = TryGetActiveDirective();
        if (d == null) return false;
        if (d.Symbols == null || d.Symbols.Count == 0) return true;
        return d.Symbols.Any(s => string.Equals(s, symbol, StringComparison.OrdinalIgnoreCase));
    }
}
