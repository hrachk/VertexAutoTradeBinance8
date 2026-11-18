using Binance.Net.Objects.Models.Futures;
using VertexAutoTrade.Core.Risk;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Strategy;
using VertexAutoTradeBinance8.Strategy.StrategyCore;

namespace VertexAutoTrade.Backtest;

public sealed class SymbolData
{
    public string Symbol = "";
    public List<BinanceFuturesUsdtKline> M15 = new(), H1 = new(), H4 = new(), D1 = new(), Exec = new();
    public TimeSpan ExecBar;
}

public sealed record BtTrade(
    string Symbol, string Side, DateTime OpenUtc, DateTime CloseUtc,
    decimal Entry, decimal Stop, decimal RiskPct, decimal R, decimal PeakR, int TargetsHit, string Exit);

public sealed class BtConfig
{
    public string Name = "";
    public RLadderOptions Ladder = new();
    public decimal TakerFee = 0.0005m;     // per side, on notional
    public decimal SlippageBps = 2m;       // adverse, per market fill
    public TimeSpan Cooldown = TimeSpan.FromHours(4); // StrategyCoreEngine cooldown
}

/// <summary>
/// Bar-by-bar replay. Signals are evaluated on each CLOSED 15m bar with only the HTF bars that
/// had closed at that moment (no look-ahead). Entry fills at the next 15m open with adverse
/// slippage. Exits are walked on the execution timeframe (5m default); when a bar touches both
/// the stop and a target the stop is assumed first (pessimistic).
/// </summary>
public static class Simulator
{
    private static readonly string[] Majors = { "BTC", "ETH", "BNB", "SOL", "XRP" };

    public static (List<BtTrade> trades, Dictionary<string, int> reasons, int signals) Run(
        IReadOnlyList<SymbolData> data, BtConfig cfg, DateTime fromUtc)
    {
        RLadderPolicy.Configure(cfg.Ladder);
        var ladder = RLadderPolicy.Current;
        var trades = new List<BtTrade>();
        var reasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int signals = 0;

        foreach (var sd in data)
        {
            DateTime cooldownUntil = DateTime.MinValue;
            DateTime busyUntil = DateTime.MinValue; // one position per symbol
            var m15 = sd.M15;
            var m15Dur = TimeSpan.FromMinutes(15);
            for (int i = 150; i < m15.Count - 1; i++)
            {
                var bar = m15[i];
                DateTime now = bar.OpenTime + m15Dur; // the moment this bar closed
                if (now < fromUtc || now < cooldownUntil || now < busyUntil) continue;

                var slice = m15.GetRange(i - 149, 150);
                // StrategyCoreEngine ATR band on 15m
                decimal atr15 = Atr(slice, 14);
                decimal mid = (bar.HighPrice + bar.LowPrice) / 2m;
                if (atr15 <= 0 || mid <= 0) continue;
                decimal atrPct = atr15 / mid;
                if (atrPct < 0.0008m || atrPct > 0.060m) { Count(reasons, "atr_band"); continue; }

                var h1 = Closed(sd.H1, now, TimeSpan.FromHours(1), 90);
                var h4 = Closed(sd.H4, now, TimeSpan.FromHours(4), 60);
                var d1 = Closed(sd.D1, now, TimeSpan.FromDays(1), 80);
                if (h1.Count < 55 || d1.Count < 30) { Count(reasons, "htf_thin"); continue; }

                var built = InstitutionalTrendSetup.TryBuild(sd.Symbol, h1, slice, h4, null, d1);
                if (!built.Ok || built.Signal == null)
                {
                    var r = built.Reason ?? "no_setup";
                    int c = r.IndexOf(':'); if (c > 0) r = r[..c];
                    Count(reasons, r);
                    continue;
                }
                var sig = built.Signal;
                if (!EntrySanityGate.Allow(sig, slice, h1, out var why))
                {
                    Count(reasons, "sanity:" + why.Split(' ')[0]);
                    continue;
                }
                signals++;
                cooldownUntil = now + cfg.Cooldown;

                var t = Manage(sd, sig, m15[i + 1], cfg, ladder);
                if (t != null)
                {
                    trades.Add(t);
                    busyUntil = t.CloseUtc;
                }
            }
        }
        return (trades.OrderBy(t => t.CloseUtc).ToList(), reasons, signals);
    }

    private static BtTrade? Manage(SymbolData sd, TradeSignal sig, BinanceFuturesUsdtKline next, BtConfig cfg, RLadderOptions ladder)
    {
        bool isLong = sig.Side == SignalSide.Buy;
        decimal slip = cfg.SlippageBps / 10000m;
        decimal fill = isLong ? next.OpenPrice * (1 + slip) : next.OpenPrice * (1 - slip);
        decimal stop = sig.StopLoss;
        if (isLong ? fill <= stop : fill >= stop) return null;
        decimal risk = Math.Abs(fill - stop);
        if (risk <= 0) return null;

        // Targets + weights exactly as Live/Demo would place them.
        decimal[] targets;
        decimal[] weights;
        bool ladderMode = ladder.Enabled;
        if (ladderMode)
        {
            targets = RLadderPolicy.TargetPrices(fill, stop, isLong, ladder); // AllowPriceUpdate rebuild
            weights = ladder.Weights;
        }
        else
        {
            targets = sig.TakeProfits.Take(3).ToArray();
            weights = targets.Length == 3 ? new[] { 0.33m, 0.33m, 0.34m } : Enumerable.Repeat(1m / Math.Max(1, targets.Length), targets.Length).ToArray();
        }

        decimal remaining = 1m, realized = 0m, fees = fill * cfg.TakerFee; // entry fee on full size
        decimal peakR = 0m;
        int hit = 0;
        decimal curStop = stop;
        string exit = "OPEN_END";
        DateTime closeT = next.OpenTime;

        int start = LowerBound(sd.Exec, next.OpenTime);
        for (int j = start; j < sd.Exec.Count && remaining > 0.0001m; j++)
        {
            var b = sd.Exec[j];
            closeT = b.OpenTime + sd.ExecBar;
            decimal hi = b.HighPrice, lo = b.LowPrice;

            // 1) stop first (pessimistic)
            bool stopHit = isLong ? lo <= curStop : hi >= curStop;
            if (stopHit)
            {
                decimal px = isLong ? Math.Min(curStop, b.OpenPrice) : Math.Max(curStop, b.OpenPrice); // gap through stop
                px = isLong ? px * (1 - slip) : px * (1 + slip);
                realized += remaining * (isLong ? px - fill : fill - px);
                fees += remaining * px * cfg.TakerFee;
                remaining = 0;
                exit = curStop == stop ? "SL" : (Math.Abs(curStop - fill) <= fill * 0.002m ? "BE" : "LOCK");
                break;
            }

            // 2) peak of this bar (before targets, so a bar that runs to 7R records 7R)
            decimal barPeak = RLadderPolicy.CurrentR(fill, risk, isLong, isLong ? hi : lo);
            if (barPeak > peakR) peakR = barPeak;

            // 3) targets in order
            while (hit < targets.Length)
            {
                decimal tp = targets[hit];
                bool tpHit = isLong ? hi >= tp : lo <= tp;
                if (!tpHit) break;
                bool last = hit == targets.Length - 1;
                decimal part = last ? remaining : Math.Min(remaining, weights[hit]);
                realized += part * (isLong ? tp - fill : fill - tp);
                fees += part * tp * cfg.TakerFee;
                remaining -= part;
                hit++;
                if (last || remaining <= 0.0001m) { exit = $"TP{hit}"; remaining = 0; break; }
            }
            if (remaining <= 0) break;

            // 4) stop ratchet (effective from the next bar)
            if (ladderMode)
            {
                var lp = RLadderPolicy.LockedStopPrice(fill, risk, isLong, peakR, hit, ladder);
                if (lp is decimal l && RLadderPolicy.IsImprovement(l, curStop, isLong)) curStop = l;
            }
            else
            {
                // Legacy Demo: BE (+0.10%) after first TP, then step-lock by current R.
                if (hit >= 1)
                {
                    decimal be = isLong ? fill * 1.0010m : fill * 0.9990m;
                    if (RLadderPolicy.IsImprovement(be, curStop, isLong)) curStop = be;
                    decimal tr = barPeak >= 2.40m ? 1.70m : barPeak >= 2.00m ? 1.40m : barPeak >= 1.70m ? 1.00m
                               : barPeak >= 1.40m ? 0.85m : barPeak >= 1.00m ? 0.50m : 0m;
                    if (tr > 0)
                    {
                        decimal l = isLong ? fill + risk * tr : fill - risk * tr;
                        if (RLadderPolicy.IsImprovement(l, curStop, isLong)) curStop = l;
                    }
                }
            }
        }
        if (remaining > 0.0001m && sd.Exec.Count > 0)
        {
            decimal px = sd.Exec[^1].ClosePrice;
            realized += remaining * (isLong ? px - fill : fill - px);
            fees += remaining * px * cfg.TakerFee;
        }
        decimal rNet = (realized - fees) / risk;
        return new BtTrade(sd.Symbol, isLong ? "LONG" : "SHORT", next.OpenTime, closeT,
            fill, stop, risk / fill, rNet, peakR, hit, exit);
    }

    // ---- helpers ----
    private static void Count(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out var v) ? v + 1 : 1;

    /// <summary>Last <paramref name="take"/> bars that had fully closed at <paramref name="now"/>.</summary>
    private static List<BinanceFuturesUsdtKline> Closed(List<BinanceFuturesUsdtKline> src, DateTime now, TimeSpan dur, int take)
    {
        int hi = LowerBound(src, now - dur + TimeSpan.FromTicks(1)); // first bar NOT closed
        int from = Math.Max(0, hi - take);
        return src.GetRange(from, hi - from);
    }

    /// <summary>First index with OpenTime &gt;= t.</summary>
    private static int LowerBound(List<BinanceFuturesUsdtKline> k, DateTime t)
    {
        int lo = 0, hi = k.Count;
        while (lo < hi)
        {
            int m = (lo + hi) / 2;
            if (k[m].OpenTime < t) lo = m + 1; else hi = m;
        }
        return lo;
    }

    private static decimal Atr(List<BinanceFuturesUsdtKline> k, int p)
    {
        if (k.Count < p + 2) return 0;
        decimal s = 0;
        for (int i = k.Count - p; i < k.Count; i++)
        {
            decimal tr = Math.Max(k[i].HighPrice - k[i].LowPrice,
                Math.Max(Math.Abs(k[i].HighPrice - k[i - 1].ClosePrice), Math.Abs(k[i].LowPrice - k[i - 1].ClosePrice)));
            s += tr;
        }
        return s / p;
    }

    public static bool IsMajor(string s) => Majors.Any(m => s.StartsWith(m, StringComparison.OrdinalIgnoreCase));
}
