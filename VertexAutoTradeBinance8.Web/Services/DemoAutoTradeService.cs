using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Web.Demo;
using VertexAutoTradeBinance8.Web.Services.Auth;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// When ParallelDemoEnabled is on for a user, new Engine signals ALWAYS open
/// paper positions on their DEMO balance.
///
/// Independent of AutoTrade ON/OFF and of Trading:EnableExecution.
/// AutoTrade only gates REAL Binance orders in the Engine; this worker
/// reads live_signals.json and trades the virtual account regardless.
/// </summary>
public sealed class DemoAutoTradeService : BackgroundService
{
    private readonly LiveSignalFileService _signals;
    private readonly DemoAccountService _demo;
    private readonly ClientDbService _db;
    private readonly ILogger<DemoAutoTradeService> _log;
    private readonly IConfiguration _cfg;
    private readonly VertexAutoTradeBinance8.Services.Learning.TradeJournalService? _journal;
    private readonly ConcurrentDictionary<string, byte> _seen = new();
    private DateTime _startedUtc = DateTime.UtcNow;
    private readonly string _seenFilePath;

    public DemoAutoTradeService(
        LiveSignalFileService signals,
        DemoAccountService demo,
        ClientDbService db,
        ILogger<DemoAutoTradeService> log,
        IConfiguration cfg,
        VertexAutoTradeBinance8.Services.Learning.TradeJournalService? journal = null)
    {
        _signals = signals;
        _demo = demo;
        _db = db;
        _log = log;
        _cfg = cfg;
        _journal = journal;
        var root = cfg["SharedData:Root"] ?? AppContext.BaseDirectory;
        _seenFilePath = Path.Combine(root, "demo-auto-seen.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _startedUtc = DateTime.UtcNow;
        LoadSeenKeys();
        _log.LogInformation(
            "[DEMO-AUTO] started at {t:o} — only NEW signals after start; seen={n} (no revive closed positions)",
            _startedUtc, _seen.Count);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogWarning(ex, "[DEMO-AUTO] tick failed"); }

            // Fast path: wake on live_signals.json change; backup poll 12s
            try
            {
                var root = _cfg["SharedData:Root"] ?? "";
                var sigPath = System.IO.Path.Combine(root, "live_signals.json");
                if (System.IO.File.Exists(sigPath) || System.IO.Directory.Exists(root))
                {
                    using var fsw = new FileSystemWatcher(root, "live_signals.json")
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                        EnableRaisingEvents = true
                    };
                    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Wake(object s, FileSystemEventArgs e) { try { tcs.TrySetResult(); } catch { } }
                    fsw.Changed += Wake; fsw.Created += Wake;
                    var delay = Task.Delay(TimeSpan.FromSeconds(12), stoppingToken);
                    await Task.WhenAny(tcs.Task, delay);
                    fsw.Changed -= Wake; fsw.Created -= Wake;
                }
                else await Task.Delay(TimeSpan.FromSeconds(12), stoppingToken);
            }
            catch (TaskCanceledException) { break; }
            catch { try { await Task.Delay(TimeSpan.FromSeconds(12), stoppingToken); } catch (TaskCanceledException) { break; } }
        }
    }

    /// <summary>From Trading:MaxOpenPositions (same key as LIVE). Fallback 5.</summary>
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
            var flag = System.IO.Path.Combine(root, "flatten_demo.flag");
            if (System.IO.File.Exists(flag))
            {
                // Engine already cleared demo-account.json; remove flag and refresh
                try { System.IO.File.Delete(flag); } catch { }
                _log.LogWarning("[DEMO-AUTO] flatten_demo.flag consumed — demo books force-closed by Engine");
            }
        }
        catch { }

        var clients = await _db.GetClientsWithParallelDemoAsync();
        if (clients.Count == 0) return;

        var signals = await _signals.LoadAsync();
        if (signals == null || signals.Count == 0) return;

        var cutoff = DateTime.UtcNow.AddMinutes(-45);
        // Prefer CORE_ signals, liquid symbols only, highest confidence first
        // LIVE-like: never re-open from stale signals after restart.
        // Signal must be at/after worker start (with small clock skew grace).
        var notBefore = _startedUtc.AddMinutes(-1);

        var candidates = signals
            .Where(s => s.Time >= cutoff
                        && s.Time >= notBefore
                        && s.Entry > 0
                        && !string.IsNullOrWhiteSpace(s.Symbol)
                        && s.Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
                        && (string.IsNullOrEmpty(s.Reason) || s.Reason.StartsWith("CORE_", StringComparison.OrdinalIgnoreCase))
                        && s.Confidence >= 55)
            .OrderByDescending(s => s.Confidence)
            .ThenByDescending(s => s.Time)
            .Take(15)
            .ToList();

        foreach (var sig in candidates)
        {
            var side = (sig.Side ?? "").Contains("Sell", StringComparison.OrdinalIgnoreCase)
                ? "SHORT" : "LONG";
            var sym = sig.Symbol.Trim().ToUpperInvariant();
            var keyBase = $"{sym}|{side}|{sig.Time:O}|{sig.Entry:F6}";

            foreach (var client in clients)
            {
                var key = client.Id + "|" + keyBase;
                if (!_seen.TryAdd(key, 0)) continue;

                // Cap concurrent demo positions — always from disk/ledger (not only bound session)
                try
                {
                    int openN = _demo.GetOpenPositionCountForClient(client.Id);
                    int maxDemo = GetMaxDemoPositions();
                    if (openN >= maxDemo)
                    {
                        _log.LogDebug("[DEMO-AUTO] {user} at max positions ({n}/{max})", client.Id, openN, maxDemo);
                        continue;
                    }
                    if (_demo.HasOpenSymbolForClient(client.Id, sym))
                    {
                        continue; // already in this symbol
                    }
                }
                catch { /* non-fatal */ }

                decimal price = sig.Entry;
                // 1R equity sizing + HardCap USD — parity with Engine RiskManager.
                // Was: marginFrac 8–10% × lev → $300–900 SL hits on alts.
                // Now: risk $ = min(riskFrac×equity, hardCap); qty = risk$ / |entry−SL|.
                bool major = sym is "BTCUSDT" or "ETHUSDT" or "BNBUSDT" or "SOLUSDT"
                             || sym.StartsWith("BTC", StringComparison.Ordinal)
                             || sym.StartsWith("ETH", StringComparison.Ordinal);
                int lev = major ? 10 : 5;

                decimal available = 10_000m;
                try
                {
                    available = Math.Max(50m, _demo.GetAvailableForClient(client.Id));
                }
                catch { /* keep default */ }

                var adj = _journal?.GetAdjustments(client.Id, sym)
                    ?? new VertexAutoTradeBinance8.Services.Learning.SymbolAdjustments();
                if (adj.SoftSkip || adj.SizeMult <= 0.20m)
                {
                    _log.LogInformation("[DEMO-AUTO] {user} SOFT_SKIP {sym} ({note})", client.Id, sym, adj.Note);
                    continue;
                }
                lev = Math.Max(1, (int)Math.Round(lev * (adj.LevMult > 0 ? adj.LevMult : 1m)));

                decimal slDist = (sig.StopLoss > 0) ? Math.Abs(price - sig.StopLoss) : 0m;
                // If no SL on signal, use ~1.2% of price as proxy (must not blow size)
                if (slDist <= 0) slDist = price * 0.012m;

                decimal riskFrac = major ? 0.0075m : 0.0060m;
                decimal riskBudget1R = available * riskFrac;
                if (adj.SizeMult > 0m && adj.SizeMult < 1m)
                    riskBudget1R *= adj.SizeMult;

                // Hard USD cap — journal BR/CVC/CAP showed $300–800 single SL
                decimal hardCapUsd = major
                    ? Math.Min(150m, Math.Max(40m, available * 0.012m))
                    : Math.Min(55m, Math.Max(18m, available * 0.0075m));
                // Optional config override: Trading:HardCapUsdAlt / HardCapUsdMajor
                try
                {
                    var cfgMajor = _cfg.GetValue<decimal?>("Trading:HardCapUsdMajor");
                    var cfgAlt = _cfg.GetValue<decimal?>("Trading:HardCapUsdAlt");
                    if (major && cfgMajor.HasValue && cfgMajor.Value > 0) hardCapUsd = cfgMajor.Value;
                    if (!major && cfgAlt.HasValue && cfgAlt.Value > 0) hardCapUsd = cfgAlt.Value;
                }
                catch { }

                if (riskBudget1R > hardCapUsd)
                    riskBudget1R = hardCapUsd;

                decimal qty = riskBudget1R / slDist;

                // Notional / margin ceiling (same spirit as RiskManager)
                decimal marginFrac = major ? 0.12m : 0.10m;
                decimal maxNotional = available * marginFrac * lev;
                decimal notional = qty * price;
                if (notional > maxNotional && price > 0)
                {
                    qty = maxNotional / price;
                    notional = qty * price;
                }
                // Floor min notional ~5 USDT for demo realism
                if (notional < 5m && price > 0)
                {
                    qty = 5m / price;
                    notional = 5m;
                    // If min notional would risk >2.5× budget, skip
                    if (qty * slDist > riskBudget1R * 2.5m)
                    {
                        _log.LogDebug("[DEMO-AUTO] {user} skip {sym}: minNotional exceeds risk budget", client.Id, sym);
                        continue;
                    }
                }

                _log.LogInformation(
                    "[DEMO-AUTO] 1R-SIZE {user} {sym} avail={a:F0} budget={b:F2} hardCap={c:F2} slDist={sd} qty={q} notional={n:F2}",
                    client.Id, sym, available, riskBudget1R, hardCapUsd, slDist, qty, notional);

                List<DemoTpLevel>? tps = null;
                if (sig.TakeProfits != null && sig.TakeProfits.Count > 0)
                {
                    tps = sig.TakeProfits.Select((p, i) => new DemoTpLevel
                    {
                        Price = p,
                        Pct = i == 0 ? 50m : (i == 1 ? 30m : 20m)
                    }).ToList();
                }

                decimal? slUse = sig.StopLoss > 0 ? sig.StopLoss : null;
                if (slUse.HasValue && adj.SlPadAtr > 0)
                {
                    decimal riskSl = Math.Abs(price - slUse.Value);
                    decimal pad = riskSl * (adj.SlPadAtr / 1.5m);
                    bool lng = side.Equals("LONG", StringComparison.OrdinalIgnoreCase);
                    slUse = lng ? slUse.Value - pad : slUse.Value + pad;
                }
                if (tps != null && adj.TpScale > 0m && adj.TpScale < 0.999m)
                {
                    foreach (var t in tps)
                        t.Price = price + (t.Price - price) * adj.TpScale;
                }
                var (ok, err) = _demo.OpenMarketPositionForClient(
                    client.Id, sym, side, qty, lev, price,
                    slUse, tps);

                if (ok)
                {
                    SaveSeenKeys();
                    _log.LogInformation("[DEMO-AUTO] {user} {side} {sym} @ {px} lev={lev}",
                        client.Id, side, sym, price, lev);
                }
                else
                {
                    _log.LogDebug("[DEMO-AUTO] {user} skip {sym}: {err}", client.Id, sym, err);
                }
            }
        }

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
            var json = File.ReadAllText(_seenFilePath);
            var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
            if (list == null) return;
            foreach (var k in list.TakeLast(500))
                _seen.TryAdd(k, 0);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[DEMO-AUTO] load seen keys failed");
        }
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
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[DEMO-AUTO] save seen keys failed");
        }
    }

}

