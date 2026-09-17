using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services.Risk;

/// <summary>
/// Circuit breaker: if BTC 5m absolute move > threshold (default 1.2%),
/// lock alt auto-entries for LockMinutes (default 15).
/// </summary>
public sealed class BtcVolatilityFilterService : BackgroundService
{
    private readonly ILogger<BtcVolatilityFilterService> _log;
    private readonly IConfiguration _cfg;
    private readonly object _gate = new();
    private DateTime _lockUntilUtc = DateTime.MinValue;
    private decimal _lastImpulse;

    // Optional price provider set by MarketData when available
    public Func<decimal>? GetBtcPrice { get; set; }

    private readonly Queue<(DateTime t, decimal px)> _ring = new();

    public BtcVolatilityFilterService(ILogger<BtcVolatilityFilterService> log, IConfiguration cfg)
    {
        _log = log;
        _cfg = cfg;
    }

    public void ObserveBtcPrice(decimal price)
    {
        if (price <= 0) return;
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            _ring.Enqueue((now, price));
            while (_ring.Count > 0 && (now - _ring.Peek().t).TotalMinutes > 6)
                _ring.Dequeue();
        }
        Evaluate();
    }

    private void Evaluate()
    {
        decimal threshold = _cfg.GetValue("Risk:BtcImpulse5mPct", 1.2m);
        int lockMin = _cfg.GetValue("Risk:BtcVolLockMinutes", 15);
        lock (_gate)
        {
            if (_ring.Count < 2) return;
            var newest = _ring.Last();
            var old = _ring.FirstOrDefault(x => (newest.t - x.t).TotalMinutes >= 4.5);
            if (old.px <= 0) old = _ring.Peek();
            if (old.px <= 0) return;
            decimal impulse = Math.Abs(newest.px - old.px) / old.px * 100m;
            _lastImpulse = impulse;
            if (impulse >= threshold)
            {
                var until = DateTime.UtcNow.AddMinutes(lockMin);
                if (until > _lockUntilUtc)
                {
                    _lockUntilUtc = until;
                    _log.LogWarning(
                        "[BTC-VOL] LOCK impulse5m={imp:F2}% >= {th:F2}% → alts blocked until {u:HH:mm:ss}Z",
                        impulse, threshold, until);
                    try
                    {
                        var root = _cfg["SharedData:Root"] ?? "";
                        if (!string.IsNullOrEmpty(root))
                            File.WriteAllText(Path.Combine(root, "btc_vol_lock.flag"),
                                $"until {until:o}\nimpulse={impulse:F2}%");
                    }
                    catch { }
                }
            }
        }
    }

    public bool IsAltEntryLocked(out string reason)
    {
        reason = "";
        if (!_cfg.GetValue("Risk:BtcVolFilterEnabled", true)) return false;
        lock (_gate)
        {
            if (DateTime.UtcNow < _lockUntilUtc)
            {
                reason = $"BTC_VOLATILITY_LOCK until {_lockUntilUtc:HH:mm:ss}Z impulse={_lastImpulse:F2}%";
                return true;
            }
            try
            {
                var root = _cfg["SharedData:Root"] ?? "";
                var f = Path.Combine(root, "btc_vol_lock.flag");
                if (File.Exists(f)) File.Delete(f);
            }
            catch { }
        }
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var px = GetBtcPrice?.Invoke() ?? 0m;
                if (px > 0) ObserveBtcPrice(px);
            }
            catch { }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }
}
