using System;
using System.Collections.Generic;
using System.Linq;
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

public record LiquidityGuardResult(bool Block, LiquidityGuardReason Reason, string? Details = null);

public class LiquidityGuardService
{
    private readonly ILogger<LiquidityGuardService> _logger;

    public LiquidityGuardService(ILogger<LiquidityGuardService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// PRO-фильтр ликвидности:
    /// - умный low-volume
    /// - smart stop-hunt detection
    /// - major coins (BTC/ETH) НЕ блокируются
    /// - SuperSignal override
    /// </summary>
    public LiquidityGuardResult Analyze(
        string symbol,
        KlineInterval interval,
        IReadOnlyList<BinanceFuturesUsdtKline> klines,
        SignalSide side,
        bool superSignal = false)
    {
        if (klines.Count < 30)
            return new LiquidityGuardResult(false, LiquidityGuardReason.None);

        bool isMajor = symbol is "BTCUSDT" or "ETHUSDT";

        var window = klines.Skip(Math.Max(0, klines.Count - 20)).ToList();

        decimal avgBody = window.Average(k => Math.Abs(k.ClosePrice - k.OpenPrice));
        decimal avgVolume = window.Average(k => k.Volume);

        var last = klines[^1];

        decimal upperWick = last.HighPrice - Math.Max(last.OpenPrice, last.ClosePrice);
        decimal lowerWick = Math.Min(last.OpenPrice, last.ClosePrice) - last.LowPrice;

        bool volumeSpike = last.Volume > avgVolume * 2.0m;
        bool tinyVolume = last.Volume < avgVolume * 0.25m;

        bool hugeLowerWick = lowerWick > avgBody * 2.0m && volumeSpike;
        bool hugeUpperWick = upperWick > avgBody * 2.0m && volumeSpike;

        // 1. SuperSignal — всегда разрешён
        if (superSignal)
        {
            _logger.LogInformation("[LiquidityGuard] SUPER-SIGNAL override → {Symbol} allowed", symbol);
            return new LiquidityGuardResult(false, LiquidityGuardReason.None);
        }

        // 2. BTC/ETH — не рубим по low-volume
        if (isMajor && tinyVolume)
        {
            _logger.LogInformation(
                "[LiquidityGuard] LOW-VOLUME IGNORED for MAJOR {Symbol}. vol={Vol:F2}, avg={Avg:F2}",
                symbol, last.Volume, avgVolume);
            tinyVolume = false;
        }

        // 3. Low Volume
        if (tinyVolume)
        {
            var msg = $"LOW VOLUME {symbol} {interval} | vol={last.Volume:F2}, avg={avgVolume:F2}";
            ConsoleReportFormatter.LiquidityBlocked(
                _logger,
                symbol,
                interval.ToString(),
                last.Volume,
                avgVolume);
            return new LiquidityGuardResult(true, LiquidityGuardReason.LowVolume, msg);
        }

        // 4. Stop-hunt вниз → блокируем шорт
        if (hugeLowerWick && last.ClosePrice > last.OpenPrice && side == SignalSide.Sell)
        {
            var msg = $"STOP-HUNT DOWN {symbol} {interval}";
            _logger.LogWarning("[LiquidityGuard] BLOCKED SHORT: {Msg}", msg);
            return new LiquidityGuardResult(true, LiquidityGuardReason.StopHuntDown, msg);
        }

        // 5. Stop-hunt вверх → блокируем лонг
        if (hugeUpperWick && last.ClosePrice < last.OpenPrice && side == SignalSide.Buy)
        {
            var msg = $"STOP-HUNT UP {symbol} {interval}";
            _logger.LogWarning("[LiquidityGuard] BLOCKED LONG: {Msg}", msg);
            return new LiquidityGuardResult(true, LiquidityGuardReason.StopHuntUp, msg);
        }

        return new LiquidityGuardResult(false, LiquidityGuardReason.None);
    }
}
