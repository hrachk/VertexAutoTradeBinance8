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
        _enginesRoot = cfg["SharedData:Root"]
            ?? Path.Combine(AppContext.BaseDirectory, "engines");
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

    /// <summary>
    /// After SL on THIS symbol: wider SL + closer TPs next time. NEVER cuts confidence.
    /// </summary>
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

            decimal tp1Dist = 0m;
            if (t.TakeProfits != null && t.TakeProfits.Count > 0)
                tp1Dist = Math.Abs(t.TakeProfits[0] - t.EntryPrice);
            if (tp1Dist > 0)
            {
                decimal prog = move / tp1Dist;
                if (prog < 0m) prog = 0m;
                if (prog > 1m) prog = 1m;
                missScores.Add(1m - prog);
            }
            else missScores.Add(0.7m);
        }

        decimal avgMiss = missScores.Count > 0 ? missScores.Average() : 0m;
        decimal avgTight = tightSlScores.Count > 0 ? tightSlScores.Average() : 1m;
        decimal stopRate = (decimal)stops / valid.Count;

        decimal sizeMult = 1m, slPad = 0m, tpScale = 1m, confMult = 1m, levMult = 1m;
        string note = "neutral";

        if (consecutiveStops >= 1 || stops >= 2 || stopRate >= 0.45m)
        {
            decimal padBase = consecutiveStops >= 3 ? 0.50m
                : consecutiveStops == 2 ? 0.38m
                : consecutiveStops == 1 ? 0.28m
                : 0.22m;
            if (avgMiss >= 0.55m) padBase += 0.10m;
            if (avgTight >= 0.85m && avgTight <= 1.25m) padBase += 0.08m;
            if (padBase > 0.75m) padBase = 0.75m;
            slPad = padBase;

            tpScale = avgMiss >= 0.60m ? 0.85m
                : avgMiss >= 0.40m ? 0.90m
                : consecutiveStops >= 2 ? 0.92m
                : 0.95m;

            sizeMult = consecutiveStops >= 3 ? 0.80m
                : consecutiveStops == 2 ? 0.90m
                : stopRate >= 0.55m ? 0.92m
                : 0.95m;
            levMult = consecutiveStops >= 3 ? 0.90m : 1.0m;

            note = "smart SL/TP after SL history stopsInRow=" + consecutiveStops
                + " missTp=" + avgMiss.ToString("F2") + " (conf untouched)";
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
            Note = note
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
