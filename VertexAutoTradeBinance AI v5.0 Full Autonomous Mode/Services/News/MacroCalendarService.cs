using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services.News;

/// <summary>
/// Static / config-driven macro windows (FOMC, CPI, NFP).
/// Pause new entries MinutesBefore..MinutesAfter event time (UTC).
/// Provides ~bulk of event protection without headline latency.
/// </summary>
public sealed class MacroCalendarService : BackgroundService
{
    private readonly ILogger<MacroCalendarService> _log;
    private readonly IConfiguration _cfg;
    private readonly INewsCatalystService? _news;
    private List<MacroEvent> _events = new();

    public MacroCalendarService(
        ILogger<MacroCalendarService> log,
        IConfiguration cfg,
        INewsCatalystService? news = null)
    {
        _log = log;
        _cfg = cfg;
        _news = news;
        ReloadEvents();
    }

    public sealed class MacroEvent
    {
        public string Name { get; set; } = "";
        public DateTime Utc { get; set; }
        public int MinutesBefore { get; set; } = 15;
        public int MinutesAfter { get; set; } = 15;
    }

    private void ReloadEvents()
    {
        var list = new List<MacroEvent>();
        // Built-in 2026 sample anchors (extend via News:MacroEvents in appsettings)
        // User should keep this list updated; config overrides/adds.
        void Add(string name, string iso, int before = 15, int after = 15)
        {
            if (DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
                list.Add(new MacroEvent { Name = name, Utc = dt, MinutesBefore = before, MinutesAfter = after });
        }

        // Placeholder high-impact US windows — update quarterly
        Add("FOMC", "2026-09-16T18:00:00Z", 30, 20);
        Add("FOMC", "2026-11-05T18:00:00Z", 30, 20);
        Add("FOMC", "2026-12-16T19:00:00Z", 30, 20);
        Add("CPI", "2026-09-11T12:30:00Z", 15, 15);
        Add("CPI", "2026-10-15T12:30:00Z", 15, 15);
        Add("CPI", "2026-11-13T13:30:00Z", 15, 15);
        Add("NFP", "2026-10-02T12:30:00Z", 15, 15);
        Add("NFP", "2026-11-06T13:30:00Z", 15, 15);
        Add("NFP", "2026-12-04T13:30:00Z", 15, 15);

        var section = _cfg.GetSection("News:MacroEvents");
        foreach (var child in section.GetChildren())
        {
            var name = child["Name"] ?? "MACRO";
            var utc = child["Utc"];
            int before = child.GetValue("MinutesBefore", 15);
            int after = child.GetValue("MinutesAfter", 15);
            if (!string.IsNullOrEmpty(utc) &&
                DateTime.TryParse(utc, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
            {
                list.Add(new MacroEvent { Name = name, Utc = dt.ToUniversalTime(), MinutesBefore = before, MinutesAfter = after });
            }
        }

        _events = list.OrderBy(e => e.Utc).ToList();
        _log.LogInformation("[MACRO-CAL] loaded {n} events (next={next})",
            _events.Count,
            _events.FirstOrDefault(e => e.Utc > DateTime.UtcNow.AddHours(-2))?.Name ?? "none");
    }

    /// <summary>True if now is inside any macro blackout window.</summary>
    public bool IsInBlackout(out string reason)
    {
        reason = "";
        if (!_cfg.GetValue("News:MacroCalendarEnabled", true))
            return false;

        var now = DateTime.UtcNow;
        foreach (var e in _events)
        {
            var start = e.Utc.AddMinutes(-e.MinutesBefore);
            var end = e.Utc.AddMinutes(e.MinutesAfter);
            if (now >= start && now <= end)
            {
                reason = $"MACRO:{e.Name} window {start:HH:mm}-{end:HH:mm}Z";
                return true;
            }
        }
        return false;
    }

    private string? _lastEmittedReason;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (IsInBlackout(out var reason))
                {
                    if (!string.Equals(_lastEmittedReason, reason, StringComparison.Ordinal))
                    {
                        _lastEmittedReason = reason;
                        _log.LogWarning("[MACRO-CAL] BLACKOUT active — {reason}", reason);
                        _news?.Ingest(new NewsEvent
                        {
                            Source = "MacroCalendar",
                            Headline = reason,
                            Credibility = 1.0m,
                            Impact = 0.95m,
                            Vector = NewsVector.Neutral,
                            Grade = NewsImpactGrade.High,
                            Category = NewsEventCategory.MacroHigh,
                            EventDurationMinutes = 45,
                            ReasonCode = NewsReasonCodes.MacroHighImpact,
                            RelatedSymbols = new List<string>(),
                            Utc = DateTime.UtcNow
                        });
                    }
                }
                else _lastEmittedReason = null;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[MACRO-CAL] tick failed");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }
}
