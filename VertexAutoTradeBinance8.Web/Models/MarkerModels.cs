namespace VertexAutoTradeBinance8.Web.Models;

public enum UiMarkerType
{
    Evaluated,
    BlockedLate,
    LowVolume,
    ConfidenceBelow,
    EntryAllowed,
    EntryRejected
}

public sealed record UiDecisionMarker(
    long CandleOpenTimeMs,      // якорим к свече (unix ms)
    UiMarkerType Type,
    string Code,
    string? Details
);

public sealed record MarkerSeries(
    string Symbol,
    string Timeframe,
    IReadOnlyList<UiDecisionMarker> Markers
);
