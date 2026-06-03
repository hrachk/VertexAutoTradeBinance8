using System.Collections.Concurrent;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services;

public class MarketDataService
{
    private readonly ILogger<MarketDataService> _logger;
    private readonly BinanceClientFactory _factory;
    private readonly SmartRegimeService _smartRegime;
    private readonly MarketDataFacade _md;

    // =========================
    // ORDER BOOK CACHE (PRO)
    // =========================
    private sealed record DepthCacheEntry(OrderBookSnapshot Snapshot, DateTime UpdatedUtc, int Depth);

    private readonly ConcurrentDictionary<string, DepthCacheEntry> _depthCache =
        new(StringComparer.OrdinalIgnoreCase);

    // singleflight per symbol
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _depthLocks =
        new(StringComparer.OrdinalIgnoreCase);

    // tune: 300-1200ms is typical for reactive
    private static readonly TimeSpan DepthTtl = TimeSpan.FromMilliseconds(800);

    public MarketDataService(
        ILogger<MarketDataService> logger,
        BinanceClientFactory factory,
        SmartRegimeService smartRegime,
        MarketDataFacade md)
    {
        _logger = logger;
        _factory = factory;
        _smartRegime = smartRegime;
        _md = md;
    }

    // ============================================================
    // 1) Futures Klines (IBinanceKline)
    // ============================================================
    public async Task<IReadOnlyList<IBinanceKline>> GetFuturesKlinesAsync(
        string symbol,
        KlineInterval interval,
        int limit = 100)
    {
        using var client = _factory.CreateRestClient();

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
    // 2) Klines (BinanceFuturesUsdtKline)
    // ============================================================
    public async Task<IReadOnlyList<BinanceFuturesUsdtKline>> GetKlines(
        string symbol,
        KlineInterval interval,
        int limit = 200)
    {
        using var client = _factory.CreateRestClient();

        var result = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
            symbol,
            interval,
            limit: limit
        );

        if (!result.Success || result.Data == null)
        {
            _logger.LogError("Error loading klines for {symbol}: {error}", symbol, result.Error);
            return Array.Empty<BinanceFuturesUsdtKline>();
        }

        return result.Data
            .Cast<BinanceFuturesUsdtKline>()
            .ToList();
    }

    // ============================================================
    // 3) EMA
    // ============================================================
    public decimal CalculateEma(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period)
    {
        if (klines.Count < period + 1)
            return 0;

        decimal k = 2m / (period + 1);
        decimal ema = klines[^period].ClosePrice;

        for (int i = klines.Count - period + 1; i < klines.Count; i++)
            ema = klines[i].ClosePrice * k + ema * (1 - k);

        return ema;
    }

    // ============================================================
    // 4) ATR
    // ============================================================
    public decimal CalculateAtr(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period = 14)
    {
        if (klines.Count < period + 1)
            return 0;

        decimal sum = 0m;
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
    // 5) FUTURES ORDER BOOK (PRODUCTION-GRADE)
    // - thread-safe cache
    // - per-symbol singleflight
    // - TTL
    // - fail-safe last-known-good
    // ============================================================
    public async Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, int depth = 50)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return null;

        var nowUtc = DateTime.UtcNow;

        // FAST PATH: return fresh cache without locking
        if (_depthCache.TryGetValue(symbol, out var cachedFast))
        {
            if (cachedFast.Depth == depth && (nowUtc - cachedFast.UpdatedUtc) <= DepthTtl)
                return cachedFast.Snapshot;
        }

        var gate = _depthLocks.GetOrAdd(symbol, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);

        try
        {
            // RE-CHECK under lock
            nowUtc = DateTime.UtcNow;
            if (_depthCache.TryGetValue(symbol, out var cached) &&
                cached.Depth == depth &&
                (nowUtc - cached.UpdatedUtc) <= DepthTtl)
            {
                return cached.Snapshot;
            }

            using var client = _factory.CreateRestClient();
            var result = await client.UsdFuturesApi.ExchangeData
                .GetOrderBookAsync(symbol, depth)
                .ConfigureAwait(false);

            if (!result.Success || result.Data == null)
            {
                _logger.LogWarning("Depth error for {symbol}: {err}", symbol, result.Error);

                // fail-safe: last-known-good
                if (_depthCache.TryGetValue(symbol, out cached))
                    return cached.Snapshot;

                return null;
            }

            // Make isolated copies (no shared state)
            var bids = result.Data.Bids.Select(x => (x.Price, x.Quantity)).ToList();
            var asks = result.Data.Asks.Select(x => (x.Price, x.Quantity)).ToList();

            // Binance обычно отдаёт bids/asks отсортированными, но не полагаемся на это
            // bids: desc, asks: asc
            bids.Sort((a, b) => b.Price.CompareTo(a.Price));
            asks.Sort((a, b) => a.Price.CompareTo(b.Price));

            var snap = new OrderBookSnapshot(symbol, bids, asks, DateTime.UtcNow);

            _depthCache[symbol] = new DepthCacheEntry(snap, DateTime.UtcNow, depth);
            return snap;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetOrderBookAsync failed for {symbol}", symbol);

            if (_depthCache.TryGetValue(symbol, out var cached))
                return cached.Snapshot;

            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    // ============================================================
    // 6) LAST KNOWN DEPTH
    // ============================================================
    public OrderBookSnapshot? GetCachedDepth(string symbol)
    {
        return _depthCache.TryGetValue(symbol, out var entry)
            ? entry.Snapshot
            : null;
    }

    public async Task<MarketSnapshot?> GetMarketSnapshot(
        string symbol,
        KlineInterval tf,
        CancellationToken ct)
    {
        IReadOnlyList<BinanceFuturesUsdtKline>? kl;
        try
        {
            kl = await _md.GetKlinesAsync(symbol, tf, 200, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetMarketSnapshot klines failed for {symbol}", symbol);
            return null;
        }

        if (kl == null || kl.Count < 50)
            return null;

        decimal atr;
        try { atr = CalculateAtr(kl, 14); }
        catch { atr = 0m; }

        SmartRegimeInfo smart;
        try { smart = _smartRegime.Evaluate(symbol, tf, kl); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SmartRegime failed for {symbol}", symbol);
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
// MODEL (UNCHANGED)
// ============================================================
public record OrderBookSnapshot(
    string Symbol,
    List<(decimal price, decimal qty)> Bids,
    List<(decimal price, decimal qty)> Asks,
    DateTime Timestamp);
