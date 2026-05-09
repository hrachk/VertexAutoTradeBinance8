namespace VertexAutoTradeBinance8.Models;

public sealed record UniverseDryRunEntry
(
    DateTime UtcTime,
    decimal BtcChangePct,
    string BtcVolTier,
    int DynamicCap,
    IReadOnlyList<string> Long,
    IReadOnlyList<string> Short,
    IReadOnlyList<string> Pinned
);
