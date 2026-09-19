using System.Collections.Concurrent;
using System.Text.Json;
using VertexAutoTradeBinance8.Services.Entry;
using VertexAutoTradeBinance8.Services.Learning;
using VertexAutoTradeBinance8.Web.Services.Auth;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// Parallel DEMO: prefer Engine approved_entries; if none, open from live_signals
/// sized on Demo equity only (never requires Binance balance).
/// </summary>
public sealed class DemoAutoTradeService : BackgroundService
{
    private readonly DemoAccountService _demo;
    private readonly ClientDbService _db;
    private readonly IConfiguration _cfg;
    private readonly ILogger<DemoAutoTradeService> _log;
    private readonly TradeJournalService? _journal;
    private readonly IApprovedEntryReader? _approved;
    private readonly ConcurrentDictionary<string, byte> _seen = new();
    private string? _seenFilePath;
    private string _sharedRoot = "";
    private DateTime _startedUtc;

    public DemoAutoTradeService(
        DemoAccountService demo,
        ClientDbService db,
        IConfiguration cfg,
        ILogger<DemoAutoTradeService> log,
        TradeJournalService? journal = null,
        IApprovedEntryReader? approved = null)
    {
        _demo = demo;
        _db = db;
        _cfg = cfg;
        _log = log;
        _journal = journal;
        _approved = approved;
        _sharedRoot = cfg["SharedData:Root"] ?? "";
        if (!string.IsNullOrEmpty(_sharedRoot))
            _seenFilePath = Path.Combine(_sharedRoot, "demo_approved_seen.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _startedUtc = DateTime.UtcNow;
        LoadSeenKeys();
        _log.LogInformation(
            "[DEMO-AUTO] started (approved + live_signals fallback) at {t:o} shared={root}",
            _startedUtc, _sharedRoot);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogWarning(ex, "[DEMO-AUTO] tick failed"); }

            try
            {
                if (!string.IsNullOrEmpty(_sharedRoot) && Directory.Exists(_sharedRoot))
                {
                    using var fsw = new FileSystemWatcher(_sharedRoot)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                        EnableRaisingEvents = true
                    };
                    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Wake(object s, FileSystemEventArgs e)
                    {
                        var n = e.Name ?? "";
                        if (n.Contains("approved_entries", StringComparison.OrdinalIgnoreCase) ||
                            n.Contains("live_signals", StringComparison.OrdinalIgnoreCase))
                            try { tcs.TrySetResult(); } catch { }
                    }
                    fsw.Changed += Wake; fsw.Created += Wake;
                    await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(6), stoppingToken));
                    fsw.Changed -= Wake; fsw.Created -= Wake;
                }
                else await Task.Delay(TimeSpan.FromSeconds(6), stoppingToken);
            }
            catch (TaskCanceledException) { break; }
            catch { try { await Task.Delay(TimeSpan.FromSeconds(6), stoppingToken); } catch { break; } }
        }
    }

    private int GetMaxDemoPositions()
    {
        int n = _cfg.GetValue("Trading:MaxOpenPositions", 5);
        return n > 0 ? n : 5;
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            if (File.Exists(Path.Combine(_sharedRoot, "emergency_kill.flag")))
                return;
        }
        catch { }

        var clients = await _db.GetClientsWithParallelDemoAsync();
        if (clients.Count == 0)
        {
            _log.LogDebug("[DEMO-AUTO] no clients with ParallelDemo");
            return;
        }

        int opened = 0;
        opened += await ProcessApprovedAsync(clients, ct);
        if (opened == 0)
            opened += await ProcessLiveSignalsFallbackAsync(clients, ct);

        SaveSeenKeys();
        if (_seen.Count > 5000)
            foreach (var k in _seen.Keys.Take(2000))
                _seen.TryRemove(k, out _);
    }

    private async Task<int> ProcessApprovedAsync(List<ClientRecord> clients, CancellationToken ct)
    {
        if (_approved == null) return 0;
        var entries = await _approved.LoadRecentAsync(TimeSpan.FromMinutes(45), ct);
        if (entries.Count == 0) return 0;

        int n = 0;
        var notBefore = _startedUtc.AddMinutes(-2);
        foreach (var e in entries.Where(x => x.Utc >= notBefore).OrderByDescending(x => x.Utc))
        {
            var key = $"appr|{e.Id}|{e.Symbol}";
            if (!_seen.TryAdd(key, 0)) continue;
            foreach (var client in clients)
            {
                if (TryOpen(client.Id, e.Symbol, e.Side, e.Entry, e.Qty, e.Leverage, e.StopLoss, e.TakeProfits,
                        e.InitialRiskPrice, e.Reason ?? "APPROVED"))
                    n++;
            }
        }
        return n;
    }

    private async Task<int> ProcessLiveSignalsFallbackAsync(List<ClientRecord> clients, CancellationToken ct)
    {
        var path = Path.Combine(_sharedRoot, "live_signals.json");
        if (!File.Exists(path)) return 0;

        List<LiveSigRow> signals;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            signals = JsonSerializer.Deserialize<List<LiveSigRow>>(json) ?? new();
        }
        catch { return 0; }

        var cutoff = DateTime.UtcNow.AddMinutes(-40);
        var notBefore = _startedUtc.AddMinutes(-2);
        var candidates = signals
            .Where(s => s.Time >= cutoff && s.Time >= notBefore && s.Entry > 0 && s.StopLoss > 0
                        && !string.IsNullOrWhiteSpace(s.Symbol) && s.Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.Confidence)
            .Take(30)
            .ToList();

        int n = 0;
        foreach (var s in candidates)
        {
            var key = $"live|{s.Symbol}|{s.Side}|{s.Time:yyyyMMddHHmm}";
            if (!_seen.TryAdd(key, 0)) continue;

            foreach (var client in clients)
            {
                try
                {
                    _demo.BindClient(client.Id);
                    var avail = _demo.GetAvailableForClient(client.Id);
                    if (avail <= 0) avail = _demo.GetEquityForClient(client.Id);
                    if (avail <= 0)
                    {
                        _log.LogWarning("[DEMO-AUTO] client {id} demo balance 0 — skip", client.Id);
                        continue;
                    }

                    decimal qty = Size1R(s, avail);
                    if (qty <= 0) continue;

                    var tps = s.TakeProfits ?? new List<decimal>();
                    if (TryOpen(client.Id, s.Symbol, s.Side, s.Entry, qty, 10, s.StopLoss, tps,
                            Math.Abs(s.Entry - s.StopLoss), s.Reason ?? "LIVE_SIGNAL_FALLBACK"))
                        n++;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "[DEMO-AUTO] fallback {sym}", s.Symbol);
                }
            }
        }
        if (n > 0)
            _log.LogInformation("[DEMO-AUTO] live_signals fallback opened {n} (approved empty/stale)", n);
        return n;
    }

    private static decimal Size1R(LiveSigRow s, decimal available)
    {
        decimal entry = s.Entry;
        decimal sl = s.StopLoss;
        if (entry <= 0 || sl <= 0) return 0;
        decimal slDist = Math.Abs(entry - sl);
        if (slDist <= 0) return 0;
        string sym = (s.Symbol ?? "").ToUpperInvariant();
        bool major = sym is "BTCUSDT" or "ETHUSDT" or "BNBUSDT" or "SOLUSDT"
                     || sym.StartsWith("BTC") || sym.StartsWith("ETH");
        decimal riskFrac = major ? 0.0075m : 0.0060m;
        decimal budget = available * riskFrac;
        decimal hardCap = major
            ? Math.Min(150m, Math.Max(40m, available * 0.012m))
            : Math.Min(55m, Math.Max(18m, available * 0.0075m));
        if (budget > hardCap) budget = hardCap;
        decimal qty = budget / slDist;
        // min notional ~$5
        if (qty * entry < 5m && entry > 0)
            qty = 5m / entry;
        if (qty * slDist > budget * 2.5m)
            return 0;
        return Math.Round(qty, 6);
    }

    private bool TryOpen(
        string clientId, string symbol, string? sideRaw, decimal entry, decimal qty, decimal lev,
        decimal stopLoss, List<decimal>? tps, decimal riskPx, string reason)
    {
        try
        {
            _demo.BindClient(clientId);
            int openN = _demo.GetOpenPositionCountForClient(clientId);
            if (openN >= GetMaxDemoPositions())
            {
                _journal?.LogSignal(symbol, "DEMO", "REJECT_DEMO", $"MAX_OPEN {openN}");
                return false;
            }
            string side = (sideRaw ?? "").Contains("Sell", StringComparison.OrdinalIgnoreCase)
                          || (sideRaw ?? "").Contains("SHORT", StringComparison.OrdinalIgnoreCase)
                ? "SHORT" : "LONG";
            if (_demo.HasOpenSymbolForClient(clientId, symbol))
            {
                _journal?.LogSignal(symbol, "DEMO", "REJECT_DEMO", "ALREADY_OPEN");
                return false;
            }
            if (qty <= 0)
            {
                _journal?.LogSignal(symbol, "DEMO", "REJECT_DEMO", "QTY_ZERO");
                return false;
            }

            var (ok, err) = _demo.OpenFromApproved(
                clientId, symbol, side, entry, qty,
                (int)Math.Max(1, Math.Round(lev <= 0 ? 10 : lev)),
                stopLoss, tps, riskPx > 0 ? riskPx : Math.Abs(entry - stopLoss), reason);

            if (ok)
            {
                _log.LogInformation(
                    "[DEMO-AUTO] OPEN {user} {side} {sym} qty={q} @ {px} ({reason})",
                    clientId, side, symbol, qty, entry, reason);
                _journal?.LogSignal(symbol, "DEMO", "EXECUTE", $"qty={qty} {reason}");
                return true;
            }
            _log.LogDebug("[DEMO-AUTO] skip {sym}: {err}", symbol, err);
            _journal?.LogSignal(symbol, "DEMO", "REJECT_DEMO", err ?? "open failed");
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[DEMO-AUTO] open {sym}", symbol);
            return false;
        }
    }

    private void LoadSeenKeys()
    {
        try
        {
            if (string.IsNullOrEmpty(_seenFilePath) || !File.Exists(_seenFilePath)) return;
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_seenFilePath));
            if (list == null) return;
            foreach (var k in list.TakeLast(500))
                _seen.TryAdd(k, 0);
        }
        catch { }
    }

    private void SaveSeenKeys()
    {
        try
        {
            if (string.IsNullOrEmpty(_seenFilePath)) return;
            var dir = Path.GetDirectoryName(_seenFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _seenFilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_seen.Keys.TakeLast(500).ToList()));
            File.Move(tmp, _seenFilePath, overwrite: true);
        }
        catch { }
    }

    private sealed class LiveSigRow
    {
        public string Symbol { get; set; } = "";
        public string Side { get; set; } = "";
        public DateTime Time { get; set; }
        public decimal Entry { get; set; }
        public decimal StopLoss { get; set; }
        public List<decimal>? TakeProfits { get; set; }
        public decimal Confidence { get; set; }
        public string? Reason { get; set; }
    }
}
