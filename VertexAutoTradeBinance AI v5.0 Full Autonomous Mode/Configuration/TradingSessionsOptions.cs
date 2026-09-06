using System.Collections.Generic;

namespace VertexAutoTradeBinance8.Configuration;

/// <summary>
/// Trading time windows. All StartUtc/EndUtc are UTC clock times (HH:mm).
/// If StartUtc > EndUtc the window crosses midnight UTC (e.g. 23:00–07:45).
/// Convert local (Yerevan UTC+4) → UTC when editing.
/// </summary>
public sealed class TradingSessionsOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Shift every window start earlier by N minutes (0 = exact times).</summary>
    public int EarlyStartMinutes { get; set; } = 0;
    public bool BlockWeekends { get; set; } = false;
    public List<SessionWindowOptions> Windows { get; set; } = new();
}

public sealed class SessionWindowOptions
{
    public string Name { get; set; } = "";
    /// <summary>UTC start HH:mm or HH:mm:ss</summary>
    public string StartUtc { get; set; } = "00:00";
    /// <summary>UTC end HH:mm or HH:mm:ss (exclusive-ish: active while now < end, or overnight logic)</summary>
    public string EndUtc { get; set; } = "23:59";
}
