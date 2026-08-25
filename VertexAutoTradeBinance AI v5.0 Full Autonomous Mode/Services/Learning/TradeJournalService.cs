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
    private string ClientDir(string clientId) => Path.Combine(_enginesRoot, $"client_{clientId}");
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

    private static SymbolAdjustments Compute(string symbol, List<TradeJournalEntry> trades)
    {
        int stops = trades.Count(t =>
            t.CloseReason.Contains("SL", StringComparison.OrdinalIgnoreCase) || t.RealizedPnl < 0);
        int wins = trades.Count(t => t.RealizedPnl > 0 &&
            !t.CloseReason.Contains("SL", StringComparison.OrdinalIgnoreCase));
        int consecutiveStops = 0;
        foreach (var t in trades.OrderByDescending(x => x.ClosedAtUtc))
        {
            if (t.RealizedPnl < 0 || t.CloseReason.Contains("SL", StringComparison.OrdinalIgnoreCase))
                consecutiveStops++;
            else break;
        }

        decimal sizeMult = 1m, slPad = 0m, tpScale = 1m, confMult = 1m, levMult = 1m;
        string note = "neutral";

        if (consecutiveStops >= 1 || stops >= 2)
        {
            sizeMult = consecutiveStops >= 3 ? 0.55m : consecutiveStops == 2 ? 0.70m : 0.85m;
            slPad = consecutiveStops >= 2 ? 0.35m : 0.20m;
            tpScale = 0.92m;
            confMult = 0.92m;
            levMult = consecutiveStops >= 2 ? 0.85m : 0.95m;
            note = $"caution stopsInRow={consecutiveStops} stops={stops}/{trades.Count}";
        }
        else if (wins >= 2 && stops == 0)
        {
            sizeMult = 0.95m;
            note = "win streak â€” hold/slight ease, no aggression";
        }

        return new SymbolAdjustments
        {
            Symbol = symbol, SizeMult = sizeMult, SlPadAtr = slPad, TpScale = tpScale,
            ConfMult = confMult, LevMult = levMult, RecentTrades = trades.Count,
            RecentStops = stops, RecentWins = wins, Note = note
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
