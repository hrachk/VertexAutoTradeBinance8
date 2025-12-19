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
    // === NEW: локальный кэш стакана ===
    private readonly Dictionary<string, OrderBookSnapshot> _depthCache = new();
    private readonly MarketDataFacade _md;

    public MarketDataService(ILogger<MarketDataService> logger, BinanceClientFactory factory, SmartRegimeService smartRegime, MarketDataFacade md   )
    {
        _logger = logger;
        _factory = factory;
        _smartRegime = smartRegime;
        _md = md;
    }
    // ============================================================
    // 1) Futures Klines (IBinanceKline) — без изменений
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
    // 2) Klines (BinanceFuturesUsdtKline) — без изменений
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
        using var client = _factory.CreateRestClient();

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
              kl = await _md.GetKlinesAsync(symbol, tf, 200, ct);

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
