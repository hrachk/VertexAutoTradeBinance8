using System.Globalization;
using System.Text;
using VertexAutoTrade.Backtest;
using VertexAutoTrade.Core.Risk;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

string Arg(string name, string def)
{
    int i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
}

var symbols = Arg("symbols", "BTCUSDT,ETHUSDT,SOLUSDT,BNBUSDT,XRPUSDT,DOGEUSDT,LINKUSDT,AVAXUSDT,SUIUSDT,HYPEUSDT,NEARUSDT,ZECUSDT")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
int days = int.Parse(Arg("days", "120"));
string execTf = Arg("exec", "5m");
decimal riskPct = decimal.Parse(Arg("risk", "0.0075"));
string outDir = Arg("out", Path.Combine(Environment.CurrentDirectory, "bt-results"));
Directory.CreateDirectory(outDir);

var toUtc = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
var fromUtc = toUtc.AddDays(-days);
var warm = fromUtc.AddDays(-100); // daily EMA21 / 80 D1 bars of warm-up

Console.WriteLine($"Vertex backtest {fromUtc:yyyy-MM-dd} → {toUtc:yyyy-MM-dd HH}:00 UTC | {symbols.Length} symbols | exec={execTf} | risk/trade={riskPct:P2}");
var data = new List<SymbolData>();
foreach (var s in symbols)
{
    try
    {
        Console.Write($"  loading {s} ... ");
        var sd = new SymbolData
        {
            Symbol = s,
            M15 = await KlineStore.LoadAsync(s, "15m", fromUtc.AddDays(-3), toUtc),
            H1 = await KlineStore.LoadAsync(s, "1h", fromUtc.AddDays(-10), toUtc),
            H4 = await KlineStore.LoadAsync(s, "4h", fromUtc.AddDays(-20), toUtc),
            D1 = await KlineStore.LoadAsync(s, "1d", warm, toUtc),
            Exec = await KlineStore.LoadAsync(s, execTf, fromUtc.AddDays(-1), toUtc),
            ExecBar = KlineStore.Duration(execTf),
        };
        Console.WriteLine($"15m={sd.M15.Count} 1h={sd.H1.Count} exec={sd.Exec.Count}");
        if (sd.M15.Count > 200) data.Add(sd);
    }
    catch (Exception ex) { Console.WriteLine("skip: " + ex.Message); }
}

RLadderOptions L(bool en, string stop, decimal room) => new()
{
    Enabled = en, StopMode = stop, MinRoomR = room,
};
var configs = new List<BtConfig>
{
    new() { Name = "LEGACY (1.15R TP1 + step-lock)", Ladder = L(false, "Structure", 0) },
    new() { Name = "LADDER struct-stop room0",       Ladder = L(true, "Structure", 0) },
    new() { Name = "LADDER pullback-stop room0",     Ladder = L(true, "Pullback", 0) },
    new() { Name = "LADDER pullback-stop room2 (default)", Ladder = L(true, "Pullback", 2) },
    new() { Name = "LADDER pullback-stop room3",     Ladder = L(true, "Pullback", 3) },
    new() { Name = "LADDER struct-stop room2",       Ladder = L(true, "Structure", 2) },
};

var summary = new StringBuilder();
summary.AppendLine("| Config | Signals | Trades | Win% | AvgWin R | AvgLoss R | Expectancy R | Profit factor | Sum R | MaxDD R | Equity ×(compounded) |");
summary.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
var mfe = new StringBuilder();
mfe.AppendLine("| Config | ≥1R | ≥1.5R | ≥2R | ≥3R | ≥4R | ≥5R | ≥7R |");
mfe.AppendLine("|---|---|---|---|---|---|---|---|");

foreach (var cfg in configs)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var (trades, reasons, signals) = Simulator.Run(data, cfg, fromUtc);
    int n = trades.Count;
    var wins = trades.Where(t => t.R > 0).ToList();
    var losses = trades.Where(t => t.R <= 0).ToList();
    decimal sumR = trades.Sum(t => t.R);
    decimal gp = wins.Sum(t => t.R), gl = -losses.Sum(t => t.R);
    decimal peak = 0, cum = 0, maxDd = 0, eq = 1m;
    foreach (var t in trades)
    {
        cum += t.R; peak = Math.Max(peak, cum); maxDd = Math.Max(maxDd, peak - cum);
        eq *= 1m + riskPct * t.R;
    }
    string P(decimal x) => n == 0 ? "-" : (trades.Count(t => t.PeakR >= x) * 100m / n).ToString("F0") + "%";
    summary.AppendLine($"| {cfg.Name} | {signals} | {n} | {(n == 0 ? 0 : wins.Count * 100m / n):F1} | {(wins.Count == 0 ? 0 : wins.Average(t => t.R)):F2} | {(losses.Count == 0 ? 0 : losses.Average(t => t.R)):F2} | {(n == 0 ? 0 : sumR / n):F3} | {(gl == 0 ? 0 : gp / gl):F2} | {sumR:F1} | {maxDd:F1} | {eq:F3} |");
    mfe.AppendLine($"| {cfg.Name} | {P(1)} | {P(1.5m)} | {P(2)} | {P(3)} | {P(4)} | {P(5)} | {P(7)} |");

    string safe = new string(cfg.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    using (var w = new StreamWriter(Path.Combine(outDir, $"trades_{safe}.csv")))
    {
        w.WriteLine("symbol,side,open_utc,close_utc,entry,stop,risk_pct,r_net,peak_r,targets_hit,exit");
        foreach (var t in trades)
            w.WriteLine($"{t.Symbol},{t.Side},{t.OpenUtc:O},{t.CloseUtc:O},{t.Entry},{t.Stop},{t.RiskPct:F5},{t.R:F3},{t.PeakR:F3},{t.TargetsHit},{t.Exit}");
    }
    Console.WriteLine($"\n== {cfg.Name}  ({sw.Elapsed.TotalSeconds:F0}s)  signals={signals} trades={n} sumR={sumR:F1}");
    Console.WriteLine("   top filters: " + string.Join(", ", reasons.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key}={kv.Value}")));
}

var report = $"# Vertex backtest {fromUtc:yyyy-MM-dd} → {toUtc:yyyy-MM-dd}\n\nSymbols: {string.Join(", ", symbols)}  \nExec TF: {execTf}, fees 0.05%/side, slippage 2 bps, risk/trade {riskPct:P2} (compounded)\n\n## Results\n{summary}\n## MFE distribution (share of trades whose best excursion reached X·R)\n{mfe}\n" +
             "Read: a ladder needs P(≥2R) high enough to pay for the −1R losers. Compare configs on the SAME data; do not trust < 100 trades.\n";
File.WriteAllText(Path.Combine(outDir, "summary.md"), report);
Console.WriteLine("\n" + report);
Console.WriteLine($"CSV + summary.md written to {outDir}");
