using System.Collections.Concurrent;
using System.Text.Json;
using VertexAutoTradeBinance8.Services.Entry;
using VertexAutoTradeBinance8.Services.Learning;
using VertexAutoTradeBinance8.Web.Services.Auth;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// Paper book. Opens only from approved_entries — the same decision the
/// engine just sent to the exchange (or the paper-only decision when the
/// live wallet is empty). Raw live_signals are watch-list, not entries.
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
        try
        {
            int rec = _demo.ReconcileOpenProtectiveLevels();
            if (rec > 0)
                _log.LogWarning("[DEMO-AUTO][RECONCILE] adjusted protective levels on {n} open Demo position(s)", rec);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[DEMO-AUTO][RECONCILE] skipped");
        }
        _log.LogInformation(
            "[DEMO-AUTO] started (approved_entries only, same decision as Live) at {t:o} shared={root}",
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
        // Own book. Do not inherit Trading:MaxOpenPositions / HardRisk max
        // (that is the live slot count — the one that logged MAX_POSITIONS:6>=3).
        int n = _cfg.GetValue("Trading:DemoMaxOpenPositions", 8);
        return n > 0 ? n : 8;
    }

    /// <summary>
    /// Registry empty or out of sync with the folders the engine already
    /// sized against (client_<id>/demo-account.json).
    /// </summary>
    private List<ClientRecord> DiscoverDemoLedgers()
    {
        var list = new List<ClientRecord>();
        var engines = _cfg["SharedData:EnginesRoot"];
        if (string.IsNullOrWhiteSpace(engines))
        {
            var root = _cfg["SharedData:Root"] ?? "";
            engines = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        if (string.IsNullOrWhiteSpace(engines) || !Directory.Exists(engines))
            return list;

        foreach (var dir in Directory.GetDirectories(engines))
        {
            var name = Path.GetFileName(dir);
            if (!name.StartsWith("client_", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(Path.Combine(dir, "demo-account.json"))) continue;
            var id = name["client_".Length..];
            if (string.IsNullOrWhiteSpace(id)) continue;
            list.Add(new ClientRecord
            {
                Id = id,
                IsActive = true,
                TradingMode = "demo",
                ParallelDemoEnabled = true,
                DemoBalance = 10_000m
            });
        }
        if (list.Count > 0)
            _log.LogInformation("[DEMO-AUTO] using {n} demo ledger folder(s) under {root}", list.Count, engines);
        return list;
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            if (File.Exists(Path.Combine(_sharedRoot, "emergency_kill.flag")))
                return;
        }
        catch { }

        var liveOn = _cfg.GetValue("Trading:EnableExecution", true);
        var clients = await _db.GetClientsForIndependentDemoAsync(liveOn);
        if (clients.Count == 0)
            clients = DiscoverDemoLedgers();
        if (clients.Count == 0)
        {
            _log.LogInformation("[DEMO-AUTO] no demo ledger — paper entry not opened");
            return;
        }

        int opened = await ProcessApprovedAsync(clients, ct);

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
        foreach (var e in entries.OrderByDescending(x => x.Utc))
        {
            var key = $"appr|{e.Id}|{e.Symbol}";
            if (!_seen.TryAdd(key, 0)) continue;
            foreach (var client in clients)
            {
                decimal clientEquity = _demo.GetEquityForClient(client.Id, client.DemoBalance > 0 ? client.DemoBalance : 10_000m);
                decimal clientQty = ScaleApprovedQuantity(e, clientEquity);
                if (clientQty <= 0m)
                {
                    _journal?.LogSignal(e.Symbol, "DEMO", "REJECT_DEMO",
                        $"CLIENT_QTY_BELOW_MIN:{client.Id}; equity={clientEquity:F2}");
                    continue;
                }

                if (TryOpen(client.Id, client.DemoBalance, e.Symbol, e.Side, e.Entry, clientQty, e.Leverage, e.StopLoss, e.TakeProfits,
                        e.InitialRiskPrice, e.Reason ?? "APPROVED"))
                    n++;
            }
        }
        return n;
    }

    private static decimal ScaleApprovedQuantity(VertexAutoTradeBinance8.Services.Entry.ApprovedEntry entry,
        decimal clientEquity)
    {
        if (clientEquity <= 0m || entry.Qty <= 0m || entry.Entry <= 0m) return 0m;

        decimal qty = entry.Qty;
        if (entry.SizingEquityUsd > 0m)
            qty *= clientEquity / entry.SizingEquityUsd;

        decimal step = entry.QuantityStep > 0m ? entry.QuantityStep : 0.00000001m;
        qty = Math.Floor(qty / step) * step;

        if (qty <= 0m || (entry.MinQty > 0m && qty < entry.MinQty)) return 0m;
        if (entry.MinNotionalUsd > 0m && qty * entry.Entry < entry.MinNotionalUsd) return 0m;
        return qty;
    }

    private bool TryOpen(
        string clientId, decimal initialBalance, string symbol, string? sideRaw, decimal entry, decimal qty, decimal lev,
        decimal stopLoss, List<decimal>? tps, decimal riskPx, string reason)
    {
        try
        {
            _demo.BindClient(clientId, initialBalance > 0m ? initialBalance : 10_000m);
            int openN = _demo.GetOpenPositionCountForClient(clientId);
            if (openN >= GetMaxDemoPositions())
            {
                _log.LogInformation(
                    "[DEMO-AUTO] {sym} skipped — demo book full {n}>={max} (live slot cap is not used)",
                    symbol, openN, GetMaxDemoPositions());
                _journal?.LogSignal(symbol, "DEMO", "REJECT_DEMO", $"DEMO_MAX_OPEN {openN}");
                return false;
            }
            string side = (sideRaw ?? "").Contains("Sell", StringComparison.OrdinalIgnoreCase)
                          || (sideRaw ?? "").Contains("SHORT", StringComparison.OrdinalIgnoreCase)
                ? "SHORT" : "LONG";
            // User-configurable directional limit (Trading:*)
            string corrMode = _cfg.GetValue("Trading:CorrelationFilterMode", "Disabled") ?? "Disabled";
            int maxSameSide = _cfg.GetValue("Trading:MaxSameSidePositions", 2);
            if (!corrMode.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            {
                int sameSide = _demo.GetSameSideOpenCountForClient(clientId, side);
                int cap = maxSameSide;
                if (corrMode.Equals("DynamicCorrelation", StringComparison.OrdinalIgnoreCase) && cap <= 0)
                    cap = 2;
                if (cap > 0 && sameSide >= cap)
                {
                    _log.LogInformation(
                        "[DEMO-AUTO] {sym} skipped — demo book same-side {n}>={cap} (live positions are not counted)",
                        symbol, sameSide, cap);
                    _journal?.LogSignal(symbol, "DEMO", "REJECT_DEMO",
                        $"DEMO_DIR_MAX_SAME_SIDE:{sameSide}>={cap} mode={corrMode}");
                    return false;
                }
            }
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
}
