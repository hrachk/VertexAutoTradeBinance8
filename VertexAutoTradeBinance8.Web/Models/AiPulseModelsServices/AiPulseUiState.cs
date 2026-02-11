public sealed class AiPulseUiState
{
    private readonly object _lock = new();

    public decimal MarketPulse { get; private set; }
    public decimal MarketPulseSmooth { get; private set; }
    public PulseMode Mode { get; private set; }
    public DateTime Time { get; private set; }

    public void Update(MarketPulseSnapshot snap)
    {
        lock (_lock)
        {
            MarketPulse = snap.Pulse;
            MarketPulseSmooth = snap.SmoothedPulse;
            Mode = snap.Mode;
            Time = snap.Time;
        }
    }
}
