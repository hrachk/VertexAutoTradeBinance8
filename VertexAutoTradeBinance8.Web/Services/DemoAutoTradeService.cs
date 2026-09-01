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

            try { await Task.Delay(TimeSpan.FromSeconds(12), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private const int MaxDemoPositions = 5; // aligned with LIVE max open

    private async Task TickAsync(CancellationToken ct)
    {
        var clients = await _db.GetClientsWithParallelDemoAsync();
        if (clients.Count == 0) return;

        var signals = await _signals.LoadAsync();
        if (signals == null || signals.Count == 0) return;

        // CRITICAL (SKR 0.01399 case): old code used 45 min window + conf-first sort,
        // so Demo could open on a signal generated long ago while price already moved.
        // Each JSON row has its own Time — we only trade the NEWEST CORE signal per
        // symbol+side, and only if it is still fresh (few minutes, not 45).
        const int maxSignalAgeMinutes = 15;
        var cutoff = DateTime.UtcNow.AddMinutes(-maxSignalAgeMinutes);
        var notBefore = _startedUtc.AddMinutes(-1);

        var candidates = signals
            .Where(s => s.Time >= cutoff
                        && s.Time >= notBefore
                        && s.Entry > 0
                        && !string.IsNullOrWhiteSpace(s.Symbol)
                        && s.Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
                        && (string.IsNullOrEmpty(s.Reason) || s.Reason.StartsWith("CORE_", StringComparison.OrdinalIgnoreCase))
                        && s.Confidence >= 55)
            // Newest first — never prefer an older row with same conf
            .OrderByDescending(s => s.Time)
            .ThenByDescending(s => s.Confidence)
            // One signal per symbol+side: the freshest only
            .GroupBy(s => (
                Symbol: (s.Symbol ?? "").Trim().ToUpperInvariant(),
                Side: (s.Side ?? "").Contains("Sell", StringComparison.OrdinalIgnoreCase) ? "SHORT" : "LONG"))
            .Select(g => g.First())
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
                    if (openN >= MaxDemoPositions)
                    {
                        _log.LogDebug("[DEMO-AUTO] {user} at max positions ({n})", client.Id, openN);
                        continue;
                    }
                    if (_demo.HasOpenSymbolForClient(client.Id, sym))
                    {
                        continue; // already in this symbol
                    }
                }
                catch { /* non-fatal */ }

                // MARKET FILL (like Live): never open at stale signal.Entry alone.
                // Bug: SKR LONG opened @ 0.01399 while market ~0.01332 → instant SL.
                decimal signalEntry = sig.Entry;
                decimal marketPx = 0m;
                try { marketPx = _demo.GetLastPrice(sym); } catch { /* ignore */ }

                decimal price;
                if (marketPx > 0)
                {
                    // Max deviation from signal entry (stale / wrong tick)
                    const decimal maxDev = 0.015m; // 1.5% — still blocks SKR-class 5% gaps
                    decimal dev = signalEntry > 0
                        ? Math.Abs(marketPx - signalEntry) / signalEntry
                        : 0m;
                    if (dev > maxDev)
                    {
                        _log.LogWarning(
                            "[DEMO-AUTO] skip {sym} {side}: market {m} vs signal entry {e} dev={d:P2} > {max:P2} (stale/wrong entry)",
                            sym, side, marketPx, signalEntry, dev, maxDev);
                        continue;
                    }
                    price = marketPx;
                }
                else
                {
                    price = signalEntry;
                }

                // Majors: BTC/ETH/BNB/SOL — 10% of Available as pure margin; others 8%.
                // Notional = margin × leverage (pleчо учитывается).
                bool major = sym is "BTCUSDT" or "ETHUSDT" or "BNBUSDT" or "SOLUSDT";
                int lev = major ? 10 : 5;

                decimal available = 10_000m;
                try
                {
                    available = Math.Max(50m, _demo.GetAvailableForClient(client.Id));
                }
                catch { /* keep default */ }

                decimal marginFrac = major ? 0.10m : 0.08m;
                decimal margin = available * marginFrac;
                decimal notional = margin * lev;
                // Cap: never use more than 95% of available as margin
                if (margin > available * 0.95m)
                {
                    margin = available * 0.95m;
                    notional = margin * lev;
                }
                var adj = _journal?.GetAdjustments(client.Id, sym) ?? new VertexAutoTradeBinance8.Services.Learning.SymbolAdjustments();
                notional *= adj.SizeMult;
                lev = Math.Max(1, (int)Math.Round(lev * adj.LevMult));
                decimal qty = notional / Math.Max(price, 0.0000001m);

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
                // Instant-SL guard: LONG with SL >= fill or SHORT with SL <= fill → skip
                if (slUse.HasValue && slUse.Value > 0)
                {
                    bool instantSl = side == "LONG"
                        ? slUse.Value >= price
                        : slUse.Value <= price;
                    if (instantSl)
                    {
                        _log.LogWarning(
                            "[DEMO-AUTO] skip {sym} {side}: SL {sl} would trigger immediately at fill {px}",
                            sym, side, slUse.Value, price);
                        continue;
                    }
                }

                var (ok, err) = _demo.OpenMarketPositionForClient(
                    client.Id, sym, side, qty, lev, price,
                    slUse, tps);

                if (ok)
                {
                    SaveSeenKeys();
                                        try { _demo.SetPositionSetupForClient(client.Id, sym, sig.Reason ?? ""); } catch { }
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

