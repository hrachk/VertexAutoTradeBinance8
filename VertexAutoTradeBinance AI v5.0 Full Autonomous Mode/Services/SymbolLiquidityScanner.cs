using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;

public sealed class SymbolLiquidityScanner
{
    private readonly ILogger<SymbolLiquidityScanner> _logger;
    private readonly BinanceClientFactory _factory;
    private readonly IConfiguration _cfg;

    private readonly SemaphoreSlim _gate = new(1, 1);

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

    public async Task<List<SymbolMarketSnapshot>> LoadSnapshotsAsync(CancellationToken ct = default)
    {
        var ttlSec = _cfg.GetValue<int?>("SymbolSelection:Auto:ScannerCacheSeconds") ?? 900;

        // FAST CACHE
        if (_cache.Count > 0 &&
            (DateTime.UtcNow - _cachedAtUtc).TotalSeconds < ttlSec)
            return _cache;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.Count > 0 &&
                (DateTime.UtcNow - _cachedAtUtc).TotalSeconds < ttlSec)
                return _cache;

            using var client = _factory.CreateRestClient();

            // =========================
            // 1) TRY TICKERS (3 RETRIES с безопасной обработкой ошибок)
            // =========================
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var res = await client.UsdFuturesApi.ExchangeData.GetTickersAsync(ct);
                    if (res.Success && res.Data != null && res.Data.Count() > 0)
                    {
                        var topVolumeCount = _cfg.GetValue<int?>("SymbolSelection:Auto:TopVolumeCount") ?? 60;
                        var cap = Math.Clamp(Math.Max(topVolumeCount * 2, 60), 40, 200);

                        var list = res.Data
                            .Where(t =>
                                t.Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) &&
                                t.LastPrice > 0 &&
                                t.QuoteVolume > 0)
                            .OrderByDescending(t => t.QuoteVolume)
                            .Take(cap)
                            .Select(t => new SymbolMarketSnapshot
                            {
                                Symbol = t.Symbol.ToUpperInvariant(),
                                QuoteVolume24h = t.QuoteVolume,
                                LastPrice = t.LastPrice,
                                PriceChangePercent = t.PriceChangePercent
                            })
                            .ToList();

                        if (list.Count > 0)
                        {
                            _cache = list;
                            _cachedAtUtc = DateTime.UtcNow;

                            _logger.LogInformation(
                                "[SYMBOL] Tickers OK: {cnt} symbols (attempt {a})",
                                list.Count, attempt);

                            return _cache;
                        }
                    }

                    _logger.LogWarning("[SYMBOL] GetTickers empty (attempt {a})", attempt);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("[SYMBOL] GetTickers TIMEOUT (attempt {a})", attempt);
                }
                catch (Exception ex) when (ex.Message.Contains("HTTP/2") || ex.Message.Contains("PROTOCOL_ERROR"))
                {
                    _logger.LogWarning(ex, "[SYMBOL] GetTickers HTTP/2 PROTOCOL_ERROR (attempt {a})", attempt);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[SYMBOL] GetTickers OTHER ERROR (attempt {a})", attempt);
                }

                // exponential backoff (умеренный, чтобы не перегружать Binance)
                await Task.Delay(1000 * attempt, ct);
            }

            // ======================================================
            // 2) FALLBACK: ExchangeInfo (не трогаем логику, просто безопасно)
            // ======================================================
            _logger.LogError("[SYMBOL] GetTickers FAILED → FALLBACK ExchangeInfo");

            var info = await client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(ct);
            if (info.Success && info.Data != null)
            {
                var list = info.Data.Symbols
                    .Where(s =>
                        s.Name.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) &&
                        s.Status == Binance.Net.Enums.SymbolStatus.Trading)
                    .Select(s => new SymbolMarketSnapshot
                    {
                        Symbol = s.Name.ToUpperInvariant(),
                        QuoteVolume24h = 1_000_000m, // SAFE DUMMY
                        LastPrice = 1m,
                        PriceChangePercent = 0m
                    })
                    .ToList();

                if (list.Count > 0)
                {
                    _cache = list;
                    _cachedAtUtc = DateTime.UtcNow;

                    _logger.LogWarning("[SYMBOL] ExchangeInfo fallback used: {cnt} symbols", list.Count);
                    return _cache;
                }
            }

            // ==========================================
            // 3) LAST RESORT: KEEP OLD CACHE
            // ==========================================
            if (_cache.Count > 0)
            {
                _logger.LogCritical("[SYMBOL] REST DEAD → using LAST KNOWN GOOD cache size={cnt}", _cache.Count);
                return _cache;
            }

            throw new InvalidOperationException("SymbolLiquidityScanner: no market data available at all");
        }
        finally
        {
            _gate.Release();
        }
    }
}
