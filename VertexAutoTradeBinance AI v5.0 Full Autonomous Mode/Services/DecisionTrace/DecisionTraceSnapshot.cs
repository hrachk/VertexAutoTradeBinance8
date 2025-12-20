 

public sealed record DecisionTraceSnapshot
{
    public string Symbol { get; init; } = "";
    public string Timeframe { get; init; } = "";
    public bool Allow { get; init; }

    public string? FailedGate { get; init; }
    public string? Reason { get; init; }

    public decimal Confidence { get; init; }
    public DateTime Time { get; init; }
}
