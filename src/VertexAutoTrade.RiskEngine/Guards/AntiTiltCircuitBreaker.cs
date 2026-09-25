using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VertexAutoTrade.RiskEngine.Abstractions;
using VertexAutoTrade.RiskEngine.Options;

namespace VertexAutoTrade.RiskEngine.Guards;

/// <summary>
/// Consecutive strategy-fail cool-off + rolling avg-R risk scaler.
/// Never widens SL; only reduces risk fraction or blocks entries temporarily.
/// </summary>
public sealed class AntiTiltCircuitBreaker : IAntiTiltCircuitBreaker
{
    private readonly AntiTiltOptions _opt;
    private readonly ILogger<AntiTiltCircuitBreaker>? _log;
    private readonly object _gate = new();
    private readonly Queue<decimal> _recentR = new();

    private int _consecutiveFails;
    private DateTime? _coolOffUntilUtc;
    private decimal _riskMult = 1m;
    private decimal _rollingAvgR;

    public AntiTiltCircuitBreaker(IOptions<AntiTiltOptions> options, ILogger<AntiTiltCircuitBreaker>? log = null)
        : this(options.Value, log) { }

    public AntiTiltCircuitBreaker(AntiTiltOptions options, ILogger<AntiTiltCircuitBreaker>? log = null)
    {
        _opt = options ?? new AntiTiltOptions();
        _log = log;
    }

    public decimal CurrentRiskMultiplier
    {
        get { lock (_gate) return _riskMult; }
    }

    public decimal RollingAvgR
    {
        get { lock (_gate) return _rollingAvgR; }
    }

    public int ConsecutiveStrategyFails
    {
        get { lock (_gate) return _consecutiveFails; }
    }

    public bool IsInCoolOff(DateTime utcNow, out DateTime? untilUtc)
    {
        lock (_gate)
        {
            utcNow = utcNow.ToUniversalTime();
            if (_coolOffUntilUtc is { } u && utcNow < u)
            {
                untilUtc = u;
                return true;
            }

            if (_coolOffUntilUtc is not null && utcNow >= _coolOffUntilUtc)
                _coolOffUntilUtc = null;

            untilUtc = null;
            return false;
        }
    }

    public decimal OnTradeClosed(ClosedTradeRiskEvent evt)
    {
        lock (_gate)
        {
            var reason = (evt.CloseReason ?? "").ToUpperInvariant();
            bool isStrategyFail = IsStrategyFail(reason);

            if (isStrategyFail && evt.RealizedR < 0)
            {
                _consecutiveFails++;
                if (_consecutiveFails >= _opt.ConsecutiveStrategyFailsToCoolOff)
                {
                    _coolOffUntilUtc = evt.ClosedAtUtc.ToUniversalTime()
                        .AddHours(Math.Max(1, _opt.CoolOffHours));
                    _log?.LogWarning(
                        "[ANTI-TILT] Cool-off until {until:o} after {n} consecutive strategy fails ({sym})",
                        _coolOffUntilUtc, _consecutiveFails, evt.Symbol);
                }
            }
            else if (evt.RealizedR > 0)
            {
                _consecutiveFails = 0;
            }

            _recentR.Enqueue(evt.RealizedR);
            while (_recentR.Count > Math.Max(5, _opt.RollingWindowTrades))
                _recentR.Dequeue();

            _rollingAvgR = _recentR.Count == 0 ? 0m : _recentR.Average();
            _riskMult = _rollingAvgR < _opt.AvgRRiskCutThreshold && _recentR.Count >= Math.Min(10, _opt.RollingWindowTrades)
                ? _opt.WeakAvgRRiskMultiplier
                : 1m;

            if (_riskMult < 1m)
                _log?.LogInformation(
                    "[ANTI-TILT] Rolling avgR={avg:F2} < {th:F2} → risk×{m:F2}",
                    _rollingAvgR, _opt.AvgRRiskCutThreshold, _riskMult);

            return _riskMult;
        }
    }

    public EntryPermission EvaluateEntry(DateTime utcNow, decimal baseRiskFraction)
    {
        if (IsInCoolOff(utcNow, out var until))
        {
            return new EntryPermission(
                false,
                RiskBlockReason.CoolOffActive,
                $"Anti-tilt cool-off until {until:o}",
                0m,
                until);
        }

        decimal mult;
        lock (_gate) mult = _riskMult;
        var effective = Math.Max(0m, baseRiskFraction * mult);
        return new EntryPermission(
            true,
            RiskBlockReason.None,
            mult < 1m ? $"OK risk×{mult:F2} (weak avgR)" : "OK",
            effective);
    }

    private bool IsStrategyFail(string reasonUpper)
    {
        foreach (var token in _opt.StrategyFailReasonTokens)
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            if (reasonUpper.Contains(token.ToUpperInvariant(), StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
