using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services.News;

/// <summary>
/// Event-driven risk overlay. Default ShadowMode=true: never blocks live entries,
/// only logs [NEWS-SHADOW]. When ShadowMode=false: High→skip, Medium→size 0.5x.
/// </summary>
public sealed class NewsCatalystService : INewsCatalystService
{
    private readonly ILogger<NewsCatalystService> _log;
    private readonly IConfiguration _cfg;
    private readonly object _gate = new();
    private readonly List<NewsEvent> _recent = new();
    private NewsDirective? _active;
    private DateTime _activeUntilUtc;
    private const decimal MinCredibility = 0.70m;
    private const decimal MinImpact = 0.50m;

    public NewsCatalystService(ILogger<NewsCatalystService> log, IConfiguration cfg)
    {
        _log = log;
        _cfg = cfg;
    }

    public bool ShadowMode => _cfg.GetValue("News:ShadowMode", true);

    public void Ingest(NewsEvent ev)
    {
        if (ev == null) return;
        lock (_gate)
        {
            _recent.Add(ev);
            if (_recent.Count > 200) _recent.RemoveRange(0, _recent.Count - 200);
            if (ev.Credibility < MinCredibility || ev.Impact < MinImpact) return;
            // Macro calendar may be Neutral direction but High grade — still risk-off
            var grade = ev.Grade;
            if (ev.Vector == NewsVector.Neutral && grade < NewsImpactGrade.High) return;
            if (grade == NewsImpactGrade.Low)
            {
                if (ev.Impact >= 0.75m && ev.Credibility >= 0.80m) grade = NewsImpactGrade.High;
                else if (ev.Impact >= 0.55m) grade = NewsImpactGrade.Medium;
            }

            // Aggregate recent same-window noise: net score over last 15 min
            var cutoff = DateTime.UtcNow.AddMinutes(-15);
            decimal net = 0m;
            int n = 0;
            foreach (var e in _recent.Where(x => x.Utc >= cutoff && x.Vector != NewsVector.Neutral))
            {
                net += (decimal)(int)e.Vector * e.Credibility * e.Impact;
                n++;
            }
            if (n >= 2 && Math.Abs(net) < 0.35m)
            {
                _log.LogInformation("[NEWS] conflicting noise net={net:F2} n={n} — no directive", net, n);
                return;
            }

            int ttlSec = grade switch
            {
                NewsImpactGrade.High => (int)Math.Clamp((double)(ev.Impact * 600m), 300, 1200),
                NewsImpactGrade.Medium => (int)Math.Clamp((double)(ev.Impact * 400m), 180, 600),
                _ => 120
            };

            decimal sizeMult = grade switch
            {
                NewsImpactGrade.High => 0m,      // skip when live
                NewsImpactGrade.Medium => 0.50m,
                _ => 0.85m
            };

            _active = new NewsDirective
            {
                Vector = ev.Vector,
                Strength = ev.Credibility * ev.Impact,
                Grade = grade,
                Symbols = ev.RelatedSymbols?.ToList() ?? new List<string>(),
                Reason = $"news:{ev.Source}:{ev.Headline}",
                PauseNewEntriesSeconds = ttlSec,
                SizeMult = sizeMult
            };
            _activeUntilUtc = DateTime.UtcNow.AddSeconds(ttlSec);

            if (ShadowMode)
            {
                _log.LogWarning(
                    "[NEWS-SHADOW] Would scale/pause grade={g} size×{sm:F2} vec={v} strength={s:F2} ttl={sec}s symbols={sym} | {h}",
                    grade, sizeMult, ev.Vector, _active.Strength, ttlSec,
                    _active.Symbols.Count == 0 ? "*" : string.Join(",", _active.Symbols),
                    ev.Headline);
            }
            else
            {
                _log.LogWarning(
                    "[NEWS] directive grade={g} size×{sm:F2} vec={v} strength={s:F2} ttl={sec}s | {h}",
                    grade, sizeMult, ev.Vector, _active.Strength, ttlSec, ev.Headline);
            }
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

    private bool AppliesTo(NewsDirective d, string symbol)
    {
        if (d.Symbols == null || d.Symbols.Count == 0) return true;
        return d.Symbols.Any(s => string.Equals(s, symbol, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsEntryPaused(string symbol)
    {
        // Shadow: never hard-block execution
        if (ShadowMode) return false;

        var d = TryGetActiveDirective();
        if (d == null) return false;
        if (!AppliesTo(d, symbol)) return false;
        // Hard pause only for High grade (size 0)
        return d.SizeMult <= 0m || d.Grade == NewsImpactGrade.High;
    }

    public decimal GetEntrySizeMult(string symbol, string? side = null)
    {
        var d = TryGetActiveDirective();
        if (d == null || !AppliesTo(d, symbol)) return 1m;

        // Optional: reduce size more if news opposes trade side
        decimal sm = d.SizeMult <= 0m ? 0m : d.SizeMult;
        if (!string.IsNullOrEmpty(side) && d.Grade >= NewsImpactGrade.Medium)
        {
            bool isLong = side.Contains("BUY", StringComparison.OrdinalIgnoreCase)
                       || side.Contains("LONG", StringComparison.OrdinalIgnoreCase);
            bool newsBull = d.Vector == NewsVector.Bullish;
            bool newsBear = d.Vector == NewsVector.Bearish;
            if ((isLong && newsBear) || (!isLong && newsBull))
                sm = Math.Min(sm <= 0 ? 0.5m : sm, 0.50m);
        }

        if (ShadowMode)
        {
            _log.LogInformation(
                "[NEWS-SHADOW] Would size×{sm:F2} for {sym} side={side} ({reason})",
                sm <= 0 ? 0m : sm, symbol, side ?? "-", d.Reason);
            return 1m; // do not affect live size in shadow
        }

        return sm <= 0m ? 0m : Math.Clamp(sm, 0.25m, 1m);
    }
}
