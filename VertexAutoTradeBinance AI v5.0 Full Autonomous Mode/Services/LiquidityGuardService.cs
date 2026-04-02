using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services;

public enum LiquidityGuardReason
{
    None,
    StopHuntDown,
    StopHuntUp,
    LowVolume
}

public sealed record LiquidityGuardResult(
    bool Block,
    LiquidityGuardReason Reason,
    bool IsExtreme,
    string? Details = null,
    DateTime UtcTime = default,
    decimal Score = 1.0m,
    bool SoftWarning = false
);

public sealed class LiquidityGuardService
{
    private static readonly TimeSpan ExtremeTtl = TimeSpan.FromMinutes(2);

    public LiquidityGuardResult? LastDanger { get; private set; }

    private readonly ILogger<LiquidityGuardService> _logger;

    public LiquidityGuardService(ILogger<LiquidityGuardService> logger)
    {
        _logger = logger;
    }

    public bool IsExtreme()
    {
        var d = LastDanger;
        if (d == null || !d.IsExtreme || d.UtcTime == default) return false;
        return (DateTime.UtcNow - d.UtcTime) <= ExtremeTtl;
    }

    public LiquidityGuardResult Analyze(
        string symbol,
        KlineInterval interval,
        IReadOnlyList<BinanceFuturesUsdtKline> klines,
        SignalSide side,
        bool superSignal = false)
    {
        if (klines == null || klines.Count < 30)
            return SetDanger(false, LiquidityGuardReason.None, false, "insufficient klines");

        bool isMajor = symbol is "BTCUSDT" or "ETHUSDT";
        var last = klines[^1];
        var window = klines.Skip(Math.Max(0, klines.Count - 20)).ToList();

        decimal avgVolume = window.Average(k => k.Volume);
        decimal avgBody = window.Average(k => Math.Abs(k.ClosePrice - k.OpenPrice));
        if (avgBody <= 0m) avgBody = 0.00000001m;

        decimal upperWick = last.HighPrice - Math.Max(last.OpenPrice, last.ClosePrice);
        decimal lowerWick = Math.Min(last.OpenPrice, last.ClosePrice) - last.LowPrice;

        decimal volRatio = avgVolume <= 0m ? 1m : last.Volume / avgVolume;

        // ------------------------ SUPER SIGNAL ------------------------
        if (superSignal)
            return SetDanger(false, LiquidityGuardReason.None, false, "super-signal override");

        // ------------------------ STOP-HUNT ------------------------
        var stopHuntResult = CheckStopHunt(last, upperWick, lowerWick, avgBody, side, volRatio);
        if (stopHuntResult != null) return stopHuntResult;

        // ------------------------ LOW VOLUME / EARLY EXPANSION ------------------------
        var lowVolResult = CheckLowVolume(symbol, isMajor, last, avgVolume, avgBody, volRatio, interval);
        if (lowVolResult != null) return lowVolResult;

        // ------------------------ VOLUME EXHAUSTION ------------------------
        var volExhaustionResult = CheckVolumeExhaustion(symbol, isMajor, volRatio);
        if (volExhaustionResult != null) return volExhaustionResult;

        // ------------------------ OK ------------------------
        return SetDanger(false, LiquidityGuardReason.None, false, null);
    }

    // ================= PRIVATE HELPERS =================

    private LiquidityGuardResult? CheckStopHunt(
        BinanceFuturesUsdtKline last,
        decimal upperWick,
        decimal lowerWick,
        decimal avgBody,
        SignalSide side,
        decimal volRatio)
    {
        bool hugeLowerWick = lowerWick >= avgBody * 2 && volRatio >= 1.68m;
        bool hugeUpperWick = upperWick >= avgBody * 2 && volRatio >= 1.68m;

        if (hugeLowerWick && last.ClosePrice > last.OpenPrice && side == SignalSide.Sell)
            return SetDanger(true, LiquidityGuardReason.StopHuntDown, true, "STOP-HUNT DOWN");

        if (hugeUpperWick && last.ClosePrice < last.OpenPrice && side == SignalSide.Buy)
            return SetDanger(true, LiquidityGuardReason.StopHuntUp, true, "STOP-HUNT UP");

        return null;
    }

    private LiquidityGuardResult? CheckLowVolume(
        string symbol,
        bool isMajor,
        BinanceFuturesUsdtKline last,
        decimal avgVolume,
        decimal avgBody,
        decimal volRatio,
        KlineInterval interval)
    {
        bool softLowVolume = volRatio < 0.35m;
        bool extremeLowVolume = volRatio < 0.18m;

        if (isMajor) softLowVolume = extremeLowVolume = false;

        // Soft-warning → разрешаем быстрый вход/выход
        if (softLowVolume)
        {
            decimal score = Math.Clamp((volRatio - 0.10m) / (0.35m - 0.10m), 0.15m, 0.80m);
            return SetDanger(false, LiquidityGuardReason.LowVolume, false,
                $"SOFT LOW VOLUME {symbol} {interval}", score, true);
        }

        // Extreme low volume → HARD block
        if (extremeLowVolume && !isMajor)
            return SetDanger(true, LiquidityGuardReason.LowVolume, true,
                $"EXTREME LOW VOLUME {symbol}", 0.10m, false);

        return null;
    }

    private LiquidityGuardResult? CheckVolumeExhaustion(string symbol, bool isMajor, decimal volRatio)
    {
        if (volRatio >= 2.30m)
            return SetDanger(false, LiquidityGuardReason.None, false,
                $"VOLUME EXHAUSTION {symbol}", isMajor ? 0.70m : 0.55m, true);

        return null;
    }

    // ================= INTERNAL STATE SETTER =================

    private LiquidityGuardResult SetDanger(
        bool block,
        LiquidityGuardReason reason,
        bool isExtreme,
        string? details,
        decimal score = 1.0m,
        bool softWarning = false)
    {
        var r = new LiquidityGuardResult(block, reason, isExtreme, details, DateTime.UtcNow, score, softWarning);
        LastDanger = r;
        if (softWarning || block)
            _logger.LogWarning("[LiquidityGuard] {Details} | Score={Score:F2}", details ?? "-", score);
        return r;
    }

    public bool IsDangerRecent(TimeSpan ttl)
    {
        var d = LastDanger;
        if (d == null || d.UtcTime == default) return false;
        return (DateTime.UtcNow - d.UtcTime) <= ttl;
    }
}