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
    public event Action<TradeJournalEntry>? TradeClosed;

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

    private readonly ConcurrentDictionary<string, (DateTime at, SideLesson lesson)> _sideCache = new();
    private readonly OnlineTradeNet _net;

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
        var configured = cfg["SharedData:EnginesRoot"];
        if (string.IsNullOrWhiteSpace(configured))
            configured = cfg["SharedData:Root"] ?? Path.Combine(AppContext.BaseDirectory, "engines");
        _enginesRoot = LiftClientFolder(configured);
        _net = new OnlineTradeNet(Path.Combine(_enginesRoot, "ml_meta_net.json"));
        try { BootstrapNet(); } catch (Exception ex) { _log.LogWarning(ex, "[META] bootstrap skipped"); }
    }

    private static string LiftClientFolder(string path)
    {
        var root = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(root);
        if (name.StartsWith("client_", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(root);
            if (!string.IsNullOrEmpty(parent)) return parent;
        }
        return root;
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
        try { ObserveClose(e); } catch (Exception ex) { _log.LogDebug(ex, "[META] observe failed {s}", e.Symbol); }
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
            _sideCache.Clear();
            try { RebuildFromFeatures(e.ClientId); } catch { }
            try
            {
                bool isSl = (e.CloseReason ?? "").IndexOf("SL", StringComparison.OrdinalIgnoreCase) >= 0;
                _dailyDd?.OnTradeClosed(e.RealizedPnl, isSl);
                _tg?.Exit(e.Symbol, e.RealizedR, e.RealizedPnl, e.CloseReason ?? "");
            }
            catch { }

            // Risk state is independent of journal persistence so an account
            // circuit breaker still sees a close if notification/DD reporting fails.
            try { TradeClosed?.Invoke(e); }
            catch (Exception ex) { _log.LogError(ex, "[RISK] close event handler failed {s}", e.Symbol); }
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
    /// Outcome memory for one symbol and side, pooled across every client book.
    /// A short that keeps stopping out is not allowed to repeat just because
    /// some other symbol, or the opposite side, was fine.
    /// </summary>
    public SideLesson EvaluateSide(string symbol, string side)
    {
        var sym = (symbol ?? "").Trim();
        var sd = NormSide(side);
        if (sym.Length == 0 || sd.Length == 0)
            return new SideLesson { Symbol = sym, Side = sd };
        var key = sym + "|" + sd;
        if (_sideCache.TryGetValue(key, out var hit) && (DateTime.UtcNow - hit.at).TotalSeconds < 20)
            return hit.lesson;
        var lesson = ComputeSide(sym, sd);
        _sideCache[key] = (DateTime.UtcNow, lesson);
        return lesson;
    }

    private SideLesson ComputeSide(string symbol, string side)
    {
        var cutoff = DateTime.UtcNow.AddDays(-14);
        var rows = new List<TradeJournalEntry>();
        foreach (var e in LoadOutcomeRows())
        {
            if (!string.Equals(e.Symbol, symbol, StringComparison.OrdinalIgnoreCase)) continue;
            if (NormSide(e.Side) != side) continue;
            if (e.ClosedAtUtc < cutoff) continue;
            if (e.EntryPrice <= 0 || e.ExitPrice <= 0) continue;
            rows.Add(e);
        }
        if (rows.Count == 0)
            return new SideLesson { Symbol = symbol, Side = side, Note = "no closed trades on this side" };

        double half = Math.Max(1.0, _halfLifeDays);
        var now = DateTime.UtcNow;
        decimal sw = 0m, sr = 0m;
        foreach (var e in rows)
        {
            var r = e.RealizedR;
            if (r == 0m && e.InitialRiskPrice > 0 && e.Qty > 0)
                r = e.RealizedPnl / (e.InitialRiskPrice * e.Qty);
            if (r > 5m) r = 5m;
            if (r < -5m) r = -5m;
            double w = Math.Exp(-Math.Max(0, (now - e.ClosedAtUtc).TotalDays) / half);
            sw += (decimal)w;
            sr += (decimal)w * r;
        }
        decimal avg = sw > 0 ? sr / sw : 0m;

        int streak = 0;
        foreach (var e in rows.OrderByDescending(x => x.ClosedAtUtc))
        {
            if (e.RealizedPnl < 0 || (e.CloseReason ?? "").Contains("SL", StringComparison.OrdinalIgnoreCase))
                streak++;
            else break;
        }
        int wins = rows.Count(e => e.RealizedPnl > 0);
        bool recentTwoWon = rows.OrderByDescending(x => x.ClosedAtUtc).Take(2).Count(e => e.RealizedPnl > 0) == 2
                            && rows.Count >= 2;

        bool block = !recentTwoWon && rows.Count >= 4 && avg <= -0.30m && streak >= 3;
        decimal size = 1m;
        decimal pen = 0m;
        decimal widen = 0m;
        if (!block && !recentTwoWon && rows.Count >= 3 && avg < -0.10m)
        {
            size = avg <= -0.40m ? 0.45m : 0.60m;
            pen = 0.06m;
            widen = streak >= 2 ? 0.22m : 0.12m;
        }
        else if (block)
        {
            size = 0m;
            widen = 0.25m;
        }

        var note = $"n={rows.Count} avgR={avg:F2} streak={streak} wins={wins}"
                   + (block ? " BLOCK" : size < 1m ? " SIZE_CUT" : "");
        if (rows.Count >= 3)
            _log.LogInformation("[MEMORY] {sym} {side} {note}", symbol, side, note);

        return new SideLesson
        {
            Symbol = symbol,
            Side = side,
            SampleCount = rows.Count,
            AvgR = Math.Round(avg, 3),
            LossStreak = streak,
            Block = block,
            SizeMult = size,
            ConfidencePenalty = pen,
            WidenStopFrac = block ? 0m : widen,
            Note = note
        };
    }

    private IEnumerable<TradeJournalEntry> LoadOutcomeRows()
    {
        var list = new List<TradeJournalEntry>();
        if (!Directory.Exists(_enginesRoot)) return list;
        foreach (var path in OutcomeFiles())
        {
            try
            {
                if (path.EndsWith("trade-journal.json", StringComparison.OrdinalIgnoreCase))
                {
                    var file = LoadJournal(path);
                    if (file.Entries != null) list.AddRange(file.Entries);
                }
                else
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (!doc.RootElement.TryGetProperty("History", out var hist) || hist.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var el in hist.EnumerateArray())
                    {
                        decimal pnl = el.TryGetProperty("RealizedPnl", out var p) && p.TryGetDecimal(out var pv) ? pv : 0m;
                        decimal r = el.TryGetProperty("RealizedR", out var rr) && rr.TryGetDecimal(out var rv) ? rv : 0m;
                        decimal entry = el.TryGetProperty("EntryPrice", out var en) && en.TryGetDecimal(out var ev) ? ev : 0m;
                        decimal exit = el.TryGetProperty("ExitPrice", out var ex) && ex.TryGetDecimal(out var xv) ? xv : 0m;
                        DateTime closed = DateTime.UtcNow;
                        if (el.TryGetProperty("ClosedAtUtc", out var c) && c.TryGetDateTime(out var cv)) closed = cv;
                        DateTime opened = closed;
                        if (el.TryGetProperty("OpenedAtUtc", out var o) && o.TryGetDateTime(out var ov)) opened = ov;
                        decimal riskPx = el.TryGetProperty("InitialRiskPrice", out var ir) && ir.TryGetDecimal(out var irv) ? irv : 0m;
                        list.Add(new TradeJournalEntry
                        {
                            Symbol = el.TryGetProperty("Symbol", out var s) ? s.GetString() ?? "" : "",
                            Side = el.TryGetProperty("Side", out var sd) ? sd.GetString() ?? "" : "",
                            RealizedPnl = pnl,
                            RealizedR = r,
                            EntryPrice = entry,
                            ExitPrice = exit,
                            InitialRiskPrice = riskPx,
                            SetupReason = el.TryGetProperty("SetupReason", out var sr) ? sr.GetString() ?? "" : "",
                            StrategyLeg = el.TryGetProperty("StrategyLeg", out var lg) ? lg.GetString() ?? "" : "",
                            CloseReason = el.TryGetProperty("CloseReason", out var cr) ? cr.GetString() ?? "" : "",
                            OpenedAtUtc = opened,
                            ClosedAtUtc = closed,
                            Qty = 1m
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[MEMORY] skip outcome file {p}", path);
            }
        }
        return list
            .GroupBy(e => (e.Symbol ?? "") + "|" + NormSide(e.Side) + "|"
                          + (e.ClosedAtUtc.Ticks / TimeSpan.TicksPerMinute) + "|"
                          + e.RealizedPnl.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Select(g => g.First())
            .ToList();
    }

    private IEnumerable<string> OutcomeFiles()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_enginesRoot)) yield break;
        foreach (var dir in Directory.GetDirectories(_enginesRoot, "client_*"))
        {
            foreach (var file in new[] { "trade-journal.json", "demo-account.json" })
            {
                var p = Path.Combine(dir, file);
                if (File.Exists(p) && seen.Add(p)) yield return p;
            }
            foreach (var nested in Directory.GetDirectories(dir, "client_*"))
            {
                foreach (var file in new[] { "trade-journal.json", "demo-account.json" })
                {
                    var p = Path.Combine(nested, file);
                    if (File.Exists(p) && seen.Add(p)) yield return p;
                }
            }
        }
    }

    public MetaNetScore ScoreMeta(
        string symbol, string side, decimal confidence, decimal riskPct, bool trendLeg, int hourUtc)
    {
        var prior = EvaluateSide(symbol, side);
        var sign = NormSide(side) == "SHORT" ? -1 : 1;
        var x = OnlineTradeNet.Features(confidence, sign, hourUtc, prior.AvgR, prior.LossStreak, riskPct, trendLeg);
        var score = _net.Score(x);
        if (score.Updates >= 8)
            _log.LogInformation("[META] {sym} {side} {note}", symbol, side, score.Note);
        return score;
    }

    private void ObserveClose(TradeJournalEntry e)
    {
        if (Math.Abs(e.RealizedR) < 0.05m && Math.Abs(e.RealizedPnl) < 0.5m) return;
        var prior = EvaluateSide(e.Symbol, e.Side);
        var risk = e.EntryPrice > 0 && e.InitialRiskPrice > 0 ? e.InitialRiskPrice / e.EntryPrice : 0.01m;
        var conf = e.SignalConf > 0 ? e.SignalConf : 0.55m;
        var sign = NormSide(e.Side) == "SHORT" ? -1 : 1;
        var x = OnlineTradeNet.Features(conf, sign, e.OpenedAtUtc.Hour, prior.AvgR, prior.LossStreak, risk, IsTrend(e));
        var loss = e.RealizedPnl < 0
                   || (e.CloseReason ?? "").Contains("SL", StringComparison.OrdinalIgnoreCase);
        _net.Learn(x, win: !loss, sampleWeight: loss ? 1.4 : 1);
        _log.LogInformation("[META] learned {sym} {side} {y} updates={n}",
            e.Symbol, e.Side, loss ? "loss" : "win", _net.Updates);
    }

    private void BootstrapNet()
    {
        if (_net.Updates > 0) return;
        var hist = new Dictionary<string, List<decimal>>(StringComparer.OrdinalIgnoreCase);
        var batch = new List<(double[] X, bool Win, double Weight)>();
        foreach (var e in LoadOutcomeRows().OrderBy(x => x.ClosedAtUtc))
        {
            if (e.EntryPrice <= 0 || string.IsNullOrWhiteSpace(e.Symbol)) continue;
            if (Math.Abs(e.RealizedR) < 0.05m && Math.Abs(e.RealizedPnl) < 0.5m) continue;
            var key = e.Symbol + "|" + NormSide(e.Side);
            if (!hist.TryGetValue(key, out var prev)) hist[key] = prev = new List<decimal>();
            decimal avg = prev.Count == 0 ? 0 : prev.Skip(Math.Max(0, prev.Count - 8)).Average();
            var streak = 0;
            for (var i = prev.Count - 1; i >= 0 && prev[i] < 0; i--) streak++;
            var risk = e.InitialRiskPrice > 0 ? e.InitialRiskPrice / e.EntryPrice : 0.01m;
            var conf = e.SignalConf > 0 ? e.SignalConf : 0.55m;
            var x = OnlineTradeNet.Features(
                conf, NormSide(e.Side) == "SHORT" ? -1 : 1, e.OpenedAtUtc.Hour, avg, streak, risk, IsTrend(e));
            var loss = e.RealizedPnl < 0
                       || (e.CloseReason ?? "").Contains("SL", StringComparison.OrdinalIgnoreCase);
            if (e.RealizedPnl == 0 && !loss) { prev.Add(e.RealizedR); continue; }
            batch.Add((x, !loss, loss ? 1.4 : 1));
            prev.Add(e.RealizedR == 0 ? (loss ? -1m : 1m) : e.RealizedR);
        }
        _net.Bootstrap(batch);
        if (batch.Count > 0)
            _log.LogInformation("[META] bootstrapped {n} closes into ml_meta_net.json", batch.Count);
    }

    private static bool IsTrend(TradeJournalEntry e)
    {
        if (string.Equals(e.StrategyLeg, "TREND", StringComparison.OrdinalIgnoreCase)) return true;
        var s = e.SetupReason ?? "";
        return s.Contains("CORE", StringComparison.OrdinalIgnoreCase)
               || s.Contains("TREND", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormSide(string? side)
    {
        var s = (side ?? "").Trim();
        if (s.Equals("BUY", StringComparison.OrdinalIgnoreCase) || s.Equals("LONG", StringComparison.OrdinalIgnoreCase))
            return "LONG";
        if (s.Equals("SELL", StringComparison.OrdinalIgnoreCase) || s.Equals("SHORT", StringComparison.OrdinalIgnoreCase))
            return "SHORT";
        return s.ToUpperInvariant();
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
            AvgRealizedR = Math.Round(avgRealizedR, 3),
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
