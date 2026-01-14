using System.Globalization;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services;

public class SymbolInfoService
{
    private readonly ILogger<SymbolInfoService> _logger;
    private readonly BinanceClientFactory _factory;

    // PROD SAFE STATIC CACHE
    private static BinanceFuturesUsdtExchangeInfo? _cachedExchangeInfo;
    private static DateTime _exchangeInfoTs;
    private static readonly SemaphoreSlim _refreshGate = new(1, 1);

    // (опционально) лог типов фильтров один раз на символ
    private static readonly HashSet<string> _loggedSymbols =
        new(StringComparer.OrdinalIgnoreCase);

    public SymbolInfoService(
        ILogger<SymbolInfoService> logger,
        BinanceClientFactory factory)
    {
        _logger = logger;
        _factory = factory;
    }

    /// <summary>
    /// Возвращает stepSize, minQty, minNotional, tickSize
    /// </summary>
    public async Task<(decimal step, decimal minQty, decimal minNotional, decimal tickSize)>
        GetFuturesFiltersAsync(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return SafeDefault("UNKNOWN", "EMPTY_SYMBOL");

        await EnsureExchangeInfoCachedAsync(symbol);

        var sym = _cachedExchangeInfo!
            .Symbols
            .FirstOrDefault(s => string.Equals(s.Name, symbol, StringComparison.OrdinalIgnoreCase));

        if (sym == null)
        {
            _logger.LogError("[FILTER][{symbol}] Symbol not found → SAFE DEFAULT", symbol);
            return SafeDefault(symbol, "SYMBOL_NOT_FOUND");
        }

        var filtersList = sym.Filters?.ToList();
        if (filtersList == null || filtersList.Count == 0)
        {
            _logger.LogCritical("[FILTER][{symbol}] EMPTY filters → SAFE DEFAULT", symbol);
            return SafeDefault(symbol, "EMPTY_FILTERS");
        }

        // типы фильтров — один раз на символ, иначе лог-спам
        if (_loggedSymbols.Add(symbol))
        {
            _logger.LogInformation("[FILTER][{symbol}] FilterTypes={types}",
                symbol, string.Join(", ", filtersList.Select(x => x.GetType().Name)));
        }

        decimal step = 0m;
        decimal minQty = 0m;
        decimal minNotional = 0m;
        decimal tickSize = 0m;

        foreach (var f in filtersList)
        {
            var type = f.GetType().Name;

            if (type.Contains("LotSize", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("MarketLotSize", StringComparison.OrdinalIgnoreCase))
            {
                step = ReadDec(f, "StepSize", "Step", "QtyStep");
                minQty = ReadDec(f, "MinQuantity", "MinQty", "MinQuantityValue");
            }
            else if (type.Contains("Price", StringComparison.OrdinalIgnoreCase))
            {
                tickSize = ReadDec(f, "TickSize", "Tick", "PriceTick");
            }
            else if (type.Contains("Notional", StringComparison.OrdinalIgnoreCase))
            {
                minNotional = ReadDec(f, "MinNotional", "Notional", "MinNotionalValue");
            }
        }

        // MANDATORY SAFE DEFAULTS
        if (tickSize <= 0m)
        {
            tickSize = 0.0001m;
            _logger.LogWarning("[FILTER][{symbol}] tickSize missing → fallback {tick}", symbol, tickSize);
        }

        if (step <= 0m)
        {
            step = tickSize;
            _logger.LogWarning("[FILTER][{symbol}] stepSize missing → fallback {step}", symbol, step);
        }

        if (minQty <= 0m) minQty = step;
        if (minNotional <= 0m) minNotional = 5m;

        _logger.LogInformation(
            "[FILTER][{symbol}] step={step}, minQty={minQty}, minNotional={minNotional}, tick={tick}",
            symbol, step, minQty, minNotional, tickSize);

        return (step, minQty, minNotional, tickSize);
    }

    public async Task<decimal> GetTickSizeAsync(string symbol)
    {
        var (_, _, _, tick) = await GetFuturesFiltersAsync(symbol);
        return tick;
    }

    private async Task EnsureExchangeInfoCachedAsync(string symbol)
    {
        // fast-path
        if (_cachedExchangeInfo != null &&
            (DateTime.UtcNow - _exchangeInfoTs).TotalMinutes <= 30)
            return;

        await _refreshGate.WaitAsync();
        try
        {
            // double-check under lock
            if (_cachedExchangeInfo != null &&
                (DateTime.UtcNow - _exchangeInfoTs).TotalMinutes <= 30)
                return;

            using var client = _factory.CreateRestClient();
            var ex = await client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();

            if (!ex.Success || ex.Data == null)
            {
                _logger.LogError("[FILTER][{symbol}] ExchangeInfo load failed → SAFE DEFAULT. Err={err}",
                    symbol, ex.Error);
                _cachedExchangeInfo = null;
                _exchangeInfoTs = DateTime.MinValue;
                return;
            }

            _cachedExchangeInfo = ex.Data;
            _exchangeInfoTs = DateTime.UtcNow;

            _logger.LogInformation("[FILTER] ExchangeInfo cached (symbols={cnt})",
                _cachedExchangeInfo.Symbols?.Length ?? 0);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static decimal ReadDec(object obj, params string[] names)
    {
        foreach (var name in names)
        {
            var p = obj.GetType().GetProperty(name);
            if (p == null) continue;

            var v = p.GetValue(obj);
            if (v is null) continue;

            if (v is decimal d) return d;
            if (v is double dd) return (decimal)dd;
            if (v is float ff) return (decimal)ff;
            if (v is int i) return i;
            if (v is long l) return l;

            if (v is string s &&
                decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var ds))
                return ds;
        }
        return 0m;
    }

    private (decimal step, decimal minQty, decimal minNotional, decimal tickSize)
        SafeDefault(string symbol, string reason)
    {
        _logger.LogWarning("[FILTER][{symbol}] SAFE DEFAULT applied. reason={reason}", symbol, reason);
        return (step: 0.0001m, minQty: 0.001m, minNotional: 5m, tickSize: 0.0001m);
    }
}
