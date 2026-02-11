public sealed class MarketPulseSnapshot
{
    public DateTime Time { get; init; }

    public decimal Pulse { get; init; }
    public decimal SmoothedPulse { get; init; }

    public PulseMode Mode { get; init; }
}
public enum PulseMode
{
    Calm,
    Alert,
    Aggressive
}
