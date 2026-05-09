using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Models;
using static VertexAutoTradeBinance8.Services.AiSelfLearningService;

public sealed class AiPulseEngine : IAiPulseEngine
{
    private readonly ConcurrentDictionary<string, MarketState> _lastState = new();
    private readonly ConcurrentDictionary<string, decimal> _ema = new();
    private readonly ConcurrentDictionary<string, SymbolPulseSnapshot> _symbols = new();

    private decimal _marketEma;

    public IReadOnlyDictionary<string, SymbolPulseSnapshot> Symbols => _symbols;

    public MarketPulseSnapshot Market { get; private set; } = new();

    private const decimal Alpha = 0.18m;

    public void Update(IReadOnlyList<MarketState> states)
    {
        foreach (var s in states)
            UpdateSingle(s);
    }

    public void UpdateSingle(MarketState s)
    {
        if (!_lastState.TryGetValue(s.Symbol, out var prev))
        {
            _lastState[s.Symbol] = s;
            return;
        }

        var deltaSlope = Math.Abs(s.TrendSlopePercent - prev.TrendSlopePercent) / 100m;
        var deltaVol = Math.Abs(s.VolatilityPercent - prev.VolatilityPercent) / 100m;
        var deltaConf = Math.Abs(s.Confidence - prev.Confidence);

        decimal pulse =
              deltaSlope * 0.9m
            + deltaVol * 1.2m
            + deltaConf * 0.7m;

        if (!s.Reason.StartsWith("PERIODIC", StringComparison.OrdinalIgnoreCase))
            pulse *= 1.4m;

        pulse *= s.Regime switch
        {
            MarketRegime.StrongUpTrend => 1.15m,
            MarketRegime.StrongDownTrend => 1.15m,
            MarketRegime.Range => 0.85m,
            _ => 0.75m
        };

        var ema = _ema.AddOrUpdate(
            s.Symbol,
            pulse,
            (_, prevEma) => prevEma + Alpha * (pulse - prevEma));

        _symbols[s.Symbol] = new SymbolPulseSnapshot
        {
            Symbol = s.Symbol,
            Time = s.Time,
            Pulse = pulse,
            SmoothedPulse = ema,
            Confidence = s.Confidence,
            Regime = s.Regime
        };

        UpdateMarket();

        _lastState[s.Symbol] = s;
    }

    private void UpdateMarket()
    {
        if (_symbols.Count == 0)
            return;

        decimal sum = 0;
        decimal weight = 0;

        foreach (var s in _symbols.Values)
        {
            sum += s.SmoothedPulse * s.Confidence;
            weight += s.Confidence;
        }

        if (weight == 0)
            return;

        var marketPulse = sum / weight;

        _marketEma = _marketEma + Alpha * (marketPulse - _marketEma);

        Market = new MarketPulseSnapshot
        {
            Time = DateTime.UtcNow,
            Pulse = marketPulse,
            SmoothedPulse = _marketEma,
            Mode = ResolveMode(_marketEma)
        };

        Console.WriteLine($"[MARKET PULSE] raw={marketPulse:F4} ema={_marketEma:F4}");
    }

    private static PulseMode ResolveMode(decimal value)
    {
        if (value < 0.25m) return PulseMode.Calm;
        if (value < 0.65m) return PulseMode.Alert;
        return PulseMode.Aggressive;
    }
}
