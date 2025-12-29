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

public record LiquidityGuardResult(
    bool Block,
    LiquidityGuardReason Reason,
    string? Details = null,
    DateTime UtcTime = default);

public class LiquidityGuardService
{
    public LiquidityGuardResult? LastDanger { get; private set; }
    private readonly ILogger<LiquidityGuardService> _logger;

    public LiquidityGuardService(ILogger<LiquidityGuardService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// PRO-фильтр ликвидности:
    /// - умный low-volume
    /// - smart stop-hunt detection
    /// - major coins (BTC/ETH) НЕ блокируются по low-volume
    /// - SuperSignal override
    /// </summary>
    public LiquidityGuardResult Analyze(
        string symbol,
        KlineInterval interval,
        IReadOnlyList<BinanceFuturesUsdtKline> klines,
        SignalSide side,
        bool superSignal = false)
    {
        // Всегда обновляем LastDanger, чтобы не таскать старый мусор
        if (klines == null || klines.Count < 30)
        {
            var r0 = new LiquidityGuardResult(false, LiquidityGuardReason.None, "insufficient klines", DateTime.UtcNow);
            LastDanger = r0;
            return r0;
        }

        bool isMajor = symbol is "BTCUSDT" or "ETHUSDT";

        var window = klines.Skip(Math.Max(0, klines.Count - 20)).ToList();

        decimal avgBody = window.Average(k => Math.Abs(k.ClosePrice - k.OpenPrice));
        decimal avgVolume = window.Average(k => k.Volume);

        // SAFETY: чтобы avgBody не был нулём (иначе стоп-хант начинает ловить мусор)
        if (avgBody <= 0m) avgBody = 0.00000001m;

        var last = klines[^1];

        decimal upperWick = last.HighPrice - Math.Max(last.OpenPrice, last.ClosePrice);
        decimal lowerWick = Math.Min(last.OpenPrice, last.ClosePrice) - last.LowPrice;

        bool volumeSpike = avgVolume > 0m && last.Volume > avgVolume * 2.0m;
        bool tinyVolume = avgVolume > 0m && last.Volume < avgVolume * 0.25m;

        // градация
        decimal volRatio = avgVolume <= 0 ? 1m : last.Volume / avgVolume;
        bool extremeLowVolume = volRatio < 0.18m; // HARD BLOCK
        bool softLowVolume = volRatio < 0.35m;    // SOFT DANGER

        bool hugeLowerWick = lowerWick > avgBody * 2.0m && volumeSpike;
        bool hugeUpperWick = upperWick > avgBody * 2.0m && volumeSpike;

        // 1) SuperSignal — всегда разрешён, но важно сбросить LastDanger
        if (superSignal)
        {
            var r = new LiquidityGuardResult(false, LiquidityGuardReason.None, "super-signal override", DateTime.UtcNow);
            LastDanger = r;
            _logger.LogInformation("[LiquidityGuard] SUPER-SIGNAL override → {Symbol} allowed", symbol);
            return r;
        }

        // 2) BTC/ETH — не рубим по low-volume
        if (isMajor && tinyVolume)
        {
            _logger.LogInformation(
                "[LiquidityGuard] LOW-VOLUME IGNORED for MAJOR {Symbol}. vol={Vol:F2}, avg={Avg:F2}",
                symbol, last.Volume, avgVolume);
            tinyVolume = false;
        }

        // 3) Low Volume
        if (softLowVolume)
        {
            var msg = $"LOW VOLUME {symbol} {interval} | ratio={volRatio:F2}";

            // экстремум — блок
            if (extremeLowVolume && !isMajor)
            {
                var result = new LiquidityGuardResult(true, LiquidityGuardReason.LowVolume, msg, DateTime.UtcNow);
                LastDanger = result;
                return result;
            }

            // иначе — НЕ блокируем, только опасность
            _logger.LogWarning("[LiquidityGuard] SOFT LOW-VOLUME {Symbol} ratio={Ratio:F2}", symbol, volRatio);
            LastDanger = new LiquidityGuardResult(false, LiquidityGuardReason.LowVolume, msg, DateTime.UtcNow);
            return LastDanger;
        }

        // 4) Stop-hunt вниз → блокируем шорт
        if (hugeLowerWick && last.ClosePrice > last.OpenPrice && side == SignalSide.Sell)
        {
            var msg = $"STOP-HUNT DOWN {symbol} {interval}";
            _logger.LogWarning("[LiquidityGuard] BLOCKED SHORT: {Msg}", msg);

            var result = new LiquidityGuardResult(true, LiquidityGuardReason.StopHuntDown, msg, DateTime.UtcNow);
            LastDanger = result;
            return result;
        }

        // 5) Stop-hunt вверх → блокируем лонг
        if (hugeUpperWick && last.ClosePrice < last.OpenPrice && side == SignalSide.Buy)
        {
            var msg = $"STOP-HUNT UP {symbol} {interval}";
            _logger.LogWarning("[LiquidityGuard] BLOCKED LONG: {Msg}", msg);

            var result = new LiquidityGuardResult(true, LiquidityGuardReason.StopHuntUp, msg, DateTime.UtcNow);
            LastDanger = result;
            return result;
        }

        var ok = new LiquidityGuardResult(false, LiquidityGuardReason.None, null, DateTime.UtcNow);
        LastDanger = ok;
        return ok;
    }

    public bool IsDangerRecent(TimeSpan ttl)
    {
        return LastDanger != null &&
               LastDanger.UtcTime != default &&
               (DateTime.UtcNow - LastDanger.UtcTime) <= ttl;
    }
}
