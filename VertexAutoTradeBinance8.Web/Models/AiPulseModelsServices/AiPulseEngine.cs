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
        if (states.Count == 0)
            return;

        var pulses = new List<decimal>();

        foreach (var s in states)
        {
            var prev = _lastState.GetOrAdd(s.Symbol, s);

            var deltaSlope = Math.Abs(s.TrendSlopePercent - prev.TrendSlopePercent) / 100m;
            var deltaVol = Math.Abs(s.VolatilityPercent - prev.VolatilityPercent) / 100m;
            var deltaConf = Math.Abs(s.Confidence - prev.Confidence);

            decimal pulse = deltaSlope * 0.9m + deltaVol * 1.2m + deltaConf * 0.7m;

            if (!s.Reason.StartsWith("PERIODIC", StringComparison.OrdinalIgnoreCase))
                pulse *= 1.4m;

            pulse *= s.Regime switch
            {
                MarketRegime.StrongUpTrend => 1.15m,
                MarketRegime.StrongDownTrend => 1.15m,
                MarketRegime.Range => 0.85m,
                _ => 0.75m
            };

            var ema = _ema.AddOrUpdate(s.Symbol,
                                       pulse,
                                       (_, prevEma) => prevEma + Alpha * (pulse - prevEma));

            var snapshot = new SymbolPulseSnapshot
            {
                Symbol = s.Symbol,
                Time = s.Time,
                Pulse = pulse,
                SmoothedPulse = ema,
                Regime = s.Regime,
                Confidence = s.Confidence
            };

            _symbols.AddOrUpdate(s.Symbol, snapshot, (_, _) => snapshot);

            pulses.Add(ema * s.Confidence);
            _lastState[s.Symbol] = s;
        }

        if (pulses.Count == 0) return;

        var marketPulse = pulses.Sum() / pulses.Count;
        _marketEma = _marketEma + Alpha * (marketPulse - _marketEma);

        Market = new MarketPulseSnapshot
        {
            Time = DateTime.UtcNow,
            Pulse = marketPulse,
            SmoothedPulse = _marketEma,
            Mode = ResolveMode(_marketEma)
        };
    }

    private static PulseMode ResolveMode(decimal value)
    {
        if (value < 0.25m) return PulseMode.Calm;
        if (value < 0.65m) return PulseMode.Alert;
        return PulseMode.Aggressive;
    }
}
