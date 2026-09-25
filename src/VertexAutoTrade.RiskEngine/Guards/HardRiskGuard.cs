using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VertexAutoTrade.RiskEngine.Abstractions;
using VertexAutoTrade.RiskEngine.Options;

namespace VertexAutoTrade.RiskEngine.Guards;

/// <summary>
/// Hard risk circuit: daily loss R / equity, max positions, timed trading block.
/// Thread-safe. Evaluated before any new entry.
/// Does NOT widen stops — only blocks or allows.
/// </summary>
public sealed class HardRiskGuard : IHardRiskGuard
{
    private readonly HardRiskOptions _opt;
    private readonly ILogger<HardRiskGuard>? _log;
    private readonly object _gate = new();

    private DateTime? _blockedUntilUtc;
    private string? _blockDetail;
    private RiskBlockReason _blockReason;
    private decimal _realizedRToday;
    private DateTime _rDayUtc;

    public HardRiskGuard(IOptions<HardRiskOptions> options, ILogger<HardRiskGuard>? log = null)
        : this(options.Value, log) { }

    public HardRiskGuard(HardRiskOptions options, ILogger<HardRiskGuard>? log = null)
    {
        _opt = options ?? new HardRiskOptions();
        _log = log;
        _rDayUtc = DateTime.UtcNow.Date;
    }

    public void RegisterClosedR(decimal realizedR, DateTime closedAtUtc)
    {
        lock (_gate)
        {
            EnsureDay(closedAtUtc.ToUniversalTime());
            _realizedRToday += realizedR;
        }
    }

    public void Trip(RiskBlockReason reason, DateTime utcNow, string detail)
    {
        lock (_gate)
        {
            var until = utcNow.ToUniversalTime().AddHours(Math.Max(1, _opt.BlockHoursAfterTrip));
            _blockedUntilUtc = until;
            _blockReason = reason;
            _blockDetail = detail;
            _log?.LogWarning("[HARD-RISK] TRIP {reason} until {until:o} | {detail}", reason, until, detail);
        }
    }

    public void ClearBlock()
    {
        lock (_gate)
        {
            _blockedUntilUtc = null;
            _blockDetail = null;
            _blockReason = RiskBlockReason.None;
        }
    }

    public bool IsTradingBlocked(DateTime utcNow, out DateTime? untilUtc, out string? detail)
    {
        lock (_gate)
        {
            utcNow = utcNow.ToUniversalTime();
            if (_blockedUntilUtc is { } u && utcNow < u)
            {
                untilUtc = u;
                detail = _blockDetail;
                return true;
            }

            if (_blockedUntilUtc is not null && utcNow >= _blockedUntilUtc)
            {
                _blockedUntilUtc = null;
                _blockDetail = null;
                _blockReason = RiskBlockReason.None;
            }

            untilUtc = null;
            detail = null;
            return false;
        }
    }

    public EntryPermission EvaluateEntry(RiskSnapshot snapshot)
    {
        var now = snapshot.UtcNow.ToUniversalTime();

        if (IsTradingBlocked(now, out var until, out var detail))
        {
            return new EntryPermission(
                false,
                RiskBlockReason.HardBlockActive,
                detail ?? "Trading blocked by hard risk trip",
                EffectiveRiskFraction: 0m,
                until);
        }

        if (snapshot.OpenPositionCount >= _opt.MaxOpenPositions)
        {
            return new EntryPermission(
                false,
                RiskBlockReason.MaxOpenPositions,
                $"Open positions {snapshot.OpenPositionCount} >= max {_opt.MaxOpenPositions}",
                0m);
        }

        decimal dayR;
        lock (_gate)
        {
            EnsureDay(now);
            // Prefer live snapshot R if provided; else internal accumulator
            dayR = snapshot.RealizedRToday != 0 ? snapshot.RealizedRToday : _realizedRToday;
            if (snapshot.RealizedRToday != 0)
                _realizedRToday = snapshot.RealizedRToday;
        }

        if (dayR <= -Math.Abs(_opt.MaxDailyLossR))
        {
            var msg = $"Daily loss {dayR:F2}R hit limit −{_opt.MaxDailyLossR:F2}R";
            Trip(RiskBlockReason.DailyLossR, now, msg);
            return new EntryPermission(false, RiskBlockReason.DailyLossR, msg, 0m,
                now.AddHours(_opt.BlockHoursAfterTrip));
        }

        if (_opt.MaxDailyLossEquityFraction > 0 && snapshot.EquityUsd > 0)
        {
            var lossFrac = snapshot.RealizedPnlTodayUsd < 0
                ? Math.Abs(snapshot.RealizedPnlTodayUsd) / snapshot.EquityUsd
                : 0m;
            if (lossFrac >= _opt.MaxDailyLossEquityFraction)
            {
                var msg = $"Daily equity loss {lossFrac:P2} >= {_opt.MaxDailyLossEquityFraction:P2}";
                Trip(RiskBlockReason.DailyLossEquity, now, msg);
                return new EntryPermission(false, RiskBlockReason.DailyLossEquity, msg, 0m,
                    now.AddHours(_opt.BlockHoursAfterTrip));
            }
        }

        return new EntryPermission(
            true,
            RiskBlockReason.None,
            "OK",
            _opt.BaseRiskFraction);
    }

    private void EnsureDay(DateTime utcNow)
    {
        var day = utcNow.Date;
        if (day != _rDayUtc)
        {
            _rDayUtc = day;
            _realizedRToday = 0m;
        }
    }
}
