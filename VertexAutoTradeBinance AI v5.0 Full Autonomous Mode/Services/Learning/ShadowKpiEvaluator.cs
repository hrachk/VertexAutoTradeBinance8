using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Services.Infra;

namespace VertexAutoTradeBinance8.Services.Learning;

public sealed class PortfolioKpi
{
    public int Trades { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal WinRate { get; set; }
    public decimal MaxDd { get; set; }
    public decimal TotalPnl { get; set; }
}

public sealed class ShadowKpiResult
{
    public PortfolioKpi Actual { get; set; } = new();
    public PortfolioKpi Filtered { get; set; } = new();
    public decimal DeltaPf => Filtered.ProfitFactor - Actual.ProfitFactor;
    public decimal DeltaDd => Filtered.MaxDd - Actual.MaxDd; // more negative Actual MaxDd is worse; filtered maxDd smaller magnitude is better
    public bool RecommendEnableGate => Actual.Trades >= 50 && DeltaPf >= 0.15m && DeltaDd >= 0m;
}

/// <summary>
/// Compares actual journal equity vs hypothetical without trades tagged NEWS/MACRO in reason or time windows.
/// Shadow windows can also be fed via RegisterShadowBlock(start,end).
/// </summary>
public sealed class ShadowKpiEvaluator
{
    private readonly SqliteJournalStore? _sqlite;
    private readonly ILogger<ShadowKpiEvaluator> _log;
    private readonly List<(DateTime start, DateTime end, string reason)> _blocks = new();
    private readonly object _gate = new();

    public ShadowKpiEvaluator(ILogger<ShadowKpiEvaluator> log, SqliteJournalStore? sqlite = null)
    {
        _log = log;
        _sqlite = sqlite;
    }

    public void RegisterShadowBlock(DateTime startUtc, DateTime endUtc, string reason)
    {
        lock (_gate)
        {
            _blocks.Add((startUtc, endUtc, reason));
            if (_blocks.Count > 500) _blocks.RemoveRange(0, _blocks.Count - 400);
        }
    }

    public ShadowKpiResult Evaluate(IReadOnlyList<TradeJournalEntry>? trades = null)
    {
        var list = trades?.ToList() ?? _sqlite?.GetRecent(500).ToList() ?? new List<TradeJournalEntry>();
        var actual = Compute(list);
        List<(DateTime start, DateTime end, string reason)> blocks;
        lock (_gate) blocks = _blocks.ToList();

        var filtered = list.Where(t =>
        {
            // drop trades that opened inside a registered shadow block
            foreach (var b in blocks)
            {
                if (t.OpenedAtUtc >= b.start && t.OpenedAtUtc <= b.end)
                    return false;
            }
            var reason = (t.CloseReason ?? "").ToUpperInvariant();
            if (reason.Contains("NEWS") || reason.Contains("MACRO")) return false;
            return true;
        }).ToList();

        var filtKpi = Compute(filtered);
        var res = new ShadowKpiResult { Actual = actual, Filtered = filtKpi };
        _log.LogInformation(
            "[SHADOW-KPI] n={n}/{nf} PF {pfa:F2}->{pff:F2} (Δ{dp:F2}) DD {dda:F2}->{ddf:F2} recommendGate={r}",
            actual.Trades, filtKpi.Trades, actual.ProfitFactor, filtKpi.ProfitFactor, res.DeltaPf,
            actual.MaxDd, filtKpi.MaxDd, res.RecommendEnableGate);
        return res;
    }

    private static PortfolioKpi Compute(List<TradeJournalEntry> trades)
    {
        var kpi = new PortfolioKpi { Trades = trades.Count };
        if (trades.Count == 0) return kpi;
        decimal grossWin = 0, grossLoss = 0;
        int wins = 0;
        decimal equity = 0, peak = 0, maxDd = 0;
        foreach (var t in trades.OrderBy(x => x.ClosedAtUtc))
        {
            var p = t.RealizedPnl;
            if (p > 0) { grossWin += p; wins++; }
            else grossLoss += Math.Abs(p);
            equity += p;
            if (equity > peak) peak = equity;
            var dd = peak - equity;
            if (dd > maxDd) maxDd = dd;
        }
        kpi.TotalPnl = grossWin - grossLoss;
        kpi.WinRate = trades.Count > 0 ? (decimal)wins / trades.Count : 0;
        kpi.ProfitFactor = grossLoss > 0 ? grossWin / grossLoss : (grossWin > 0 ? 99m : 0m);
        kpi.MaxDd = maxDd;
        return kpi;
    }
}
