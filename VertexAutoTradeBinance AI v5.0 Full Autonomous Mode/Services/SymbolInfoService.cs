using System.Globalization;
using System.Text.Json;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services;

public sealed class SymbolInfoService
{
    private readonly ILogger<SymbolInfoService> _logger;
    private readonly BinanceClientFactory _factory;

    private static BinanceFuturesUsdtExchangeInfo? _cachedExchangeInfo;
    private static DateTime _exchangeInfoTsUtc;
    private static readonly SemaphoreSlim _refreshGate = new(1, 1);
    private static readonly HashSet<string> _loggedSymbols = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SymbolInfoService(ILogger<SymbolInfoService> logger, BinanceClientFactory factory)
    {
        _logger = logger;
        _factory = factory;
    }

    public enum QtyRule
    {
        Market, // StopMarket / TakeProfitMarket / Market close (SL/TP)
        Limit   // Limit entry
    }

    public sealed record FuturesFilters(
        decimal StepSize,
        decimal MinQty,
        decimal MinNotional,
        decimal TickSize,
        QtyRule RuleUsed);

    public async Task<(decimal step, decimal minQty, decimal minNotional, decimal tickSize)>
        GetFuturesFiltersAsync(string symbol, QtyRule rule = QtyRule.Market, CancellationToken ct = default)
    {
        var f = await GetFuturesFiltersDetailedAsync(symbol, rule, ct).ConfigureAwait(false);
        return (f.StepSize, f.MinQty, f.MinNotional, f.TickSize);
    }

    public async Task<FuturesFilters> GetFuturesFiltersDetailedAsync(
        string symbol,
        QtyRule rule = QtyRule.Market,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol cannot be empty.", nameof(symbol));

        await EnsureExchangeInfoCachedAsync(ct).ConfigureAwait(false);

        var info = _cachedExchangeInfo;
        if (info?.Symbols == null || info.Symbols.Length == 0)
            throw new InvalidOperationException($"[FILTER][{symbol}] ExchangeInfo missing/empty → TRADING BLOCKED");

        var sym = info.Symbols.FirstOrDefault(s =>
            s.Name.Equals(symbol, StringComparison.OrdinalIgnoreCase));

        if (sym == null)
            throw new InvalidOperationException($"[FILTER][{symbol}] Symbol not found → TRADING BLOCKED");

        var filters = sym.Filters;
        if (filters == null || filters.Length == 0)
            throw new InvalidOperationException($"[FILTER][{symbol}] Filters empty → TRADING BLOCKED");

        // Log filter types once per symbol
        if (_loggedSymbols.Add(symbol))
        {
            var types = new List<string>(filters.Length);
            foreach (var f in filters)
            {
                var el = JsonSerializer.SerializeToElement(f, JsonOpts);
                types.Add(ReadString(el, "FilterType", "filterType") ?? "UNKNOWN");
            }
            _logger.LogInformation("[FILTER][{symbol}] FilterTypes={types}", symbol, string.Join(", ", types));
        }

        decimal lotStep = 0m, lotMinQty = 0m;
        decimal marketStep = 0m, marketMinQty = 0m;
        decimal tickSize = 0m;
        decimal minNotional = 0m;

        foreach (var f in filters)
        {
            var el = JsonSerializer.SerializeToElement(f, JsonOpts);

            var type = ReadString(el, "FilterType", "filterType") ?? string.Empty;
            switch (type)
            {
                case "LOT_SIZE":
                    lotStep = ReadDecimal(el, "StepSize", "stepSize");
                    lotMinQty = ReadDecimal(el, "MinQuantity", "minQty", "minQuantity");
                    break;

                case "MARKET_LOT_SIZE":
                    marketStep = ReadDecimal(el, "StepSize", "stepSize");
                    marketMinQty = ReadDecimal(el, "MinQuantity", "minQty", "minQuantity");
                    break;

                case "PRICE_FILTER":
                    tickSize = ReadDecimal(el, "TickSize", "tickSize");
                    break;

                case "MIN_NOTIONAL":
                    minNotional = ReadDecimal(el, "MinNotional", "minNotional");
                    break;

                default:
                    // Важно: не спамим логами постоянно
                    break;
            }
        }

        decimal step;
        decimal minQty;

        if (rule == QtyRule.Market)
        {
            if (marketStep <= 0m || marketMinQty <= 0m)
                throw new InvalidOperationException($"[FILTER][{symbol}] MARKET_LOT_SIZE missing/invalid → TRADING BLOCKED");

            step = marketStep;
            minQty = marketMinQty;
        }
        else
        {
            if (lotStep <= 0m || lotMinQty <= 0m)
                throw new InvalidOperationException($"[FILTER][{symbol}] LOT_SIZE missing/invalid → TRADING BLOCKED");

            step = lotStep;
            minQty = lotMinQty;
        }

        if (tickSize <= 0m)
            throw new InvalidOperationException($"[FILTER][{symbol}] PRICE_FILTER missing/invalid → TRADING BLOCKED");

        if (step <= 0m || minQty <= 0m)
            throw new InvalidOperationException($"[FILTER][{symbol}] INVALID qty filters step={step} minQty={minQty} → TRADING BLOCKED");

        _logger.LogInformation(
            "[FILTER][{symbol}] rule={rule} step={step} minQty={minQty} minNotional={minNotional} tick={tick}",
            symbol, rule, step, minQty, minNotional, tickSize);

        return new FuturesFilters(step, minQty, minNotional, tickSize, rule);
    }

    public async Task<decimal> GetTickSizeAsync(string symbol, CancellationToken ct = default)
    {
        var f = await GetFuturesFiltersDetailedAsync(symbol, QtyRule.Market, ct).ConfigureAwait(false);
        return f.TickSize;
    }

    public static void EnforceMinNotionalOrThrow(string symbol, decimal qty, decimal price, decimal minNotional)
    {
        if (qty <= 0m || price <= 0m || minNotional <= 0m) return;

        var notional = qty * price;
        if (notional + 1e-12m < minNotional)
            throw new InvalidOperationException(
                $"[FILTER][{symbol}] Notional too small: {notional.ToString("0.########", CultureInfo.InvariantCulture)} < {minNotional.ToString("0.########", CultureInfo.InvariantCulture)}");
    }

    private async Task EnsureExchangeInfoCachedAsync(CancellationToken ct)
    {
        if (_cachedExchangeInfo != null &&
            (DateTime.UtcNow - _exchangeInfoTsUtc).TotalMinutes <= 30)
            return;

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedExchangeInfo != null &&
                (DateTime.UtcNow - _exchangeInfoTsUtc).TotalMinutes <= 30)
                return;

            using var client = _factory.CreateRestClient();
            var res = await client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(ct: ct).ConfigureAwait(false);

            if (!res.Success || res.Data == null)
            {
                if (_cachedExchangeInfo != null)
                {
                    _logger.LogWarning("[FILTER] ExchangeInfo refresh failed, keeping cache. Err={err}", res.Error);
                    return;
                }

                throw new InvalidOperationException($"[FILTER] ExchangeInfo load failed → TRADING BLOCKED. Err={res.Error}");
            }

            _cachedExchangeInfo = res.Data;
            _exchangeInfoTsUtc = DateTime.UtcNow;

            _logger.LogInformation("[FILTER] ExchangeInfo cached (symbols={cnt})", _cachedExchangeInfo.Symbols?.Length ?? 0);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    // --------------------------
    // JSON readers (robust to lib changes)
    // --------------------------
    private static string? ReadString(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var p))
            {
                if (p.ValueKind == JsonValueKind.String) return p.GetString();
                return p.ToString();
            }
        }
        return null;
    }

    private static decimal ReadDecimal(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return 0m;

        foreach (var n in names)
        {
            if (!el.TryGetProperty(n, out var p)) continue;

            if (p.ValueKind == JsonValueKind.Number)
            {
                if (p.TryGetDecimal(out var d)) return d;
                if (p.TryGetDouble(out var db)) return (decimal)db;
            }

            if (p.ValueKind == JsonValueKind.String &&
                decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds))
                return ds;
        }

        return 0m;
    }
}
