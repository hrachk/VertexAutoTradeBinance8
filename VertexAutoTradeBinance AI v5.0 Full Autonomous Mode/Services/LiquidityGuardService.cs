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

    // NEW (optional): 0..1, где 1 = отлично, 0 = опасно
    decimal Score = 1.0m,

    // NEW: мягкое предупреждение (не блок)
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

    /// <summary>
    /// EXTREME = недавний HARD-блок по ликвидности (TTL-ограничен)
    /// Используется для запрета counter-trend override
    /// </summary>
    public bool IsExtreme()
    {
        var d = LastDanger;
        if (d == null) return false;

        if (!d.IsExtreme) return false;

        if (d.UtcTime == default) return false;

        return (DateTime.UtcNow - d.UtcTime) <= ExtremeTtl;
    }

    /// <summary>
    /// PRO-фильтр ликвидности:
    /// - smart low-volume (soft / extreme)
    /// - stop-hunt detection
    /// - BTC/ETH не блокируются по low-volume
    /// - SuperSignal override
    /// </summary>
    //public LiquidityGuardResult Analyze(
    //    string symbol,
    //    KlineInterval interval,
    //    IReadOnlyList<BinanceFuturesUsdtKline> klines,
    //    SignalSide side,
    //    bool superSignal = false)
    //{
    //    // ---------------------------------------------------------------------
    //    // SAFETY: insufficient data
    //    // ---------------------------------------------------------------------
    //    if (klines == null || klines.Count < 30)
    //    {
    //        return SetDanger(
    //            block: false,
    //            reason: LiquidityGuardReason.None,
    //            isExtreme: false,
    //            details: "insufficient klines");
    //    }

    //    bool isMajor = symbol is "BTCUSDT" or "ETHUSDT";

    //    var window = klines.Skip(Math.Max(0, klines.Count - 20)).ToList();

    //    decimal avgBody = window.Average(k => Math.Abs(k.ClosePrice - k.OpenPrice));
    //    decimal avgVolume = window.Average(k => k.Volume);

    //    if (avgBody <= 0m)
    //        avgBody = 0.00000001m; // safety clamp

    //    var last = klines[^1];

    //    decimal upperWick = last.HighPrice - Math.Max(last.OpenPrice, last.ClosePrice);
    //    decimal lowerWick = Math.Min(last.OpenPrice, last.ClosePrice) - last.LowPrice;

    //    bool volumeSpike = avgVolume > 0m && last.Volume > avgVolume * (isMajor ? 1.45m : 1.65m);

    //    decimal volRatio = avgVolume <= 0m ? 1m : last.Volume / avgVolume;

    //    bool extremeLowVolume = volRatio < 0.18m; // HARD
    //    bool softLowVolume = volRatio < 0.35m; // SOFT

    //    bool hugeLowerWick = lowerWick > avgBody * 2.0m && volumeSpike;
    //    bool hugeUpperWick = upperWick > avgBody * 2.0m && volumeSpike;

    //    // ---------------------------------------------------------------------
    //    // 1) SuperSignal override
    //    // ---------------------------------------------------------------------
    //    if (superSignal)
    //    {
    //        _logger.LogInformation(
    //            "[LiquidityGuard] SUPER-SIGNAL override → {Symbol} allowed",
    //            symbol);

    //        return SetDanger(
    //            block: false,
    //            reason: LiquidityGuardReason.None,
    //            isExtreme: false,
    //            details: "super-signal override");
    //    }

    //    // ---------------------------------------------------------------------
    //    // 2) BTC / ETH — ignore low-volume
    //    // ---------------------------------------------------------------------
    //    if (isMajor && softLowVolume)
    //    {
    //        _logger.LogInformation(
    //            "[LiquidityGuard] LOW-VOLUME IGNORED for MAJOR {Symbol} ratio={Ratio:F2}",
    //            symbol, volRatio);

    //        softLowVolume = false;
    //        extremeLowVolume = false;
    //    }

    //    // ---------------------------------------------------------------------
    //    // 3) LOW VOLUME
    //    // ---------------------------------------------------------------------
    //    if (softLowVolume)
    //    {
    //        var msg = $"LOW VOLUME {symbol} {interval} | ratio={volRatio:F2}";

    //        // EXTREME: как и было — блок (это реально опасно для исполнения на альтах)
    //        if (extremeLowVolume && !isMajor)
    //        {
    //            _logger.LogWarning(
    //                "[LiquidityGuard] EXTREME LOW-VOLUME BLOCK {Symbol} ratio={Ratio:F2}",
    //                symbol, volRatio);

    //            return SetDanger(
    //                block: true,
    //                reason: LiquidityGuardReason.LowVolume,
    //                isExtreme: true,
    //                details: msg,
    //                score: 0.10m,
    //                softWarning: false);
    //        }

    //        // SOFT: НЕ блокируем, но снижаем score и помечаем warning
    //        _logger.LogWarning(
    //            "[LiquidityGuard] SOFT LOW-VOLUME {Symbol} ratio={Ratio:F2}",
    //            symbol, volRatio);

    //        // score шкалируем от ratio (0.35 -> ~0.65, 0.18 -> ~0.2)
    //        var score = Math.Clamp((volRatio - 0.10m) / (0.35m - 0.10m), 0.15m, 0.80m);

    //        return SetDanger(
    //            block: false,
    //            reason: LiquidityGuardReason.LowVolume,
    //            isExtreme: false,
    //            details: msg,
    //            score: score,
    //            softWarning: true);
    //    }


    //    // ---------------------------------------------------------------------
    //    // 4) STOP-HUNT DOWN → block SHORT
    //    // ---------------------------------------------------------------------
    //    if (hugeLowerWick && last.ClosePrice > last.OpenPrice && side == SignalSide.Sell)
    //    {
    //        var msg = $"STOP-HUNT DOWN {symbol} {interval}";

    //        _logger.LogWarning("[LiquidityGuard] BLOCKED SHORT: {Msg}", msg);

    //        return SetDanger(
    //            block: true,
    //            reason: LiquidityGuardReason.StopHuntDown,
    //            isExtreme: true,
    //            details: msg);
    //    }

    //    // ---------------------------------------------------------------------
    //    // 5) STOP-HUNT UP → block LONG
    //    // ---------------------------------------------------------------------
    //    if (hugeUpperWick && last.ClosePrice < last.OpenPrice && side == SignalSide.Buy)
    //    {
    //        var msg = $"STOP-HUNT UP {symbol} {interval}";

    //        _logger.LogWarning("[LiquidityGuard] BLOCKED LONG: {Msg}", msg);

    //        return SetDanger(
    //            block: true,
    //            reason: LiquidityGuardReason.StopHuntUp,
    //            isExtreme: true,
    //            details: msg);
    //    }

    //    // ---------------------------------------------------------------------
    //    // OK
    //    // ---------------------------------------------------------------------
    //    return SetDanger(
    //        block: false,
    //        reason: LiquidityGuardReason.None,
    //        isExtreme: false,
    //        details: null);
    //}

    // =====================================================================
    // INTERNAL STATE SETTER (single point of truth)
    // =====================================================================

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

        var window = klines.Skip(Math.Max(0, klines.Count - 20)).ToList();

        decimal avgBody = window.Average(k => Math.Abs(k.ClosePrice - k.OpenPrice));
        decimal avgVolume = window.Average(k => k.Volume);
        if (avgBody <= 0m) avgBody = 0.00000001m;

        var last = klines[^1];

        decimal upperWick = last.HighPrice - Math.Max(last.OpenPrice, last.ClosePrice);
        decimal lowerWick = Math.Min(last.OpenPrice, last.ClosePrice) - last.LowPrice;

        decimal volRatio = avgVolume <= 0m ? 1m : last.Volume / avgVolume;

        // ================= VOLUME TIERS =================
        bool volumeSpikeSoft = volRatio >= 1.45m;   // EARLY
        bool volumeSpike = volRatio >= 1.68m;   // IMPULSE
        bool volumeSpikeHard = volRatio >= 2.30m;   // EXHAUSTION

        bool extremeLowVolume = volRatio < 0.18m;
        bool softLowVolume = volRatio < 0.35m;

        // BTC / ETH — не душим low-volume
        if (isMajor)
        {
            softLowVolume = false;
            extremeLowVolume = false;
        }

        bool hugeLowerWick = lowerWick >= avgBody * 2.0m && volumeSpike;
        bool hugeUpperWick = upperWick >= avgBody * 2.0m && volumeSpike;

        // =================================================
        // 1) SUPER SIGNAL
        // =================================================
        if (superSignal)
            return SetDanger(false, LiquidityGuardReason.None, false, "super-signal override");

        // =================================================
        // 2) STOP-HUNT (HARD BLOCK)
        // =================================================
        if (hugeLowerWick && last.ClosePrice > last.OpenPrice && side == SignalSide.Sell)
            return SetDanger(true, LiquidityGuardReason.StopHuntDown, true, "STOP-HUNT DOWN");

        if (hugeUpperWick && last.ClosePrice < last.OpenPrice && side == SignalSide.Buy)
            return SetDanger(true, LiquidityGuardReason.StopHuntUp, true, "STOP-HUNT UP");

        // =================================================
        // 3) EARLY EXPANSION (KEY BLOCK)
        // =================================================
        if (softLowVolume && volumeSpikeSoft)
        {
            // Начало движения, разрешаем вход
            return SetDanger(
                block: false,
                reason: LiquidityGuardReason.None,
                isExtreme: false,
                details: $"EARLY_EXPANSION {symbol} {interval}",
                score: isMajor ? 0.85m : 0.75m,
                softWarning: true);
        }

        // =================================================
        // 4) LOW VOLUME
        // =================================================
        if (softLowVolume)
        {
            if (extremeLowVolume && !isMajor)
                return SetDanger(true, LiquidityGuardReason.LowVolume, true,
                    $"EXTREME LOW VOLUME {symbol}", 0.10m, false);

            var score = Math.Clamp(
                (volRatio - 0.10m) / (0.35m - 0.10m),
                0.15m,
                0.80m);

            return SetDanger(false, LiquidityGuardReason.LowVolume, false,
                $"SOFT LOW VOLUME {symbol}", score, true);
        }

        // =================================================
        // 5) VOLUME EXHAUSTION (WARNING)
        // =================================================
        if (volumeSpikeHard)
        {
            return SetDanger(
                false,
                LiquidityGuardReason.None,
                false,
                $"VOLUME EXHAUSTION {symbol}",
                isMajor ? 0.70m : 0.55m,
                true);
        }

        // =================================================
        // OK
        // =================================================
        return SetDanger(false, LiquidityGuardReason.None, false, null);
    }

    private LiquidityGuardResult SetDanger(
     bool block,
     LiquidityGuardReason reason,
     bool isExtreme,
     string? details,
     decimal score = 1.0m,
     bool softWarning = false)
    {
        var r = new LiquidityGuardResult(
     block,
     reason,
     isExtreme,
     details,
     DateTime.UtcNow,
     score,
     softWarning);

        LastDanger = r;
        return r;
    }

    public bool IsDangerRecent(TimeSpan ttl)
    {
        var d = LastDanger;
        if (d == null) return false;
        if (d.UtcTime == default) return false;

        return (DateTime.UtcNow - d.UtcTime) <= ttl;
    }
}
