using Binance.Net.Objects.Models.Futures;
using Binance.Net.Objects.Models.Spot;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;

namespace VertexAutoTradeBinance8.Services;

public sealed class SymbolInfoService
{
    private readonly ILogger<SymbolInfoService> _logger;
    private readonly BinanceClientFactory _factory;

    // ===== GLOBAL CACHE (INTENTIONALLY STATIC) =====
    private static BinanceFuturesUsdtExchangeInfo? _cachedExchangeInfo;
    private static DateTime _exchangeInfoTsUtc = DateTime.MinValue;

    // single-flight gate for ALL instances
    private static readonly SemaphoreSlim _refreshGate = new(1, 1);
    private static DateTime _lastAttemptUtc = DateTime.MinValue;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SymbolInfoService(
        ILogger<SymbolInfoService> logger,
        BinanceClientFactory factory)
    {
        _logger = logger;
        _factory = factory;
    }

    // ======================================================
    // PUBLIC MODELS
    // ======================================================

    public enum QtyRule
    {
        Market,
        Limit
    }

    public sealed record FuturesFilters(
     decimal StepSize,
     decimal MinQty,
     decimal MaxQty,
     decimal MinNotional,
     decimal TickSize,
     QtyRule RuleUsed)
    {
        public static FuturesFilters Fallback(QtyRule rule) =>
    new(
        StepSize: 0.0001m,
        MinQty: 0.0001m,
        MaxQty: 1_000_000m,
        MinNotional: 0m,
        TickSize: 0.0001m,
        RuleUsed: rule
    );
    }

    // ======================================================
    // PUBLIC API
    // ======================================================

    public async Task<(decimal step, decimal minQty, decimal maxQty, decimal minNotional, decimal tickSize)>
        GetFuturesFiltersAsync(
            string symbol,
            QtyRule rule = QtyRule.Market,
            CancellationToken ct = default)
    {
        var f = await GetFuturesFiltersDetailedAsync(symbol, rule, ct).ConfigureAwait(false);
        return (f.StepSize, f.MinQty, f.MaxQty, f.MinNotional, f.TickSize);

    }

    public async Task<FuturesFilters> GetFuturesFiltersDetailedAsync(
    string symbol,
    QtyRule rule = QtyRule.Market,
    CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException(nameof(symbol));

        await EnsureExchangeInfoCachedAsync(ct).ConfigureAwait(false);

        var info = _cachedExchangeInfo;
        if (info?.Symbols == null)
        {
            _logger.LogError("[FILTER][{symbol}] ExchangeInfo missing → FALLBACK", symbol);
            return FuturesFilters.Fallback(rule);
        }

        var sym = info.Symbols.FirstOrDefault(s =>
            s.Name.Equals(symbol, StringComparison.OrdinalIgnoreCase));

        if (sym == null)
        {
            _logger.LogError("[FILTER][{symbol}] Symbol not found → FALLBACK", symbol);
            return FuturesFilters.Fallback(rule);
        }

        decimal step = 0m;
        decimal minQty = 0m;
        decimal maxQty = decimal.MaxValue;
        decimal tick = 0.0001m;
        decimal minNotional = 0m;

        if (sym.Filters != null)
        {
            foreach (var f in sym.Filters)
            {
                switch (f)
                {
                    case BinanceSymbolLotSizeFilter lot:
                        step = Math.Max(step, lot.StepSize);
                        minQty = Math.Max(minQty, lot.MinQuantity);
                        maxQty = Math.Min(maxQty, lot.MaxQuantity);
                        break;

                    case BinanceSymbolMarketLotSizeFilter mlot:
                        step = Math.Max(step, mlot.StepSize);
                        minQty = Math.Max(minQty, mlot.MinQuantity);
                        maxQty = Math.Min(maxQty, mlot.MaxQuantity);
                        break;

                    case BinanceSymbolPriceFilter price:
                        tick = price.TickSize;
                        break;

                    case BinanceSymbolMinNotionalFilter notional:
                        minNotional = notional.MinNotional;
                        break;
                }
            }
        }

        // ===== HARD FAIL-SAFE NORMALIZATION =====
        if (step <= 0m) step = 0.0001m;
        if (minQty <= 0m) minQty = step;
        if (maxQty <= 0m)
            maxQty = decimal.MaxValue;
        if (tick <= 0m) tick = 0.0001m;
       

        _logger.LogInformation(
            "[FILTER][{symbol}] rule={rule} step={step} minQty={minQty} tick={tick} minNotional={minNotional}",
            symbol, rule, step, minQty, tick, minNotional);

        return new FuturesFilters(step, minQty, maxQty, minNotional, tick, rule);

    }


    public async Task<decimal> GetTickSizeAsync(
        string symbol,
        CancellationToken ct = default)
    {
        var f = await GetFuturesFiltersDetailedAsync(symbol, QtyRule.Market, ct)
            .ConfigureAwait(false);
        return f.TickSize;
    }

    /// <summary>
    /// ⚠️ Использовать ТОЛЬКО в OrderExecutor перед реальным placement.
    /// В логике сигналов / симуляции НЕ вызывать.
    /// </summary>
    public static void EnforceMinNotionalOrThrow(
        string symbol,
        decimal qty,
        decimal price,
        decimal minNotional)
    {
        if (qty <= 0m || price <= 0m || minNotional <= 0m)
            return;

        var notional = qty * price;
        if (notional + 1e-12m < minNotional)
            throw new InvalidOperationException(
                $"[FILTER][{symbol}] Notional too small: {notional.ToString("0.########", CultureInfo.InvariantCulture)} < {minNotional.ToString("0.########", CultureInfo.InvariantCulture)}");
    }

    // ======================================================
    // EXCHANGE INFO CACHE (PRODUCTION SAFE)
    // ======================================================

    private async Task EnsureExchangeInfoCachedAsync(CancellationToken ct)
    {
        if (_cachedExchangeInfo != null &&
            DateTime.UtcNow - _exchangeInfoTsUtc <= CacheTtl)
            return;

        if (_cachedExchangeInfo != null &&
            DateTime.UtcNow - _lastAttemptUtc < RetryCooldown)
            return;

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedExchangeInfo != null &&
                DateTime.UtcNow - _exchangeInfoTsUtc <= CacheTtl)
                return;

            _lastAttemptUtc = DateTime.UtcNow;

            using var client = _factory.CreateRestClient();

            Exception? lastError = null;

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var res = await client
                        .UsdFuturesApi
                        .ExchangeData
                        .GetExchangeInfoAsync(ct: ct)
                        .ConfigureAwait(false);

                    if (!res.Success || res.Data?.Symbols == null || res.Data.Symbols.Length == 0)
                        throw new InvalidOperationException(res.Error?.Message ?? "Empty ExchangeInfo");

                    _cachedExchangeInfo = res.Data;
                    _exchangeInfoTsUtc = DateTime.UtcNow;

                    _logger.LogInformation(
                        "[FILTER] ExchangeInfo cached OK (symbols={cnt})",
                        res.Data.Symbols.Length);

                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _logger.LogWarning(
                        ex,
                        "[FILTER] ExchangeInfo attempt {attempt}/3 failed",
                        attempt);

                    await Task.Delay(300 * attempt, ct);
                }
            }

            if (_cachedExchangeInfo != null)
            {
                _logger.LogError(
                    lastError,
                    "[FILTER] ExchangeInfo refresh failed → USING STALE CACHE");
                return;
            }

            throw new InvalidOperationException(
                "[FILTER] ExchangeInfo unavailable and no cache present",
                lastError);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<bool> SymbolExistsAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return false;

        await EnsureExchangeInfoCachedAsync(ct).ConfigureAwait(false);

        var info = _cachedExchangeInfo;

        if (info?.Symbols == null)
            return false;

        return info.Symbols.Any(s =>
            s.Name.Equals(symbol, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<decimal> NormalizeQtyAsync(
    string symbol,
    decimal qty,
    CancellationToken ct = default)
    {
        var filters = await GetFuturesFiltersDetailedAsync(symbol, QtyRule.Market, ct);

        var step = filters.StepSize;

        if (step <= 0)
            return qty;

        return Math.Floor(qty / step) * step;
    }
}
