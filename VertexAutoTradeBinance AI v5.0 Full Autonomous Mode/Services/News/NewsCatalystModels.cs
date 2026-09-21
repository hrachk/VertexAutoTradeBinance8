namespace VertexAutoTradeBinance8.Services.News;

public enum NewsVector { Neutral = 0, Bullish = 1, Bearish = -1 }

public enum NewsImpactGrade { Low = 0, Medium = 1, High = 2 }

/// <summary>Event Severity Matrix (TZ News Filter Alpha).</summary>
public enum NewsEventCategory
{
    /// <summary>CPI, FOMC, NFP, rates — HARD_BLOCK all symbols.</summary>
    MacroHigh = 0,
    /// <summary>Unlocks, listings, hardforks — scale/block related tokens only.</summary>
    TokenSpecific = 1,
    /// <summary>Exchange maintenance, API instability — microstructure gate.</summary>
    Infrastructure = 2
}

public static class NewsReasonCodes
{
    public const string MacroHighImpact = "REASON_MACRO_HIGH_IMPACT";
    public const string TokenEvent = "REASON_TOKEN_EVENT";
    public const string SpreadExpansion = "REASON_SPREAD_EXPANSION";
    public const string LowDepth = "REASON_LOW_DEPTH";
    public const string HighSlippage = "REASON_HIGH_NEWS_SLIPPAGE_RISK";
    public const string DynamicWindow = "REASON_DYNAMIC_VOL_WINDOW";
    public const string OpposingVector = "REASON_NEWS_OPPOSES_SIDE";
}

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
    public NewsImpactGrade Grade { get; set; } = NewsImpactGrade.Low;
    public NewsEventCategory Category { get; set; } = NewsEventCategory.TokenSpecific;
    /// <summary>Expected event half-life in minutes (used for dynamic T_end).</summary>
    public int EventDurationMinutes { get; set; } = 30;
    public List<string> RelatedSymbols { get; set; } = new();
    public string? OfficialSpeaker { get; set; }
    public string? ReasonCode { get; set; }
}

public sealed class NewsDirective
{
    public DateTime Utc { get; set; } = DateTime.UtcNow;
    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndUtc { get; set; }
    public NewsVector Vector { get; set; }
    public decimal Strength { get; set; }
    public NewsImpactGrade Grade { get; set; }
    public NewsEventCategory Category { get; set; }
    public IReadOnlyList<string> Symbols { get; set; } = Array.Empty<string>();
    public string Reason { get; set; } = "";
    public string ReasonCode { get; set; } = "";
    public int PauseNewEntriesSeconds { get; set; }
    /// <summary>0 = hard block, 0.25–0.5 token scale, 1 = full.</summary>
    public decimal SizeMult { get; set; } = 1m;
}

/// <summary>Per-entry evaluation result (≤2–5 ms when directive cached).</summary>
public sealed class NewsEntryDecision
{
    public decimal SizeMult { get; set; } = 1m;
    public bool WouldBlock { get; set; }
    public string ReasonCode { get; set; } = "";
    public string Reason { get; set; } = "";
    public NewsEventCategory? Category { get; set; }
}
