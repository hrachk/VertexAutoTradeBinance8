using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Caching.Memory;

namespace VertexAutoTradeBinance8.Services;

public sealed class MarketDataFacade : IMarketDataFacade
{
    private readonly IMemoryCache _cache;
    private readonly BinanceClientFactory _factory; // твой существующий фабричный слой
    private readonly ILogger<MarketDataFacade> _logger;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public MarketDataFacade(
        IMemoryCache cache,
        BinanceClientFactory factory,
        ILogger<MarketDataFacade> logger)
    {
        _cache = cache;
        _factory = factory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<BinanceFuturesUsdtKline>> GetKlinesAsync(
        string symbol,
        KlineInterval interval,
        int limit,
        CancellationToken ct)
    {
        // Ключ кэша: символ+ТФ+лимит
        string key = $"klines:{symbol}:{interval}:{limit}";

        if (_cache.TryGetValue<IReadOnlyList<BinanceFuturesUsdtKline>>(key, out var cached) && cached != null)
            return cached;

        var sem = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            // double-check после ожидания
            if (_cache.TryGetValue<IReadOnlyList<BinanceFuturesUsdtKline>>(key, out cached) && cached != null)
                return cached;

            using var client = _factory.CreateRestClient(); // важно: НЕ new BinanceRestClient()

            var res = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                symbol,
                interval,
                limit: limit,
                ct: ct);

            if (!res.Success || res.Data == null)
            {
                _logger.LogWarning("[MKT] GetKlines FAIL {symbol} {tf}: {err}", symbol, interval, res.Error);
                return Array.Empty<BinanceFuturesUsdtKline>();
            }

            // Если тип уже BinanceFuturesUsdtKline — можно ToList()
            // Если другой тип — маппинг оставляем
            var list = res.Data.Select(k => new BinanceFuturesUsdtKline
            {
                OpenTime = k.OpenTime,
                OpenPrice = k.OpenPrice,
                HighPrice = k.HighPrice,
                LowPrice = k.LowPrice,
                ClosePrice = k.ClosePrice,
                Volume = k.Volume,
                CloseTime = k.CloseTime,
                QuoteVolume = k.QuoteVolume,
                TakerBuyBaseVolume = k.TakerBuyBaseVolume,
                TakerBuyQuoteVolume = k.TakerBuyQuoteVolume,
                TradeCount = k.TradeCount
            }).ToList();

            // TTL — короткий, чтобы не стать “памятью прошлого”
            var ttl = interval switch
            {
                KlineInterval.OneMinute => TimeSpan.FromSeconds(2),
                KlineInterval.FiveMinutes => TimeSpan.FromSeconds(5),
                _ => TimeSpan.FromSeconds(10)
            };

            _cache.Set(key, list, ttl);
            return list;
        }
        finally
        {
            sem.Release();
        }
    }
}
