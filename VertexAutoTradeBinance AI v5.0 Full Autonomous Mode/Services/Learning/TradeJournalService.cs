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
    private readonly double _halfLifeDays;
    private readonly decimal _probeMinConf;
    private readonly ICloseContextProvider? _closeCtx;
    private readonly SqliteJournalStore? _sqlite;
    private readonly DailyDrawdownGuard? _dailyDd;
    private readonly TelegramNotificationService? _tg;
    private static readonly JsonSerializerOptions JsonOpt = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> Locks = new();

    public TradeJournalService(IConfiguration cfg, ILogger<TradeJournalService> log, SqliteJournalStore? sqlite = null,
        DailyDrawdownGuard? dailyDd = null, TelegramNotificationService? tg = null,
        ICloseContextProvider? closeCtx = null)
    {
        _log = log;
        _cfg = cfg;
        _sqlite = sqlite;
        _dailyDd = dailyDd;
        _tg = tg;
        _closeCtx = closeCtx;
        _enginesRoot = cfg["SharedData:Root"]
            ?? Path.Combine(AppContext.BaseDirectory, "engines");
        _windowDays = Math.Clamp(cfg.GetValue("TradeMemory:WindowDays", 30), 7, 90);
        _halfLifeDays = Math.Clamp(cfg.GetValue("TradeMemory:HalfLifeDays", 7.0), 1.0, 30.0);
        _probeMinConf = cfg.GetValue("TradeMemory:ProbeMinConfidence", 0.72m);
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
        // Phase 1 final: live BTC/ETH deltas + news + probe flag
        if (!e.IsMemoryProbe)
            e.IsMemoryProbe = ProbeEntryTracker.TryConsume(e.Symbol);
        try
        {
            if (_closeCtx != null && e.BtcDeltaPctAtClose == 0 && e.EthDeltaPctAtClose == 0)
            {
                var (btc, eth, news) = _closeCtx.GetContext();
                e.BtcDeltaPctAtClose = btc;
                e.EthDeltaPctAtClose = eth;
                if (string.IsNullOrWhiteSpace(e.SlAttributionCode))
                {
                    e.SlAttributionCode = SlAttribution.Classify(
                        e.CloseReason, e.RealizedPnl, btc, eth, news,
                        e.InitialRiskPrice, e.EntryPrice, e.ExitPrice);
                }
            }
        }
        catch { }

        if (string.IsNullOrWhiteSpace(e.SlAttributionCode))
        {
            e.SlAttributionCode = SlAttribution.Classify(
                e.CloseReason, e.RealizedPnl, e.BtcDeltaPctAtClose, e.EthDeltaPctAtClose,
                newsSpikeActive: false, e.InitialRiskPrice, e.EntryPrice, e.ExitPrice);
        }
        if (SlAttribution.IsStop(e.CloseReason, e.RealizedPnl) &&
            (e.CloseReason ?? "").IndexOf("SL_", StringComparison.OrdinalIgnoreCase) < 0)
            e.CloseReason = e.SlAttributionCode;
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
                        var line = JsonSerializer.Serialize(new { e.Symbol, e.Side, e.Source, e.RealizedPnl, e.RealizedR, e.InitialRiskPrice, e.CloseReason, e.SlAttributionCode, e.BtcDeltaPctAtClose, e.EthDeltaPctAtClose, e.IsMemoryProbe, e.ClosedAtUtc, e.OpenedAtUtc, e.SignalConf, e.Leverage });
                        File.AppendAllText(featPath, line + Environment.NewLine);
                    } catch { }
                }
            }
            else
            {
                // features still useful for offline skip model
                try {
                    var featPath = Path.Combine(ClientDir(e.ClientId), "trade-features.jsonl");
                    var line = JsonSerializer.Serialize(new { e.Symbol, e.Side, e.Source, e.RealizedPnl, e.RealizedR, e.InitialRiskPrice, e.CloseReason, e.SlAttributionCode, e.BtcDeltaPctAtClose, e.EthDeltaPctAtClose, e.IsMemoryProbe, e.ClosedAtUtc, e.OpenedAtUtc, e.SignalConf, e.Leverage });
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
            var cutoff = DateTime.UtcNow.AddDays(-_windowDays);
            List<TradeJournalEntry> recent;
            if (_sqlite != null && (SqliteOnly || !File.Exists(JournalPath(clientId))))
                recent = _sqlite.GetRecent(2000).Where(x => x.ClosedAtUtc >= cutoff).ToList();
            else
            {
                var journal = LoadJournal(JournalPath(clientId));
                recent = journal.Entries.Where(x => x.ClosedAtUtc >= cutoff).ToList();
            }
            var mem = new SymbolMemoryFile { UpdatedUtc = DateTime.UtcNow };
            foreach (var g in recent.GroupBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase))
                mem.BySymbol[g.Key] = Compute(g.Key, g.ToList());
            Directory.CreateDirectory(ClientDir(clientId));
            if (!SqliteOnly)
            {
                lock (LockFor(clientId))
                    File.WriteAllText(MemoryPath(clientId), JsonSerializer.Serialize(mem, JsonOpt));
            }
            // Always mirror into SQLite SymbolMemory
            if (_sqlite != null)
            {
                foreach (var kv in mem.BySymbol)
                {
                    var a = kv.Value;
                    decimal avgR = 0m;
                    try
                    {
                        var samples = recent.Where(x => string.Equals(x.Symbol, kv.Key, StringComparison.OrdinalIgnoreCase)
                            && Math.Abs(x.RealizedR) <= 3m && x.RealizedR != 0m).Select(x => x.RealizedR).ToList();
                        if (samples.Count > 0) avgR = samples.Average();
                    }
                    catch { }
                    decimal winRate = 0m;
                    var symTrades = recent.Where(x => string.Equals(x.Symbol, kv.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (symTrades.Count > 0)
                        winRate = (decimal)symTrades.Count(x => x.RealizedPnl > 0) / symTrades.Count;
                    _sqlite.UpsertSymbolMemory(kv.Key, a.SizeMult, a.SoftSkip, avgR, winRate, a.Note ?? "");
                }
                _log.LogInformation("[SQLITE-TRACE] SymbolMemory synced {n} symbols client={c}", mem.BySymbol.Count, clientId);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[JOURNAL] rebuild failed {c}", clientId);
        }
    }

    /// <summary>Write a signal decision row into SQLite SignalLogs (and log).</summary>
    public void LogSignal(string symbol, string type, string action, string? blockReason = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(symbol)) return;
            if (_sqlite == null)
            {
                _log.LogDebug("[SQLITE-TRACE] LogSignal skipped — no SqliteJournalStore ({s} {a})", symbol, action);
                return;
            }
            _sqlite.LogSignal(symbol, type ?? "", action ?? "", blockReason);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[SQLITE-TRACE] LogSignal failed {s}", symbol);
        }
    }

    /// <summary>
    /// After SL on THIS symbol: contextual weights + time-decay + optional probe.
    /// NEVER cuts confidence score on the signal itself.
    /// </summary>
    private SymbolAdjustments Compute(string symbol, List<TradeJournalEntry> trades)
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

        double halfLife = Math.Max(1.0, _halfLifeDays);
        DateTime now = DateTime.UtcNow;
        double W(TradeJournalEntry x)
        {
            double dt = Math.Max(0, (now - x.ClosedAtUtc).TotalDays);
            return Math.Exp(-dt / halfLife);
        }

        decimal stopsW = 0, winsW = 0, totalW = 0;
        int stops = 0, wins = 0;
        var attrCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var tr in valid)
        {
            double w = W(tr);
            totalW += (decimal)w;
            bool isSl = SlAttribution.IsStop(tr.CloseReason, tr.RealizedPnl);
            if (isSl)
            {
                stops++;
                var code = string.IsNullOrWhiteSpace(tr.SlAttributionCode)
                    ? SlAttribution.Classify(tr.CloseReason, tr.RealizedPnl, tr.BtcDeltaPctAtClose, tr.EthDeltaPctAtClose, false, tr.InitialRiskPrice, tr.EntryPrice, tr.ExitPrice)
                    : tr.SlAttributionCode;
                decimal aw = SlAttribution.Weight(code);
                stopsW += (decimal)w * aw;
                attrCounts[code] = attrCounts.GetValueOrDefault(code) + 1;
            }
            else if (tr.RealizedPnl > 0)
            {
                wins++;
                winsW += (decimal)w;
            }
        }

        // Weighted consecutive strategy-fail stops (full weight only)
        decimal consecutiveStopsW = 0;
        int consecutiveStops = 0;
        foreach (var tr in valid.OrderByDescending(x => x.ClosedAtUtc))
        {
            if (!SlAttribution.IsStop(tr.CloseReason, tr.RealizedPnl)) break;
            var code = string.IsNullOrWhiteSpace(tr.SlAttributionCode) ? tr.CloseReason : tr.SlAttributionCode;
            decimal aw = SlAttribution.Weight(code);
            if (aw >= 0.99m)
            {
                consecutiveStops++;
                consecutiveStopsW += aw;
            }
            else
            {
                // discounted external shock — does not extend consecutive strategy streak
                break;
            }
        }

        var missScores = new List<(decimal v, double w)>();
        var tightSlScores = new List<(decimal v, double w)>();
        foreach (var tr in valid)
        {
            if (!SlAttribution.IsStop(tr.CloseReason, tr.RealizedPnl)) continue;
            double w = W(tr);
            decimal risk = tr.InitialRiskPrice > 0 ? tr.InitialRiskPrice
                : (tr.StopLoss is > 0 ? Math.Abs(tr.EntryPrice - tr.StopLoss.Value) : Math.Abs(tr.EntryPrice - tr.ExitPrice));
            if (risk <= 0) continue;
            decimal move = Math.Abs(tr.EntryPrice - tr.ExitPrice);
            tightSlScores.Add((Math.Min(2m, move / risk), w));
            decimal tp1Dist = 0m;
            if (tr.TakeProfits != null && tr.TakeProfits.Count > 0)
                tp1Dist = Math.Abs(tr.TakeProfits[0] - tr.EntryPrice);
            if (tp1Dist > 0)
            {
                decimal prog = Math.Clamp(move / tp1Dist, 0m, 1m);
                missScores.Add((1m - prog, w));
            }
            else missScores.Add((0.7m, w));
        }

        decimal avgMiss = WeightedAvg(missScores);
        decimal avgTight = WeightedAvg(tightSlScores);
        decimal stopRate = totalW > 0 ? stopsW / totalW : 0m;

        var rSamples = valid
            .Where(tr => tr.RealizedR != 0m && Math.Abs(tr.RealizedR) <= 3.0m)
            .Select(tr => ((decimal)W(tr), tr.RealizedR))
            .ToList();
        decimal avgRealizedR = 0m;
        if (rSamples.Count > 0)
        {
            decimal sw = rSamples.Sum(x => x.Item1);
            if (sw > 0) avgRealizedR = rSamples.Sum(x => x.Item1 * x.Item2) / sw;
        }
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
            if (avgRealizedR <= -0.55m && rSamples.Count >= 3)
                sizeMult = Math.Min(sizeMult, 0.15m);
            else if (avgRealizedR <= -0.40m && rSamples.Count >= 3)
                sizeMult = Math.Min(sizeMult, 0.40m);
            else if (avgRealizedR <= -0.25m && rSamples.Count >= 2)
                sizeMult = Math.Min(sizeMult, 0.65m);
            levMult = consecutiveStops >= 3 ? 0.85m : 1.0m;

            note = "smart SL/TP stopsInRow=" + consecutiveStops
                + " missTp=" + avgMiss.ToString("F2")
                + " halfLife=" + halfLife.ToString("F0")
                + " (conf untouched)";
        }
        else if (wins >= 2 && stops == 0)
        {
            sizeMult = 0.95m;
            note = "win streak hold/slight ease (conf untouched)";
        }

        bool softSkip = avgRealizedR <= -0.55m && rSamples.Count >= 3;
        if (softSkip)
        {
            sizeMult = Math.Min(sizeMult, 0.15m);
            note = (note == "neutral" ? "offline soft-skip" : note) + " SOFT_SKIP";
        }

        // Phase 3 final: successful probe TP clears SoftSkip
        var probeWins = valid.Where(tr => tr.IsMemoryProbe && (tr.RealizedPnl > 0
            || (tr.CloseReason ?? "").IndexOf("TP", StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        if (probeWins.Count > 0)
        {
            softSkip = false;
            sizeMult = Math.Max(sizeMult, 0.55m);
            note += " | Probe SUCCESS -> SoftSkip clear";
            _log.LogInformation("[MEMORY] {sym} Probe SUCCESS -> SoftSkip clear (n={n})", symbol, probeWins.Count);
        }

        bool allowProbe = softSkip;
        decimal probeSize = 0.25m;

        if (attrCounts.Count > 0)
            note += " | attrs=" + string.Join(",", attrCounts.Select(kv => kv.Key + ":" + kv.Value));
        if (rSamples.Count > 0)
            note = $"{note} | avgR={avgRealizedR:F2} nR={rSamples.Count} stopRateW={stopRate:F2}";

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
            AllowProbe = allowProbe,
            ProbeSizeMult = probeSize,
            Note = note
        };
    }

    private static decimal WeightedAvg(List<(decimal v, double w)> xs)
    {
        if (xs == null || xs.Count == 0) return 0m;
        double sw = xs.Sum(x => x.w);
        if (sw <= 1e-9) return xs.Average(x => x.v);
        return (decimal)(xs.Sum(x => (double)x.v * x.w) / sw);
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
