using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Services.Entry;
using VertexAutoTradeBinance8.Services.Learning;
using VertexAutoTradeBinance8.Web.Services.Auth;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// Demo execution mirror: opens ONLY Engine-approved entries (full PROC + RiskManager + 1m).
/// No local sizing/filters — parity 1:1 with Live decision path.
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
        var root = cfg["SharedData:Root"] ?? "";
        if (!string.IsNullOrEmpty(root))
            _seenFilePath = Path.Combine(root, "demo_approved_seen.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _startedUtc = DateTime.UtcNow;
        LoadSeenKeys();
        _log.LogInformation(
            "[DEMO-AUTO] started (approved-entries parity mode) at {t:o} seen={n}",
            _startedUtc, _seen.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogWarning(ex, "[DEMO-AUTO] tick failed"); }

            try
            {
                var root = _cfg["SharedData:Root"] ?? "";
                var path = Path.Combine(root, "approved_entries.json");
                if (Directory.Exists(root))
                {
                    using var fsw = new FileSystemWatcher(root)
                    {
                        Filter = "approved_entries.json",
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                        EnableRaisingEvents = true
                    };
                    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Wake(object s, FileSystemEventArgs e) { try { tcs.TrySetResult(); } catch { } }
                    fsw.Changed += Wake; fsw.Created += Wake;
                    var delay = Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);
                    await Task.WhenAny(tcs.Task, delay);
                    fsw.Changed -= Wake; fsw.Created -= Wake;
                }
                else await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);
            }
            catch (TaskCanceledException) { break; }
            catch { try { await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken); } catch (TaskCanceledException) { break; } }
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
            var root = _cfg["SharedData:Root"] ?? "";
            var flag = Path.Combine(root, "flatten_demo.flag");
            if (File.Exists(flag))
            {
                try { File.Delete(flag); } catch { }
                _log.LogWarning("[DEMO-AUTO] flatten_demo.flag consumed");
            }
        }
        catch { }

        // Kill flag blocks Demo same as Live
        try
        {
            var root = _cfg["SharedData:Root"] ?? "";
            if (File.Exists(Path.Combine(root, "emergency_kill.flag")))
            {
                _log.LogDebug("[DEMO-AUTO] kill flag active — skip");
                return;
            }
        }
        catch { }

        var clients = await _db.GetClientsWithParallelDemoAsync();
        if (clients.Count == 0) return;

        if (_approved == null)
        {
            _log.LogWarning("[DEMO-AUTO] IApprovedEntryReader missing — no entries");
            return;
        }

        var entries = await _approved.LoadRecentAsync(TimeSpan.FromMinutes(45), ct);
        if (entries.Count == 0) return;

        var notBefore = _startedUtc.AddMinutes(-1);
        foreach (var e in entries.Where(x => x.Utc >= notBefore).OrderByDescending(x => x.Utc))
        {
            var key = $"{e.Id}|{e.Symbol}|{e.Utc:o}";
            if (!_seen.TryAdd(key, 0)) continue;

            foreach (var client in clients)
            {
                try
                {
                    _demo.BindClient(client.Id);

                    int openN = _demo.GetOpenPositionCountForClient(client.Id);
                    if (openN >= GetMaxDemoPositions())
                    {
                        _journal?.LogSignal(e.Symbol, "DEMO", "REJECT_DEMO",
                            $"MAX_OPEN {openN}>={GetMaxDemoPositions()}");
                        continue;
                    }

                    string side = (e.Side ?? "").Contains("Sell", StringComparison.OrdinalIgnoreCase)
                                  || (e.Side ?? "").Contains("SHORT", StringComparison.OrdinalIgnoreCase)
                        ? "SHORT" : "LONG";

                    if (_demo.HasOpenSymbolForClient(client.Id, e.Symbol))
                    {
                        _journal?.LogSignal(e.Symbol, "DEMO", "REJECT_DEMO", "ALREADY_OPEN");
                        continue;
                    }

                    // Qty/SL/TP/Lev from Engine RiskManager — no local sizing
                    decimal qty = e.Qty;
                    if (qty <= 0)
                    {
                        _journal?.LogSignal(e.Symbol, "DEMO", "REJECT_DEMO", "QTY_ZERO");
                        continue;
                    }

                    var (ok, err) = _demo.OpenFromApproved(
                        client.Id,
                        e.Symbol,
                        side,
                        e.Entry,
                        qty,
                        (int)Math.Max(1, Math.Round(e.Leverage)),
                        e.StopLoss,
                        e.TakeProfits,
                        e.InitialRiskPrice > 0 ? e.InitialRiskPrice : Math.Abs(e.Entry - e.StopLoss),
                        e.Reason);

                    if (ok)
                    {
                        _log.LogInformation(
                            "[DEMO-AUTO] OPEN (approved) {user} {side} {sym} qty={q} @ {px} lev={lev}",
                            client.Id, side, e.Symbol, qty, e.Entry, e.Leverage);
                        _journal?.LogSignal(e.Symbol, "DEMO", "EXECUTE",
                            $"qty={qty} lev={e.Leverage} approvedId={e.Id}");
                    }
                    else
                    {
                        _log.LogDebug("[DEMO-AUTO] skip {sym}: {err}", e.Symbol, err);
                        _journal?.LogSignal(e.Symbol, "DEMO", "REJECT_DEMO", err ?? "open failed");
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[DEMO-AUTO] client {id} {sym}", client.Id, e.Symbol);
                }
            }
        }

        SaveSeenKeys();
        if (_seen.Count > 5000)
        {
            foreach (var k in _seen.Keys.Take(2000))
                _seen.TryRemove(k, out _);
        }
    }

    private void LoadSeenKeys()
    {
        try
        {
            if (string.IsNullOrEmpty(_seenFilePath) || !File.Exists(_seenFilePath)) return;
            var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_seenFilePath));
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
            var list = _seen.Keys.TakeLast(500).ToList();
            var tmp = _seenFilePath + ".tmp";
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(list));
            File.Move(tmp, _seenFilePath, overwrite: true);
        }
        catch { }
    }
}
