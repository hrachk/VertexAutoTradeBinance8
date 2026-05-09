using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;

public class TradeResultMonitorService
{
    private readonly ILogger<TradeResultMonitorService> _logger;
    private readonly BinanceClientFactory _factory;
    private readonly AiSelfLearningService _aiLearning;

    // idempotency: symbol|entryTime|side
    private static readonly ConcurrentDictionary<string, bool> _processed = new();

    public TradeResultMonitorService(
        ILogger<TradeResultMonitorService> logger,
        BinanceClientFactory factory,
        AiSelfLearningService aiLearning)
    {
        _logger = logger;
        _factory = factory;
        _aiLearning = aiLearning;
    }

    public async Task CheckClosedPositionAsync(
     string symbol,
     TradeSignal signal,
     decimal realizedPnlUsd,
     decimal exitPrice,
     MarketRegime exitRegime,
     CancellationToken ct)
    {
        if (signal == null)
            return;

        // ------------------------------------------------------------
        // 1) Idempotency guard (per-signal)
        // ------------------------------------------------------------
        var key = $"{symbol}|{signal.Side}|{signal.Time:O}";
        if (!_processed.TryAdd(key, true))
            return;

        try
        {
            // ------------------------------------------------------------
            // 2) Geometry sanity checks
            // ------------------------------------------------------------
            if (signal.EntryPrice <= 0 || exitPrice <= 0)
                return;

            var risk = Math.Abs(signal.EntryPrice - signal.StopLoss);
            if (risk <= 0)
                return;

            var reward = Math.Abs(exitPrice - signal.EntryPrice);
            var rr = reward / risk;

            // hard sanity filter
            if (rr <= 0m || rr > 10m)
                return;

            // ------------------------------------------------------------
            // 3) AI learning (CANONICAL CALL)
            // ------------------------------------------------------------
            _aiLearning.RecordTrade(
                symbol: symbol,
                side: signal.Side,
                entry: signal.EntryPrice,
                exit: exitPrice,
                regime: exitRegime
            );

            // ------------------------------------------------------------
            // 4) Logging (informational only)
            // ------------------------------------------------------------
            bool isWin =
                (signal.Side == SignalSide.Buy && exitPrice > signal.EntryPrice) ||
                (signal.Side == SignalSide.Sell && exitPrice < signal.EntryPrice);

            _logger.LogInformation(
                "[AI-LEARN] CLOSED {symbol} {side} | entry={entry:F2} exit={exit:F2} rr={rr:F2} pnlUsd={pnl:F2} win={win} regime={regime}",
                symbol,
                signal.Side,
                signal.EntryPrice,
                exitPrice,
                rr,
                realizedPnlUsd,
                isWin,
                exitRegime
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TRADE-MONITOR] RecordTrade failed");
        }
    }

}
