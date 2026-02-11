public sealed class AiPulseUiState
{
    private readonly object _lock = new();
    private MarketPulseSnapshot _market = new();

    public MarketPulseSnapshot Get()
    {
        lock (_lock)
            return _market;
    }

    public void Update(MarketPulseSnapshot snap)
    {
        lock (_lock)
            _market = snap;
    }

    // Для Razor страницы
    public PulseMode Mode
    {
        get
        {
            lock (_lock)
                return _market.Mode;
        }
    }

    public decimal MarketPulse
    {
        get
        {
            lock (_lock)
                return _market.Pulse;
        }
    }

    public decimal MarketPulseSmooth
    {
        get
        {
            lock (_lock)
                return _market.SmoothedPulse;
        }
    }
}
