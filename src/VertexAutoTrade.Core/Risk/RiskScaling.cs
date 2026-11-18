namespace VertexAutoTrade.Core.Risk;

/// <summary>
/// Fixed-fractional position sizing and compounding ("масштабирование"):
///   Risk $  = risk % × CURRENT balance (recomputed before every trade)
///   Volume  = Risk $ / |Entry − Stop|
///   Result  = R × Risk $
/// After losses the dollar risk shrinks, after wins it grows — the account compounds
/// without ever betting more than the chosen fraction of what is left.
/// </summary>
public static class RiskScaling
{
    /// <summary>Quantity (coins/contracts) that loses exactly <paramref name="riskUsd"/> at the stop.</summary>
    public static decimal QtyForRisk(decimal riskUsd, decimal entry, decimal stop)
    {
        decimal d = Math.Abs(entry - stop);
        return riskUsd <= 0 || d <= 0 ? 0m : riskUsd / d;
    }

    /// <summary>Kelly fraction for a fixed R:R: f* = p − (1 − p) / rr. ≤ 0 means no edge.</summary>
    public static decimal Kelly(decimal winRate, decimal rr)
        => rr <= 0 ? 0m : winRate - (1m - winRate) / rr;

    /// <summary>Expected R per trade for a fixed R:R: p·rr − (1 − p).</summary>
    public static decimal ExpectancyR(decimal winRate, decimal rr) => winRate * rr - (1m - winRate);

    public sealed record Row(int N, bool Win, decimal RiskUsd, decimal ResultUsd, decimal Balance, decimal DrawdownPct);

    /// <summary>Replays a win/loss sequence with risk = riskPct × current balance.</summary>
    public static List<Row> Simulate(decimal start, decimal riskPct, decimal rr, IReadOnlyList<bool> outcomes, decimal feeR = 0m)
    {
        var rows = new List<Row>(outcomes.Count);
        decimal bal = start, peak = start;
        for (int i = 0; i < outcomes.Count; i++)
        {
            decimal risk = Math.Round(bal * riskPct, 2, MidpointRounding.AwayFromZero);
            decimal res = outcomes[i] ? risk * (rr - feeR) : -risk * (1m + feeR);
            bal = Math.Round(bal + res, 2, MidpointRounding.AwayFromZero);
            if (bal > peak) peak = bal;
            rows.Add(new Row(i + 1, outcomes[i], risk, Math.Round(res, 2), bal, peak > 0 ? 1m - bal / peak : 0m));
        }
        return rows;
    }

    /// <summary>Deterministic pseudo-random win/loss sequence with the given win rate (seeded).</summary>
    public static List<bool> Sequence(int n, decimal winRate, int seed)
    {
        var rng = new Random(seed);
        var list = new List<bool>(n);
        for (int i = 0; i < n; i++) list.Add((decimal)rng.NextDouble() < winRate);
        return list;
    }

    /// <summary>
    /// Monte-Carlo over many sequences: median final multiple, 5th percentile, and the share of
    /// runs whose drawdown ever exceeded <paramref name="ddLimit"/>.
    /// </summary>
    public static (decimal median, decimal p5, decimal ruinShare, int worstStreak) MonteCarlo(
        decimal riskPct, decimal rr, decimal winRate, int trades, int runs = 2000, decimal ddLimit = 0.5m, int seed = 42)
    {
        var finals = new List<decimal>(runs);
        int ruined = 0, worst = 0;
        var rng = new Random(seed);
        for (int r = 0; r < runs; r++)
        {
            decimal bal = 1m, peak = 1m;
            bool hit = false;
            int streak = 0;
            for (int i = 0; i < trades; i++)
            {
                bool win = (decimal)rng.NextDouble() < winRate;
                bal += bal * riskPct * (win ? rr : -1m);
                if (win) streak = 0; else worst = Math.Max(worst, ++streak);
                if (bal > peak) peak = bal;
                if (1m - bal / peak >= ddLimit) hit = true;
            }
            if (hit) ruined++;
            finals.Add(bal);
        }
        finals.Sort();
        return (finals[runs / 2], finals[runs / 20], (decimal)ruined / runs, worst);
    }
}
