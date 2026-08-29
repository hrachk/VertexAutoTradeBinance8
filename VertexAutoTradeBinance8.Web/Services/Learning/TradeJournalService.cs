using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services.Learning;

public sealed class TradeJournalService
{
    private readonly ILogger<TradeJournalService> _log;
    private readonly string _enginesRoot;
    private readonly int _windowDays;
    private static readonly JsonSerializerOptions JsonOpt = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> Locks = new();

    public TradeJournalService(IConfiguration cfg, ILogger<TradeJournalService> log)
    {
        _log = log;
        // Same root as DemoAccountService (per-client ledgers live here)
        var enginesRoot = cfg["SharedData:EnginesRoot"];
        if (string.IsNullOrWhiteSpace(enginesRoot))
            enginesRoot = cfg["SharedData:Root"];
        if (string.IsNullOrWhiteSpace(enginesRoot))
            enginesRoot = Path.Combine(AppContext.BaseDirectory, "engines");
        _enginesRoot = enginesRoot;
        _windowDays = Math.Clamp(cfg.GetValue("TradeMemory:WindowDays", 30), 7, 90);
    }

    private static object LockFor(string id) => Locks.GetOrAdd(id, _ => new object());

    private string ClientDir(string clientId)
    {
        var id = (clientId ?? "").Trim();
        if (id.StartsWith("client_", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(_enginesRoot, id);
        return Path.Combine(_enginesRoot, "client_" + id);
    }

    private string JournalPath(string clientId) => Path.Combine(ClientDir(clientId), "trade-journal.json");
    private string MemoryPath(string clientId) => Path.Combine(ClientDir(clientId), "symbol-memory.json");

    public void Append(TradeJournalEntry e)
    {
        if (string.IsNullOrWhiteSpace(e.ClientId) || string.IsNullOrWhiteSpace(e.Symbol)) return;
        try
        {
            Directory.CreateDirectory(ClientDir(e.ClientId));
            var path = JournalPath(e.ClientId);
            lock (LockFor(e.ClientId))
            {
                var file = LoadJournal(path);
                file.Entries.Add(e);
                if (file.Entries.Count > 2000)
                    file.Entries = file.Entries.OrderByDescending(x => x.ClosedAtUtc).Take(2000).ToList();
                File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOpt));
            }
            RebuildMemory(e.ClientId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[JOURNAL] append failed {c} {s}", e.ClientId, e.Symbol);
        }
    }

    public SymbolAdjustments GetAdjustments(string clientId, string symbol)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(symbol))
            return new SymbolAdjustments { Symbol = symbol ?? "" };
        try
        {
            var mem = LoadMemory(MemoryPath(clientId));
            if (mem.BySymbol.TryGetValue(symbol, out var a)) return a;
        }
        catch { }
        return new SymbolAdjustments { Symbol = symbol };
    }

    /// <summary>True if this CORE setup should be soft-skipped (repeated failure, low MFE).</summary>
    public bool ShouldSoftSkipSetup(string clientId, string symbol, string setup)
    {
        if (string.IsNullOrWhiteSpace(setup)) return false;
        var adj = GetAdjustments(clientId, symbol);
        if (adj.SoftSkipSetups == null || adj.SoftSkipSetups.Count == 0) return false;
        return adj.SoftSkipSetups.Any(s =>
            string.Equals(s, setup, StringComparison.OrdinalIgnoreCase));
    }


    /// <summary>Recent journal rows for UI (Demo and/or Live). Newest first.</summary>

    /// <summary>Load from one or more client folder name variants (001 / client_001).</summary>
    public List<TradeJournalEntry> LoadRecentMany(IEnumerable<string> clientIds, string? sourceFilter = null, int max = 150)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = new List<TradeJournalEntry>();
        foreach (var id in clientIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var e in LoadRecent(id, sourceFilter, max))
            {
                var key = e.Id;
                if (string.IsNullOrEmpty(key))
                    key = $"{e.Symbol}|{e.Side}|{e.ClosedAtUtc:o}|{e.EntryPrice}|{e.ExitPrice}|{e.Qty}";
                if (!seen.Add(key)) continue;
                all.Add(e);
            }
        }
        return all.OrderByDescending(e => e.ClosedAtUtc).Take(Math.Clamp(max, 1, 500)).ToList();
    }

    public List<TradeJournalEntry> LoadRecent(string clientId, string? sourceFilter = null, int max = 150)
    {
        try
        {
            var file = LoadJournal(JournalPath(clientId));
            IEnumerable<TradeJournalEntry> q = file.Entries;
            if (!string.IsNullOrWhiteSpace(sourceFilter))
                q = q.Where(e => string.Equals(e.Source, sourceFilter, StringComparison.OrdinalIgnoreCase));
            return q.OrderByDescending(e => e.ClosedAtUtc).Take(Math.Clamp(max, 1, 500)).ToList();
        }
        catch { return new List<TradeJournalEntry>(); }
    }

    public void RebuildMemory(string clientId)
    {
        try
        {
            var journal = LoadJournal(JournalPath(clientId));
            var cutoff = DateTime.UtcNow.AddDays(-_windowDays);
            var recent = journal.Entries.Where(x => x.ClosedAtUtc >= cutoff).ToList();
            var mem = new SymbolMemoryFile { UpdatedUtc = DateTime.UtcNow };
            foreach (var g in recent.GroupBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase))
                mem.BySymbol[g.Key] = Compute(g.Key, g.ToList());
            Directory.CreateDirectory(ClientDir(clientId));
            lock (LockFor(clientId))
                File.WriteAllText(MemoryPath(clientId), JsonSerializer.Serialize(mem, JsonOpt));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[JOURNAL] rebuild failed {c}", clientId);
        }
    }

    private static SymbolAdjustments Compute(string symbol, List<TradeJournalEntry> trades)
    {
        var valid = new List<TradeJournalEntry>();
        foreach (var t in trades)
        {
            if (t.EntryPrice <= 0 || t.ExitPrice <= 0) continue;
            if (t.Qty <= 0 && Math.Abs(t.RealizedPnl) < 0.01m) continue;
            if (t.Qty <= 0 && (t.ClosedAtUtc - t.OpenedAtUtc).TotalSeconds < 2) continue;
            valid.Add(t);
        }

        if (valid.Count == 0)
            return new SymbolAdjustments { Symbol = symbol, Note = "neutral (no valid trades)" };

        int stops = 0, wins = 0;
        foreach (var t in valid)
        {
            bool isSl = t.CloseReason.IndexOf("SL", StringComparison.OrdinalIgnoreCase) >= 0 || t.RealizedPnl < 0;
            if (isSl) stops++;
            else if (t.RealizedPnl > 0) wins++;
        }

        int consecutiveStops = 0;
        foreach (var t in valid.OrderByDescending(x => x.ClosedAtUtc))
        {
            bool isSl = t.CloseReason.IndexOf("SL", StringComparison.OrdinalIgnoreCase) >= 0 || t.RealizedPnl < 0;
            if (isSl) consecutiveStops++;
            else break;
        }

        var missScores = new List<decimal>();
        var tightSlScores = new List<decimal>();
        var mfeRatios = new List<decimal>(); // MFE / risk — high means almost reached TP before dying

        foreach (var t in valid)
        {
            bool isSl = t.CloseReason.IndexOf("SL", StringComparison.OrdinalIgnoreCase) >= 0 || t.RealizedPnl < 0;
            if (!isSl) continue;

            decimal risk = 0m;
            if (t.StopLoss.HasValue && t.StopLoss.Value > 0)
                risk = Math.Abs(t.EntryPrice - t.StopLoss.Value);
            if (risk <= 0) risk = Math.Abs(t.EntryPrice - t.ExitPrice);
            if (risk <= 0) continue;

            decimal move = Math.Abs(t.EntryPrice - t.ExitPrice);
            tightSlScores.Add(Math.Min(2m, move / risk));

            if (t.Mfe > 0 && risk > 0)
                mfeRatios.Add(t.Mfe / risk);

            decimal tp1Dist = 0m;
            if (t.TakeProfits != null && t.TakeProfits.Count > 0)
                tp1Dist = Math.Abs(t.TakeProfits[0] - t.EntryPrice);
            if (tp1Dist > 0)
            {
                decimal prog = t.Mfe > 0 ? t.Mfe / tp1Dist : move / tp1Dist;
                if (prog < 0m) prog = 0m;
                if (prog > 1m) prog = 1m;
                missScores.Add(1m - prog);
            }
            else missScores.Add(0.7m);
        }

        decimal avgMiss = missScores.Count > 0 ? missScores.Average() : 0m;
        decimal avgTight = tightSlScores.Count > 0 ? tightSlScores.Average() : 1m;
        decimal avgMfeR = mfeRatios.Count > 0 ? mfeRatios.Average() : 0m;
        decimal stopRate = (decimal)stops / valid.Count;

        decimal sizeMult = 1m, slPad = 0m, tpScale = 1m, confMult = 1m, levMult = 1m;
        bool preferStruct = false;
        string note = "neutral";
        var softSkip = new List<string>();

        // Soft-skip only setups that repeatedly died with low MFE (never approached TP)
        foreach (var sg in valid.GroupBy(x => string.IsNullOrWhiteSpace(x.Setup) ? "_" : x.Setup, StringComparer.OrdinalIgnoreCase))
        {
            if (sg.Key == "_" || sg.Count() < 3) continue;
            int sStops = sg.Count(t => t.CloseReason.IndexOf("SL", StringComparison.OrdinalIgnoreCase) >= 0 || t.RealizedPnl < 0);
            if (sStops < 3) continue;
            decimal sRate = (decimal)sStops / sg.Count();
            var lowMfe = sg.Where(t => t.Mfe > 0 || t.CloseReason.IndexOf("SL", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            decimal avgSetupMfeR = 0m;
            int n = 0;
            foreach (var t in sg)
            {
                decimal risk = t.StopLoss.HasValue && t.StopLoss.Value > 0
                    ? Math.Abs(t.EntryPrice - t.StopLoss.Value)
                    : Math.Abs(t.EntryPrice - t.ExitPrice);
                if (risk <= 0) continue;
                if (t.Mfe > 0) { avgSetupMfeR += t.Mfe / risk; n++; }
            }
            if (n > 0) avgSetupMfeR /= n;
            // Failed setup: high stop rate AND never got far in favor (MFE < 0.5R average)
            if (sRate >= 0.70m && (n == 0 || avgSetupMfeR < 0.50m))
                softSkip.Add(sg.Key);
        }

        if (consecutiveStops >= 1 || stops >= 2 || stopRate >= 0.45m)
        {
            decimal padBase = consecutiveStops >= 3 ? 0.50m
                : consecutiveStops == 2 ? 0.38m
                : consecutiveStops == 1 ? 0.28m
                : 0.22m;
            if (avgMiss >= 0.55m) padBase += 0.10m;
            if (avgTight >= 0.85m && avgTight <= 1.25m) padBase += 0.08m;
            // Died with almost no favorable move → stop was too tight / wrong side of structure
            if (avgMfeR < 0.35m && consecutiveStops >= 1) { padBase += 0.12m; preferStruct = true; }
            if (padBase > 0.75m) padBase = 0.75m;
            slPad = padBase;
            preferStruct = preferStruct || consecutiveStops >= 2;

            tpScale = avgMiss >= 0.60m ? 0.85m
                : avgMiss >= 0.40m ? 0.90m
                : consecutiveStops >= 2 ? 0.92m
                : 0.95m;
            // If MFE was high (almost TP) then reverse to SL → don't pull TP as hard; widen SL more
            if (avgMfeR >= 0.80m)
            {
                tpScale = Math.Max(tpScale, 0.95m);
                slPad = Math.Min(0.75m, slPad + 0.10m);
                preferStruct = true;
            }

            sizeMult = consecutiveStops >= 3 ? 0.80m
                : consecutiveStops == 2 ? 0.90m
                : stopRate >= 0.55m ? 0.92m
                : 0.95m;
            levMult = consecutiveStops >= 3 ? 0.90m : 1.0m;

            note = "smart SL/TP mfeR=" + avgMfeR.ToString("F2")
                + " missTp=" + avgMiss.ToString("F2")
                + " softSkip=" + softSkip.Count
                + " (conf untouched)";
        }
        else if (wins >= 2 && stops == 0)
        {
            sizeMult = 0.95m;
            note = "win streak hold/slight ease (conf untouched)";
        }

        return new SymbolAdjustments
        {
            Symbol = symbol,
            SizeMult = sizeMult,
            SlPadAtr = slPad,
            TpScale = tpScale,
            ConfMult = confMult,
            LevMult = levMult,
            RecentTrades = valid.Count,
            RecentStops = stops,
            RecentWins = wins,
            Note = note,
            PreferStructureSl = preferStruct,
            SoftSkipSetups = softSkip
        };
    }

    private static TradeJournalFile LoadJournal(string path)
    {
        if (!File.Exists(path)) return new TradeJournalFile();
        try { return JsonSerializer.Deserialize<TradeJournalFile>(File.ReadAllText(path)) ?? new TradeJournalFile(); }
        catch { return new TradeJournalFile(); }
    }

    private static SymbolMemoryFile LoadMemory(string path)
    {
        if (!File.Exists(path)) return new SymbolMemoryFile();
        try { return JsonSerializer.Deserialize<SymbolMemoryFile>(File.ReadAllText(path)) ?? new SymbolMemoryFile(); }
        catch { return new SymbolMemoryFile(); }
    }
}
