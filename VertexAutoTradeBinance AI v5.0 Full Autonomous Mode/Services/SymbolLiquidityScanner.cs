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

    // =====================================================
    // GetTickersAsync (все фьючерсы) имеет weight=40
    // Защищаемся от слишком частых вызовов — минимум 60 сек между попытками
    // =====================================================
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(60);

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

            using var client = _factory.CreatePublicRestClient(); // public tickers — no API key required

            // =====================================================
            // Rate limit guard: GetTickersAsync weight=40
            // Не шлём запрос если последняя попытка была < 60 сек назад
            // =====================================================
            var sinceLastAttempt = DateTime.UtcNow - _lastAttemptUtc;
            if (sinceLastAttempt < MinRequestInterval && _cache.Count > 0)
            {
                _logger.LogDebug("[SYMBOL] Rate limit guard: returning cache (last attempt {s}s ago)", (int)sinceLastAttempt.TotalSeconds);
                return _cache;
            }
            _lastAttemptUtc = DateTime.UtcNow;

            // =========================
            // 1) TRY TICKERS (3 RETRIES с безопасной обработкой ошибок)
            // GetTickersAsync weight=40 — тяжёлый запрос, используем осторожно
            // =========================
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var res = await client.UsdFuturesApi.ExchangeData.GetTickersAsync(ct);
                    if (res.Success && res.Data != null)
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

            // Never leave CORE with zero universe for a full day — hard majors fallback
            _logger.LogError("[SYMBOL] no market data — returning hardcoded majors fallback");
            _cache = new List<SymbolMarketSnapshot>
            {
                new() { Symbol = "BTCUSDT", QuoteVolume24h = 1e9m, LastPrice = 1m },
                new() { Symbol = "ETHUSDT", QuoteVolume24h = 5e8m, LastPrice = 1m },
                new() { Symbol = "SOLUSDT", QuoteVolume24h = 2e8m, LastPrice = 1m },
                new() { Symbol = "BNBUSDT", QuoteVolume24h = 1e8m, LastPrice = 1m },
                new() { Symbol = "XRPUSDT", QuoteVolume24h = 1e8m, LastPrice = 1m },
                new() { Symbol = "DOGEUSDT", QuoteVolume24h = 8e7m, LastPrice = 1m },
                new() { Symbol = "ADAUSDT", QuoteVolume24h = 7e7m, LastPrice = 1m },
                new() { Symbol = "AVAXUSDT", QuoteVolume24h = 6e7m, LastPrice = 1m },
                new() { Symbol = "LINKUSDT", QuoteVolume24h = 5e7m, LastPrice = 1m },
                new() { Symbol = "DOTUSDT", QuoteVolume24h = 4e7m, LastPrice = 1m },
            };
            _cachedAtUtc = DateTime.UtcNow;
            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }
}
