// =======================================================
// BinanceHistoryImporter.cs
// =======================================================
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using static FuturesTradeAggregator;

public sealed class BinanceHistoryImporter
{
    private readonly AiSelfLearningService _ai;

    public BinanceHistoryImporter(AiSelfLearningService ai)
    {
        _ai = ai;
    }

    public void ImportClosedTrades(IEnumerable<BinanceFuturesUsdtUserTrade> fills)
    {
        var trades = FuturesTradeAggregator.BuildClosedTrades(fills)
            .OrderBy(t => t.CloseTime);

        foreach (var t in trades)
        {
            // ⚠️ только то, что RecordTrade принимает
            _ai.RecordTrade(
                symbol: t.Symbol,
                side: t.Side,
                entry: t.EntryPrice,
                exit: t.ExitPrice,
                regime: MarketRegime.Unknown
            );

            // ⚠️ если хочешь помнить последний импорт, можно отдельным полем
            _ai.UpdateLastImportedTradeCloseUtc(t.CloseTime);
        }
    }

}
