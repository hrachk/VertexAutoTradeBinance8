using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;

namespace VertexAutoTradeBinance8.Services;

public interface IMarketDataFacade
{
    Task<IReadOnlyList<BinanceFuturesUsdtKline>> GetKlinesAsync(
        string symbol,
        KlineInterval interval,
        int limit,
        CancellationToken ct);
}
