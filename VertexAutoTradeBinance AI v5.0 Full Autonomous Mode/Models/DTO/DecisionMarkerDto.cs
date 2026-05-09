namespace VertexAutoTradeBinance8.Models.DTO;

public enum DecisionMarkerType
{
    Evaluated,          // свеча была рассмотрена
    BlockedLate,        // BLOCKED_LATE
    LowVolume,          // LOW_VOLUME
    ConfidenceBelow,    // CONF_BELOW
    EntryAllowed,       // ENTER_ALLOWED
    EntryRejected       // общий reject (fallback)
}

public sealed class DecisionMarkerDto
{
    public string Symbol { get; init; } = string.Empty;
    public string Timeframe { get; init; } = string.Empty;

    /// <summary>
    /// Время свечи (OpenTime или CloseTime — фиксируем одно)
    /// </summary>
    public DateTime CandleTimeUtc { get; init; }

    public DecisionMarkerType Type { get; init; }

    /// <summary>
    /// Короткий код: BLOCKED_LATE, LOW_VOLUME и т.д.
    /// </summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>
    /// Человекочитаемое пояснение (для UI tooltip)
    /// </summary>
    public string? Details { get; init; }

    /// <summary>
    /// Доп. данные (confidence, vol, atr, profile и т.д.)
    /// </summary>
    public Dictionary<string, decimal>? Metrics { get; init; }

    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
}
