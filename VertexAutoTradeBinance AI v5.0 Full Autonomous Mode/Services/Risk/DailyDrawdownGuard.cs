using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services.Risk;

/// <summary>
/// Daily loss circuit breaker: equity drop ≥ threshold OR N consecutive SL → EMERGENCY_STOP until 00:00 UTC.
/// </summary>
public sealed class DailyDrawdownGuard
{
    private readonly ILogger<DailyDrawdownGuard> _log;
    private readonly IConfiguration _cfg;
    private readonly object _gate = new();
    private DateTime _dayUtc = DateTime.UtcNow.Date;
    private decimal _dayStartEquity;
    private decimal _dayRealizedPnl;
    private int _consecutiveStops;
    private bool _emergency;

    public DailyDrawdownGuard(ILogger<DailyDrawdownGuard> log, IConfiguration cfg)
    {
        _log = log;
        _cfg = cfg;
    }

    public bool IsEmergencyStop =>
        _cfg.GetValue("Risk:DailyDdEnabled", true) && Snapshot().emergency;

    public (bool emergency, decimal dayPnl, int stops) Snapshot()
    {
        lock (_gate)
        {
            RollDay();
            return (_emergency, _dayRealizedPnl, _consecutiveStops);
        }
    }

    private void RollDay()
    {
        var today = DateTime.UtcNow.Date;
        if (today != _dayUtc)
        {
            _dayUtc = today;
            _dayRealizedPnl = 0;
            _consecutiveStops = 0;
            _emergency = false;
            _dayStartEquity = 0;
            _log.LogInformation("[DAILY-DD] new UTC day — emergency cleared");
        }
    }

    public void SetDayStartEquity(decimal equity)
    {
        lock (_gate)
        {
            RollDay();
            if (_dayStartEquity <= 0 && equity > 0)
                _dayStartEquity = equity;
        }
    }

    public void OnTradeClosed(decimal realizedPnl, bool isStopLoss)
    {
        if (!_cfg.GetValue("Risk:DailyDdEnabled", true)) return;
        decimal maxDdPct = _cfg.GetValue("Risk:DailyLossLimitPct", 3.0m);
        int maxStops = _cfg.GetValue("Risk:MaxConsecutiveStops", 3);

        lock (_gate)
        {
            RollDay();
            _dayRealizedPnl += realizedPnl;
            if (isStopLoss) _consecutiveStops++;
            else _consecutiveStops = 0;

            bool byPnl = _dayStartEquity > 0 &&
                         (_dayRealizedPnl / _dayStartEquity) * 100m <= -maxDdPct;
            bool byStops = _consecutiveStops >= maxStops;

            if (!_emergency && (byPnl || byStops))
            {
                _emergency = true;
                _log.LogError(
                    "[DAILY-DD] EMERGENCY_STOP dayPnl={pnl:F2} startEq={eq:F2} stopsInRow={s} until 00:00 UTC",
                    _dayRealizedPnl, _dayStartEquity, _consecutiveStops);
            }
        }
    }

    public bool ShouldBlockNewEntries(out string reason)
    {
        reason = "";
        if (!_cfg.GetValue("Risk:DailyDdEnabled", true)) return false;
        var (em, pnl, stops) = Snapshot();
        if (!em) return false;
        reason = $"EMERGENCY_STOP dayPnl={pnl:F2} stops={stops} until 00:00 UTC";
        return true;
    }
}
