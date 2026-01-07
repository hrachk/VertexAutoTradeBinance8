using Microsoft.Extensions.Configuration;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services;

/// <summary>
/// SymbolLiquidityScanner (PRO)
/// ============================
/// - Единственный REST-вход для тикеров (GetTickersAsync)
/// - TTL-кэш (ScannerCacheSeconds)
/// - Singleflight (SemaphoreSlim)
/// - Fail-safe: при ошибке возвращает последний кэш
///
/// Разрешено вызывать сколько угодно раз —
/// фактический REST будет выполняться редко.
/// </summary>
public sealed class SymbolLiquidityScanner
{
    private readonly ILogger<SymbolLiquidityScanner> _logger;
    private readonly BinanceClientFactory _factory;
    private readonly IConfiguration _cfg;

    // singleflight gate
    private readonly SemaphoreSlim _gate = new(1, 1);

    // cache
    private DateTime _cachedAtUtc = DateTime.MinValue;
    private List<SymbolMarketSnapshot> _cache = new();

    public SymbolLiquidityScanner(
        ILogger<SymbolLiquidityScanner> logger,
        BinanceClientFactory factory,
        IConfiguration cfg)
    {
        _logger = logger;
        _factory = factory;
        _cfg = cfg;
    }

    public async Task<List<SymbolMarketSnapshot>> LoadSnapshotsAsync(
        CancellationToken ct = default)
    {
        var ttlSec =
            _cfg.GetValue<int?>("SymbolSelection:Auto:ScannerCacheSeconds")
            ?? 900; // default 15 min

        // FAST PATH — cache hit
        if (_cache.Count > 0 &&
            (DateTime.UtcNow - _cachedAtUtc).TotalSeconds < ttlSec)
        {
            return _cache;
        }

        await _gate.WaitAsync(ct);
        try
        {
            // DOUBLE-CHECK после входа в gate
            if (_cache.Count > 0 &&
                (DateTime.UtcNow - _cachedAtUtc).TotalSeconds < ttlSec)
            {
                return _cache;
            }

            using var client = _factory.CreateRestClient();

            var res = await client
                .UsdFuturesApi
                .ExchangeData
                .GetTickersAsync(ct);

            if (!res.Success || res.Data == null)
            {
                _logger.LogWarning(
                    "[SYMBOL] GetTickers REST failed → keep cache size={cnt}",
                    _cache.Count);

                return _cache.Count > 0
                    ? _cache
                    : new List<SymbolMarketSnapshot>();
            }

            var scannerCap =
    _cfg.GetValue<int?>("SymbolSelection:Auto:ScannerTopLimit")
    ?? 30; // SAFE DEFAULT

            var list = res.Data
    .Where(t =>
        t.Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) &&
        t.QuoteVolume > 0 &&
        t.LastPrice > 0
    )
    .OrderByDescending(t => t.QuoteVolume)
    .Take(scannerCap) // 🔥 ВАЖНО
    .Select(t => new SymbolMarketSnapshot
    {
        Symbol = t.Symbol.ToUpperInvariant(),
        QuoteVolume24h = t.QuoteVolume,
        LastPrice = t.LastPrice,
        PriceChangePercent = t.PriceChangePercent
    })
    .ToList();

            _cache = list;
            _cachedAtUtc = DateTime.UtcNow;

            _logger.LogInformation(
                "[SYMBOL] Loaded tickers snapshots: {cnt} (cache ttl={ttl}s)",
                list.Count,
                ttlSec);

            return list;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[SYMBOL] LoadSnapshotsAsync error → keep cache size={cnt}",
                _cache.Count);

            return _cache.Count > 0
                ? _cache
                : new List<SymbolMarketSnapshot>();
        }
        finally
        {
            _gate.Release();
        }
    }
}
