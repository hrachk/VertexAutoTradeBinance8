namespace VertexAutoTrade.Core.Risk;

/// <summary>
/// R-multiple exit ladder shared by Demo (Web) and Live (Supervisor).
///
/// Why this exists (from client_001 logs 2026-11-17/18): every CORE signal had TP1
/// floored at 1.15R and the step-lock moved SL to +0.5R at 1.0R and +0.85R at 1.4R.
/// Winners closed at ≈ +0.86R, losers at −1R → 36% win rate lost ≈ −3.6R over 11 trades.
/// A trend system only pays if winners are allowed to run several R; this ladder
/// fixes the exit math in one place so Demo and Live behave identically.
///
/// Default ladder (configurable under "RLadder"):
///   targets 2R / 3R / 5R / 7R, size 25% each (the last is the runner's hard TP).
///   SL → break-even + fees after the first target fills (or at BreakevenAtR peak).
///   Lock steps by peak MFE: 3R→+1R, 4R→+2R, 5R→+3R, 6R→+4R.
/// </summary>
public sealed class RLadderOptions
{
    public const string SectionName = "RLadder";

    /// <summary>Master switch. false = legacy exit logic everywhere.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Take-profit levels in R (distance / initial risk). Ascending.</summary>
    public decimal[] TargetsR { get; set; } = { 2.0m, 3.0m, 5.0m, 7.0m };

    /// <summary>Fraction of the INITIAL quantity closed at each target. Sums to 1.</summary>
    public decimal[] Weights { get; set; } = { 0.25m, 0.25m, 0.25m, 0.25m };

    /// <summary>
    /// Entry filter: structural room (distance to the nearest opposing HTF level) must be
    /// at least this many R, otherwise the trade is skipped. 0 disables the filter.
    /// </summary>
    public decimal MinRoomR { get; set; } = 2.0m;

    /// <summary>Peak R at which SL goes to break-even even if no target has filled yet. 0 = only after TP1.</summary>
    public decimal BreakevenAtR { get; set; } = 0m;

    /// <summary>Break-even buffer as fraction of entry (round-trip taker fees + slippage).</summary>
    public decimal FeeBufferPct { get; set; } = 0.0012m;

    /// <summary>Peak-MFE lock steps: when peak ≥ TriggerR the stop is moved to +LockR (never back).</summary>
    public RLockStep[] LockSteps { get; set; } =
    {
        new() { TriggerR = 3.0m, LockR = 1.0m },
        new() { TriggerR = 4.0m, LockR = 2.0m },
        new() { TriggerR = 5.0m, LockR = 3.0m },
        new() { TriggerR = 6.0m, LockR = 4.0m },
    };

    /// <summary>Stop placement for CORE trend setups: "Pullback" (15m pullback extreme) or "Structure" (1H swing, legacy).</summary>
    public string StopMode { get; set; } = "Pullback";

    /// <summary>Normalised, validated copy (weights rescaled to 1, ascending targets, sane steps).</summary>
    public RLadderOptions Normalized()
    {
        var t = (TargetsR ?? Array.Empty<decimal>()).Where(x => x > 0).OrderBy(x => x).ToArray();
        if (t.Length == 0) t = new[] { 2.0m, 3.0m, 5.0m, 7.0m };
        var w = (Weights ?? Array.Empty<decimal>()).Take(t.Length).Select(x => Math.Max(0m, x)).ToList();
        while (w.Count < t.Length) w.Add(w.Count == 0 ? 1m : w[^1]);
        decimal sum = w.Sum();
        if (sum <= 0) { w = Enumerable.Repeat(1m, t.Length).ToList(); sum = t.Length; }
        var steps = (LockSteps ?? Array.Empty<RLockStep>()).Where(s => s.TriggerR > 0 && s.LockR < s.TriggerR)
            .OrderBy(s => s.TriggerR).Select(s => new RLockStep { TriggerR = s.TriggerR, LockR = s.LockR }).ToArray();
        return new RLadderOptions
        {
            Enabled = Enabled,
            TargetsR = t,
            Weights = w.Select(x => x / sum).ToArray(),
            MinRoomR = Math.Max(0m, MinRoomR),
            BreakevenAtR = Math.Max(0m, BreakevenAtR),
            FeeBufferPct = Math.Clamp(FeeBufferPct, 0m, 0.01m),
            LockSteps = steps,
            StopMode = string.IsNullOrWhiteSpace(StopMode) ? "Pullback" : StopMode,
        };
    }

    /// <summary>
    /// Builds options from a flat key reader (e.g. <c>key =&gt; configuration["RLadder:" + key]</c>).
    /// Avoids ConfigurationBinder, which appends config arrays to the default arrays
    /// instead of replacing them. Missing keys keep defaults.
    /// </summary>
    public static RLadderOptions FromKeys(Func<string, string?> get)
    {
        var o = new RLadderOptions();
        static bool D(string? v, out decimal d) => decimal.TryParse(v, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out d);
        decimal[]? Arr(string name)
        {
            var list = new List<decimal>();
            for (int i = 0; i < 32; i++)
            {
                var v = get($"{name}:{i}");
                if (v == null) break;
                if (D(v, out var d)) list.Add(d);
            }
            return list.Count > 0 ? list.ToArray() : null;
        }
        if (bool.TryParse(get("Enabled"), out var en)) o.Enabled = en;
        o.TargetsR = Arr("TargetsR") ?? o.TargetsR;
        o.Weights = Arr("Weights") ?? o.Weights;
        if (D(get("MinRoomR"), out var room)) o.MinRoomR = room;
        if (D(get("BreakevenAtR"), out var be)) o.BreakevenAtR = be;
        if (D(get("FeeBufferPct"), out var fee)) o.FeeBufferPct = fee;
        var sm = get("StopMode");
        if (!string.IsNullOrWhiteSpace(sm)) o.StopMode = sm;
        var steps = new List<RLockStep>();
        for (int i = 0; i < 32; i++)
        {
            var t = get($"LockSteps:{i}:TriggerR");
            if (t == null) break;
            if (D(t, out var tr) && D(get($"LockSteps:{i}:LockR"), out var lr))
                steps.Add(new RLockStep { TriggerR = tr, LockR = lr });
        }
        if (steps.Count > 0) o.LockSteps = steps.ToArray();
        return o.Normalized();
    }

    public bool UsePullbackStop => string.Equals(StopMode, "Pullback", StringComparison.OrdinalIgnoreCase);
}

public sealed class RLockStep
{
    public decimal TriggerR { get; set; }
    public decimal LockR { get; set; }
}

public static class RLadderPolicy
{
    /// <summary>Process-wide ladder (set once at startup from configuration). Static so pure strategy code can read it.</summary>
    public static RLadderOptions Current { get; private set; } = new RLadderOptions().Normalized();

    public static void Configure(RLadderOptions? options)
        => Current = (options ?? new RLadderOptions()).Normalized();

    /// <summary>Price of every ladder target for a trade.</summary>
    public static decimal[] TargetPrices(decimal entry, decimal stop, bool isLong, RLadderOptions? o = null)
    {
        o ??= Current;
        decimal risk = Math.Abs(entry - stop);
        if (entry <= 0 || risk <= 0) return Array.Empty<decimal>();
        return o.TargetsR.Select(r => isLong ? entry + risk * r : entry - risk * r)
            .Where(p => p > 0).ToArray();
    }

    /// <summary>Current R of a price relative to entry and initial risk (signed).</summary>
    public static decimal CurrentR(decimal entry, decimal initialRisk, bool isLong, decimal price)
    {
        if (initialRisk <= 0 || entry <= 0 || price <= 0) return 0m;
        return (isLong ? price - entry : entry - price) / initialRisk;
    }

    /// <summary>
    /// The stop (in R from entry) the position is entitled to, given its peak MFE and how many
    /// targets have filled. Returns null when the initial stop must stay where it is.
    /// Monotonic in peakR, so callers can ratchet without extra state.
    /// </summary>
    public static decimal? LockedStopR(decimal peakR, int targetsFilled, RLadderOptions? o = null)
    {
        o ??= Current;
        decimal? lockR = null;
        bool beByTarget = targetsFilled >= 1;
        bool beByPeak = o.BreakevenAtR > 0 && peakR >= o.BreakevenAtR;
        if (beByTarget || beByPeak) lockR = 0m; // break-even (+fees added in price space)
        foreach (var s in o.LockSteps)
            if (peakR >= s.TriggerR && (lockR is null || s.LockR > lockR)) lockR = s.LockR;
        return lockR;
    }

    /// <summary>Stop price for <see cref="LockedStopR"/>; includes the fee buffer at break-even.</summary>
    public static decimal? LockedStopPrice(
        decimal entry, decimal initialRisk, bool isLong, decimal peakR, int targetsFilled, RLadderOptions? o = null)
    {
        o ??= Current;
        var lr = LockedStopR(peakR, targetsFilled, o);
        if (lr is null || entry <= 0 || initialRisk <= 0) return null;
        decimal fee = entry * o.FeeBufferPct;
        decimal dist = initialRisk * lr.Value;
        // The fee buffer only matters near BE; at +1R and beyond it is already covered.
        if (dist < fee) dist = fee;
        return isLong ? entry + dist : entry - dist;
    }

    /// <summary>True when <paramref name="candidate"/> is a tighter (better) stop than <paramref name="current"/>.</summary>
    public static bool IsImprovement(decimal candidate, decimal current, bool isLong)
    {
        if (candidate <= 0) return false;
        if (current <= 0) return true;
        return isLong ? candidate > current : candidate < current;
    }

    /// <summary>
    /// Room filter: given the nearest opposing level (resistance for long, support for short)
    /// return how many R are available. A missing level means open air (returns MaxValue).
    /// </summary>
    public static decimal RoomR(decimal entry, decimal stop, bool isLong, decimal? opposingLevel)
    {
        decimal risk = Math.Abs(entry - stop);
        if (risk <= 0) return 0m;
        if (opposingLevel is not > 0) return decimal.MaxValue;
        decimal d = isLong ? opposingLevel.Value - entry : entry - opposingLevel.Value;
        return d <= 0 ? 0m : d / risk;
    }

    /// <summary>
    /// Expected R per trade for this ladder given the probability that price reaches each target
    /// before the stop (pReach[i], non-increasing). Path assumption: after reaching target i and
    /// failing to reach i+1, the remainder exits at the locked stop for that peak.
    /// Useful for the UI calculator and for comparing ladders with backtest hit rates.
    /// </summary>
    public static decimal ExpectancyR(decimal[] pReach, RLadderOptions? o = null)
    {
        o ??= Current;
        int n = o.TargetsR.Length;
        if (pReach == null || pReach.Length < n) return 0m;
        decimal e = 0m;
        decimal pPrev = 1m;
        for (int k = 0; k <= n; k++)
        {
            // Probability that the trade's max target reached is exactly k (k = 0 → none).
            decimal pk = k < n ? pPrev - pReach[k] : pReach[n - 1];
            if (k < n) pPrev = pReach[k];
            if (pk <= 0) continue;
            decimal realized = 0m, remaining = 1m;
            for (int i = 0; i < k; i++) { realized += o.Weights[i] * o.TargetsR[i]; remaining -= o.Weights[i]; }
            decimal exitR;
            if (k == 0) exitR = -1m;
            else
            {
                decimal peak = o.TargetsR[k - 1];
                exitR = LockedStopR(peak, k, o) ?? -1m;
            }
            e += pk * (realized + remaining * exitR);
        }
        return e;
    }
}

/// <summary>
/// Progressive ("нарастающий") risk: fixed-fractional of current equity (compounds on wins),
/// scaled down in drawdown from the equity high-water mark. Never scales risk up beyond base:
/// growth comes from compounding, not from betting more after losses or streaks.
/// </summary>
public static class EquityCurveRiskScaler
{
    public static decimal Multiplier(decimal equity, decimal highWaterMark)
    {
        if (equity <= 0 || highWaterMark <= 0) return 1m;
        decimal dd = 1m - equity / highWaterMark;
        if (dd >= 0.15m) return 0.25m;
        if (dd >= 0.10m) return 0.50m;
        if (dd >= 0.06m) return 0.70m;
        if (dd >= 0.03m) return 0.85m;
        return 1m;
    }
}
