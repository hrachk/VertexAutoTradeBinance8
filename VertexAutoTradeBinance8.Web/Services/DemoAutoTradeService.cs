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
    private readonly ConcurrentDictionary<string, byte> _seen = new();

    public DemoAutoTradeService(
        LiveSignalFileService signals,
        DemoAccountService demo,
        ClientDbService db,
        ILogger<DemoAutoTradeService> log)
    {
        _signals = signals;
        _demo = demo;
        _db = db;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("[DEMO-AUTO] Parallel demo worker started (ignores AutoTrade ON/OFF)");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogWarning(ex, "[DEMO-AUTO] tick failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(12), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private static readonly HashSet<string> DemoAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "BTCUSDT", "ETHUSDT", "BNBUSDT", "SOLUSDT", "XRPUSDT",
        "DOGEUSDT", "ADAUSDT", "AVAXUSDT", "LINKUSDT", "DOTUSDT",
        "LTCUSDT", "BCHUSDT", "NEARUSDT", "ATOMUSDT", "UNIUSDT",
        "AAVEUSDT", "OPUSDT", "ARBUSDT", "SUIUSDT", "TIAUSDT",
        "INJUSDT", "APTUSDT", "FILUSDT", "RENDERUSDT", "WLDUSDT",
        "TONUSDT", "TRXUSDT", "XMRUSDT", "ETCUSDT", "SEIUSDT"
    };

    private const int MaxDemoPositions = 3;

    private async Task TickAsync(CancellationToken ct)
    {
        var clients = await _db.GetClientsWithParallelDemoAsync();
        if (clients.Count == 0) return;

        var signals = await _signals.LoadAsync();
        if (signals == null || signals.Count == 0) return;

        var cutoff = DateTime.UtcNow.AddMinutes(-45);
        // Prefer CORE_ signals, liquid symbols only, highest confidence first
        var candidates = signals
            .Where(s => s.Time >= cutoff
                        && s.Entry > 0
                        && !string.IsNullOrWhiteSpace(s.Symbol)
                        && DemoAllowlist.Contains(s.Symbol.Trim().ToUpperInvariant())
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

                // Cap concurrent demo positions — quality over spam
                try
                {
                    // Peek bound state if same client; otherwise open will still enforce balance
                    var snap = _demo.BoundClientId == client.Id ? _demo.GetSnapshot() : null;
                    if (snap != null && snap.Positions.Count >= MaxDemoPositions)
                    {
                        _log.LogDebug("[DEMO-AUTO] {user} at max positions ({n})", client.Id, snap.Positions.Count);
                        continue;
                    }
                    if (snap != null && snap.Positions.Any(p =>
                            string.Equals(p.Symbol, sym, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue; // already in this symbol
                    }
                }
                catch { /* non-fatal */ }

                decimal price = sig.Entry;
                int lev = sym is "BTCUSDT" or "ETHUSDT" ? 10 : 5;

                // Size from REAL demo wallet (~$10k), not a fixed $40 toy notional.
                // Target ~2.5% of equity as position notional (feels like a real small account risk).
                decimal equity = 10_000m;
                try
                {
                    if (_demo.BoundClientId == client.Id)
                        equity = Math.Max(100m, _demo.GetEquity());
                }
                catch { /* keep default */ }

                decimal riskFrac = sym is "BTCUSDT" or "ETHUSDT" ? 0.03m : 0.02m; // 2–3% of equity
                decimal notional = Math.Clamp(equity * riskFrac, 50m, equity * 0.12m);
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

                var (ok, err) = _demo.OpenMarketPositionForClient(
                    client.Id, sym, side, qty, lev, price,
                    sig.StopLoss > 0 ? sig.StopLoss : null, tps);

                if (ok)
                    _log.LogInformation("[DEMO-AUTO] {user} {side} {sym} @ {px} lev={lev}",
                        client.Id, side, sym, price, lev);
                else
                    _log.LogDebug("[DEMO-AUTO] {user} skip {sym}: {err}", client.Id, sym, err);
            }
        }

        if (_seen.Count > 5000)
        {
            foreach (var k in _seen.Keys.Take(2000))
                _seen.TryRemove(k, out _);
        }
    }
}
