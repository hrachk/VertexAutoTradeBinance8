using VertexAutoTrade.Core.Pipeline;

namespace VertexAutoTrade.NewsMacro;

public sealed class MacroEventWindow
{
    public string Name { get; init; } = "";
    public DateTime Utc { get; init; }
    public int MinutesBefore { get; init; } = 15;
    public int MinutesAfter { get; init; } = 15;
}

/// <summary>
/// Hard pause new entries inside macro windows (CPI/FOMC/NFP…).
/// Does not close positions by default — only blocks new risk.
/// </summary>
public sealed class CalendarMacroGuard
{
    private readonly List<MacroEventWindow> _events;

    public CalendarMacroGuard(IEnumerable<MacroEventWindow>? events = null)
    {
        _events = (events ?? DefaultEvents()).OrderBy(e => e.Utc).ToList();
    }

    public GateDecision Evaluate(DateTime utcNow)
    {
        utcNow = utcNow.ToUniversalTime();
        foreach (var e in _events)
        {
            var start = e.Utc.AddMinutes(-e.MinutesBefore);
            var end = e.Utc.AddMinutes(e.MinutesAfter);
            if (utcNow >= start && utcNow <= end)
                return GateDecision.Reject(GateLayer.NewsMacro, "MACRO_BLACKOUT",
                    $"MACRO:{e.Name} {start:HH:mm}-{end:HH:mm}Z", end);
        }
        return GateDecision.Ok(GateLayer.NewsMacro);
    }

    /// <summary>Optional size cut when near (but not inside) window.</summary>
    public decimal SizeMultNearWindow(DateTime utcNow, int softMinutes = 45)
    {
        utcNow = utcNow.ToUniversalTime();
        foreach (var e in _events)
        {
            var softStart = e.Utc.AddMinutes(-(e.MinutesBefore + softMinutes));
            var hardStart = e.Utc.AddMinutes(-e.MinutesBefore);
            if (utcNow >= softStart && utcNow < hardStart)
                return 0.5m;
        }
        return 1m;
    }

    public static IEnumerable<MacroEventWindow> DefaultEvents()
    {
        // Placeholder schedule — replace via config in production
        yield return new MacroEventWindow { Name = "FOMC", Utc = new DateTime(2026, 11, 5, 18, 0, 0, DateTimeKind.Utc) };
        yield return new MacroEventWindow { Name = "CPI", Utc = new DateTime(2026, 10, 15, 12, 30, 0, DateTimeKind.Utc) };
        yield return new MacroEventWindow { Name = "NFP", Utc = new DateTime(2026, 10, 3, 12, 30, 0, DateTimeKind.Utc) };
    }
}
