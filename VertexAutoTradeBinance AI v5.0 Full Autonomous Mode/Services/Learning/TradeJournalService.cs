using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Services.Infra;
using VertexAutoTradeBinance8.Services.Risk;
using VertexAutoTradeBinance8.Services.Notify;

namespace VertexAutoTradeBinance8.Services.Learning;

public sealed class TradeJournalService
{
    private readonly ILogger<TradeJournalService> _log;
    private readonly IConfiguration _cfg;
    private readonly string _enginesRoot;
    private readonly int _windowDays;
    private readonly SqliteJournalStore? _sqlite;
    private readonly DailyDrawdownGuard? _dailyDd;
    private readonly TelegramNotificationService? _tg;
    private static readonly JsonSerializerOptions JsonOpt = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> Locks = new();

    public TradeJournalService(IConfiguration cfg, ILogger<TradeJournalService> log, SqliteJournalStore? sqlite = null,
        DailyDrawdownGuard? dailyDd = null, TelegramNotificationService? tg = null)
    {
        _log = log;
        _cfg = cfg;
        _sqlite = sqlite;
        _dailyDd = dailyDd;
        _tg = tg;
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
    private bool SqliteOnly => _cfg.GetValue("Journal:SqliteOnly", false);

    public void Append(TradeJournalEntry e)
    {
        if (string.IsNullOrWhiteSpace(e.ClientId) || string.IsNullOrWhiteSpace(e.Symbol)) return;
        // Sanitize R: BE-trail partials used to store ±20 and poison memory
        if (e.RealizedR > 5m) e.RealizedR = 5m;
        if (e.RealizedR < -5m) e.RealizedR = -5m;
        try
        {
            Directory.CreateDirectory(ClientDir(e.ClientId));
            var path = JournalPath(e.ClientId);
            // SQLite is primary; JSON dual-write unless Journal:SqliteOnly=true
            if (_sqlite == null)
            {
                _log.LogError("[SQLITE-TRACE] SqliteJournalStore is NULL — trade will not be written to DB (DI missing?)");
            }
            else
            {
                try
                {
                    _sqlite.InsertTrade(e);
                }
                catch (Exception sx)
                {
                    _log.LogError(sx, "[SQLITE-TRACE] InsertTrade failed for {s} — falling back JSON if allowed", e.Symbol);
                    if (SqliteOnly) throw;
                }
            }
            if (!SqliteOnly)
            {
                lock (LockFor(e.ClientId))
                {
                    var file = LoadJournal(path);
                    file.Entries.Add(e);
                    if (file.Entries.Count > 2000)
                        file.Entries = file.Entries.OrderByDescending(x => x.ClosedAtUtc).Take(2000).ToList();
                    File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOpt));
                    try {
                        var featPath = Path.Combine(ClientDir(e.ClientId), "trade-features.jsonl");
                        var line = JsonSerializer.Serialize(new { e.Symbol, e.Side, e.Source, e.RealizedPnl, e.RealizedR, e.InitialRiskPrice, e.CloseReason, e.ClosedAtUtc, e.SignalConf });
                        File.AppendAllText(featPath, line + Environment.NewLine);
                    } catch { }
                }
            }
            else
            {
                // features still useful for offline skip model
                try {
                    var featPath = Path.Combine(ClientDir(e.ClientId), "trade-features.jsonl");
                    var line = JsonSerializer.Serialize(new { e.Symbol, e.Side, e.Source, e.RealizedPnl, e.RealizedR, e.InitialRiskPrice, e.CloseReason, e.ClosedAtUtc, e.SignalConf });
                    File.AppendAllText(featPath, line + Environment.NewLine);
                } catch { }
            }
            RebuildMemory(e.ClientId);
            try { RebuildFromFeatures(e.ClientId); } catch { }
            try
            {
                bool isSl = (e.CloseReason ?? "").IndexOf("SL", StringComparison.OrdinalIgnoreCase) >= 0;
                _dailyDd?.OnTradeClosed(e.RealizedPnl, isSl);
                _tg?.Exit(e.Symbol, e.RealizedR, e.RealizedPnl, e.CloseReason ?? "");
            }
            catch { }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[JOURNAL] append failed {c} {s}", e.ClientId, e.Symbol);
        }
    }

    /// <summary>Recent closed trades from trade-journal.json (newest first).</summary>
    public IReadOnlyList<TradeJournalEntry> GetRecentEntries(string clientId, string? sourceFilter = null, int take = 500)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return Array.Empty<TradeJournalEntry>();
        take = Math.Clamp(take, 1, 2000);
        try
        {
            if (_sqlite != null && (SqliteOnly || !File.Exists(JournalPath(clientId))))
            {
                var list = _sqlite.GetRecent(take).ToList();
                if (!string.IsNullOrWhiteSpace(sourceFilter))
                {
                    var sf = sourceFilter.Trim();
                    list = list.Where(e => string.Equals(e.Source, sf, StringComparison.OrdinalIgnoreCase)).ToList();
                }
                return list;
            }
            var path = JournalPath(clientId);
            lock (LockFor(clientId))
            {
                var file = LoadJournal(path);
                IEnumerable<TradeJournalEntry> q = file.Entries ?? new List<TradeJournalEntry>();
                if (!string.IsNullOrWhiteSpace(sourceFilter))
                {
                    var sf = sourceFilter.Trim();
                    q = q.Where(e => string.Equals(e.Source, sf, StringComparison.OrdinalIgnoreCase));
                }
                return q.OrderByDescending(e => e.ClosedAtUtc).Take(take).ToList();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[JOURNAL] GetRecentEntries failed {c}", clientId);
            try { return _sqlite?.GetRecent(take) ?? Array.Empty<TradeJournalEntry>(); }
            catch { return Array.Empty<TradeJournalEntry>(); }
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


    /// <summary>
    /// Offline feature-store pass: re-read trade-features.jsonl + journal and rebuild memory.
    /// Call after batch of closes or on a timer so skip/size stays fresh.
    /// </summary>
    public void RebuildFromFeatures(string clientId)
    {
        try
        {
            RebuildMemory(clientId);
            var featPath = Path.Combine(ClientDir(clientId), "trade-features.jsonl");
            if (!File.Exists(featPath)) return;
            var bySym = new Dictionary<string, List<decimal>>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(featPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("Symbol", out var sEl)) continue;
                    var sym = sEl.GetString() ?? "";
                    if (string.IsNullOrEmpty(sym)) continue;
                    decimal r = 0m;
                    if (root.TryGetProperty("RealizedR", out var rEl) && rEl.TryGetDecimal(out var rd))
                        r = rd;
                    if (Math.Abs(r) > 3m || r == 0m) continue;
                    if (!bySym.TryGetValue(sym, out var list))
                        bySym[sym] = list = new List<decimal>();
                    list.Add(r);
                }
                catch { }
            }
            if (bySym.Count == 0) return;
            var mem = LoadMemory(MemoryPath(clientId));
            foreach (var kv in bySym)
            {
                if (kv.Value.Count < 3) continue;
                var avg = kv.Value.Average();
                if (!mem.BySymbol.TryGetValue(kv.Key, out var adj))
                    adj = new SymbolAdjustments { Symbol = kv.Key };
                if (avg <= -0.55m)
                {
                    adj.SoftSkip = true;
                    adj.SizeMult = Math.Min(adj.SizeMult <= 0 ? 1m : adj.SizeMult, 0.15m);
                    adj.Note = (adj.Note ?? "") + $" | features avgR={avg:F2} n={kv.Value.Count} SOFT_SKIP";
                }
                else if (avg <= -0.30m)
                {
                    adj.SizeMult = Math.Min(adj.SizeMult <= 0 ? 1m : adj.SizeMult, 0.60m);
                    adj.Note = (adj.Note ?? "") + $" | features avgR={avg:F2} n={kv.Value.Count}";
                }
                mem.BySymbol[kv.Key] = adj;
            }
            mem.UpdatedUtc = DateTime.UtcNow;
            lock (LockFor(clientId))
                File.WriteAllText(MemoryPath(clientId), JsonSerializer.Serialize(mem, JsonOpt));
            _log.LogInformation("[JOURNAL] RebuildFromFeatures {c}: {n} symbols touched", clientId, bySym.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[JOURNAL] RebuildFromFeatures failed {c}", clientId);
        }
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
        var rSamples = valid.Where(t => t.RealizedR != 0m && Math.Abs(t.RealizedR) <= 3.0m)
            .Select(t => t.RealizedR).ToList();
        decimal avgRealizedR = rSamples.Count > 0 ? rSamples.Average() : 0m;
        if (avgRealizedR <= -0.35m && rSamples.Count >= 2)
            stopRate = Math.Max(stopRate, 0.50m);

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

            sizeMult = consecutiveStops >= 3 ? 0.70m
                : consecutiveStops == 2 ? 0.82m
                : stopRate >= 0.55m ? 0.88m
                : 0.93m;
            // Offline expectancy from RealizedR (feature-store / journal)
            if (avgRealizedR <= -0.55m && rSamples.Count >= 3)
                sizeMult = Math.Min(sizeMult, 0.15m); // soft-skip: near-zero size
            else if (avgRealizedR <= -0.40m && rSamples.Count >= 3)
                sizeMult = Math.Min(sizeMult, 0.40m);
            else if (avgRealizedR <= -0.25m && rSamples.Count >= 2)
                sizeMult = Math.Min(sizeMult, 0.65m);
            levMult = consecutiveStops >= 3 ? 0.85m : 1.0m;

            note = "smart SL/TP after SL history stopsInRow=" + consecutiveStops
                + " missTp=" + avgMiss.ToString("F2") + " (conf untouched)";
        }
        else if (wins >= 2 && stops == 0)
        {
            sizeMult = 0.95m;
            note = "win streak hold/slight ease (conf untouched)";
        }

        // Standalone soft-skip even without stop-streak block
        bool softSkip = avgRealizedR <= -0.55m && rSamples.Count >= 3;
        if (softSkip)
        {
            sizeMult = Math.Min(sizeMult, 0.15m);
            note = (note == "neutral" ? "offline soft-skip" : note) + " SOFT_SKIP";
        }

        if (rSamples.Count > 0)
            note = $"{note} | avgR={avgRealizedR:F2} nR={rSamples.Count}";
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
            SoftSkip = softSkip,
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
