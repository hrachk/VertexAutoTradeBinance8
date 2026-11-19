/* MarketDataService v4.0 — LiquidityCluster Ready */

using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services;

public class MarketDataService
{
    private readonly ILogger<MarketDataService> _logger;
    private readonly BinanceClientFactory _factory;
    private readonly SmartRegimeService _smartRegime;
    // === NEW: локальный кэш стакана ===
    private readonly Dictionary<string, OrderBookSnapshot> _depthCache = new();

    public MarketDataService(ILogger<MarketDataService> logger, BinanceClientFactory factory, SmartRegimeService smartRegime)
    {
        _logger = logger;
        _factory = factory;
        _smartRegime = smartRegime;
    }

    // ============================================================
    // 1) Futures Klines (IBinanceKline) — без изменений
    // ============================================================
    public async Task<IReadOnlyList<IBinanceKline>> GetFuturesKlinesAsync(
        string symbol,
        KlineInterval interval,
        int limit = 100)
    {
        var client = _factory.CreateRestClient();

        var result = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
            symbol,
            interval,
            limit: limit
        );

        if (!result.Success || result.Data == null)
        {
            _logger.LogError("Error fetching futures klines for {symbol}: {error}", symbol, result.Error);
            throw new Exception($"Error fetching futures klines: {result.Error}");
        }

        return result.Data;
    }

    // ============================================================
    // 2) Klines (BinanceFuturesUsdtKline) — с коротким кэшем
    //    FIX: одни и те же свечи запрашивались 3–6 раз за цикл на символ
    //    (TF-selector, strategy, risk, executor, supervisor) → weight limit / бан IP.
    // ============================================================
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime at, List<BinanceFuturesUsdtKline> data)> _klineCache = new();

    private static TimeSpan KlineTtl(KlineInterval interval) => interval switch
    {
        KlineInterval.OneMinute => TimeSpan.FromSeconds(4),
        KlineInterval.ThreeMinutes => TimeSpan.FromSeconds(6),
        KlineInterval.FiveMinutes => TimeSpan.FromSeconds(8),
        KlineInterval.FifteenMinutes => TimeSpan.FromSeconds(15),
        KlineInterval.ThirtyMinutes => TimeSpan.FromSeconds(30),
        KlineInterval.OneHour => TimeSpan.FromSeconds(60),
        _ => TimeSpan.FromMinutes(2)
    };

    public async Task<IReadOnlyList<BinanceFuturesUsdtKline>> GetKlines(
        string symbol,
        KlineInterval interval,
        int limit = 200)
    {
        var key = $"{symbol}|{interval}";
        if (_klineCache.TryGetValue(key, out var hit) &&
            DateTime.UtcNow - hit.at < KlineTtl(interval) &&
            hit.data.Count >= Math.Min(limit, 200))
        {
            return hit.data.Count <= limit ? hit.data : hit.data.Skip(hit.data.Count - limit).ToList();
        }

        var client = _factory.CreateRestClient();
        var fetch = Math.Max(limit, 200);
        var result = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, interval, limit: fetch);

        if (!result.Success || result.Data == null)
        {
            _logger.LogError("Error loading klines for {symbol}: {error}", symbol, result.Error);
            return Array.Empty<BinanceFuturesUsdtKline>();
        }

        var list = result.Data.Cast<BinanceFuturesUsdtKline>().ToList();
        _klineCache[key] = (DateTime.UtcNow, list);
        return list.Count <= limit ? list : list.Skip(list.Count - limit).ToList();
    }

    /// <summary>
    /// Только ЗАКРЫТЫЕ свечи. Последний элемент ответа Binance — формирующаяся свеча:
    /// её close/high/low меняются каждую секунду → сигналы «перерисовываются».
    /// FIX: стратегия теперь принимает решение только по закрытым барам.
    /// </summary>
    public async Task<IReadOnlyList<BinanceFuturesUsdtKline>> GetClosedKlines(
        string symbol,
        KlineInterval interval,
        int limit = 200)
    {
        var all = await GetKlines(symbol, interval, limit + 1);
        return DropFormingCandle(all);
    }

    public static IReadOnlyList<BinanceFuturesUsdtKline> DropFormingCandle(IReadOnlyList<BinanceFuturesUsdtKline> all)
    {
        if (all.Count == 0) return all;
        var now = DateTime.UtcNow;
        return all[^1].CloseTime > now ? all.Take(all.Count - 1).ToList() : all;
    }

    // ============================================================
    // 3) EMA (центр)
    // ============================================================
    public decimal CalculateEma(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period)
    {
        if (klines.Count < period + 1)
            return 0;

        decimal k = 2m / (period + 1);
        decimal ema = klines[^period].ClosePrice;

        for (int i = klines.Count - period + 1; i < klines.Count; i++)
        {
            ema = klines[i].ClosePrice * k + ema * (1 - k);
        }

        return ema;
    }

    // ============================================================
    // 4) ATR (центр)
    // ============================================================
    public decimal CalculateAtr(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period = 14)
    {
        if (klines.Count < period + 1)
            return 0;

        decimal sum = 0;
        for (int i = klines.Count - period; i < klines.Count; i++)
        {
            var cur = klines[i];
            var prev = klines[i - 1];

            decimal tr1 = cur.HighPrice - cur.LowPrice;
            decimal tr2 = Math.Abs(cur.HighPrice - prev.ClosePrice);
            decimal tr3 = Math.Abs(cur.LowPrice - prev.ClosePrice);

            sum += Math.Max(tr1, Math.Max(tr2, tr3));
        }

        return sum / period;
    }

    // ============================================================
    // 5) NEW — Получение стакана FUTURES
    // ============================================================
    public async Task<OrderBookSnapshot?> GetOrderBookAsync(
        string symbol,
        int depth = 50)
    {
        var client = _factory.CreateRestClient();

        var result = await client.UsdFuturesApi.ExchangeData.GetOrderBookAsync(symbol, depth);

        if (!result.Success || result.Data == null)
        {
            _logger.LogError("Depth error for {symbol}: {err}", symbol, result.Error);
            return null;
        }

        var snapshot = new OrderBookSnapshot(
            symbol,
            result.Data.Bids.Select(x => (x.Price, x.Quantity)).ToList(),
            result.Data.Asks.Select(x => (x.Price, x.Quantity)).ToList(),
            DateTime.UtcNow);

        _depthCache[symbol] = snapshot;
        return snapshot;
    }

    // ============================================================
    // 6) NEW — Получить последний кэш стакана
    // ============================================================
    public OrderBookSnapshot? GetCachedDepth(string symbol)
    {
        if (_depthCache.TryGetValue(symbol, out var snapshot))
            return snapshot;

        return null;
    }

    public async Task<MarketSnapshot?> GetMarketSnapshot(
      string symbol,
      KlineInterval tf,
      CancellationToken ct)
    {
        IReadOnlyList<BinanceFuturesUsdtKline>? kl;
        try
        {
            kl = await GetKlines(symbol, tf, 200);
        }
        catch
        {
            return null;
        }

        if (kl == null || kl.Count < 50)
            return null;

        // --- ATR ---
        decimal atr = 0;
        try
        {
            atr = CalculateAtr(kl, 14);   // <--- ИСПОЛЬЗУЕМ ТВОЙ СУЩЕСТВУЮЩИЙ МЕТОД
        }
        catch
        {
            atr = 0;
        }

        // --- Smart Regime ---
        SmartRegimeInfo smart;
        try
        {
            smart = _smartRegime.Evaluate(symbol, tf, kl);
        }
        catch
        {
            return null;
        }

        return new MarketSnapshot
        {
            TrendSlopePercent = smart.TrendSlopePercent,
            VolatilityPercent = smart.VolatilityPercent,
            Atr = atr,
            Confidence = smart.Confidence
        };
    }



}

// ============================================================
// МОДЕЛЬ СНИМКОВ СТАКАНА (NEW)
// ============================================================
public record OrderBookSnapshot(
    string Symbol,
    List<(decimal price, decimal qty)> Bids,
    List<(decimal price, decimal qty)> Asks,
    DateTime Timestamp);
