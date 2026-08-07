using Binance.Net.Objects.Models.Spot;

namespace VertexAutoTradeBinance8.Services;

/// <summary>
/// FIX: раньше GetFuturesFiltersAsync на КАЖДЫЙ вызов дёргал GetExchangeInfoAsync()
/// (~2 МБ JSON по всем контрактам) и логировал результат. Метод вызывается на каждый
/// ордер, каждый стоп и каждый проход Supervisor'а по каждому символу — это заметная
/// доля weight-лимита и десятки мегабайт трафика в минуту.
/// Теперь: один запрос на все символы, кэш на 6 часов.
/// </summary>
public class SymbolInfoService
{
    private readonly ILogger<SymbolInfoService> _logger;
    private readonly BinanceClientFactory _factory;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Dictionary<string, (decimal step, decimal minQty, decimal minNotional, decimal tickSize)>? _cache;
    private DateTime _cacheUtc = DateTime.MinValue;

    public SymbolInfoService(ILogger<SymbolInfoService> logger, BinanceClientFactory factory)
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
        var cache = _cache;

        if (cache == null || DateTime.UtcNow - _cacheUtc > CacheTtl)
        {
            await _lock.WaitAsync();
            try
            {
                if (_cache == null || DateTime.UtcNow - _cacheUtc > CacheTtl)
                    await ReloadAsync();

                cache = _cache;
            }
            finally
            {
                _lock.Release();
            }
        }

        if (cache != null && cache.TryGetValue(symbol, out var f))
            return f;

        _logger.LogError("No symbol info for {Symbol}", symbol);
        return (0, 0, 0, 0.0001m);
    }

    private async Task ReloadAsync()
    {
        using var client = _factory.CreateRestClient();

        var exchangeInfo = await client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();

        if (!exchangeInfo.Success || exchangeInfo.Data == null)
        {
            _logger.LogError("Failed to load exchange info: {err}", exchangeInfo.Error);
            return; // старый кэш (если был) остаётся валидным
        }

        var map = new Dictionary<string, (decimal, decimal, decimal, decimal)>(StringComparer.OrdinalIgnoreCase);

        foreach (var sym in exchangeInfo.Data.Symbols)
        {
            decimal step = 0, minQty = 0, minNotional = 0, tickSize = 0;

            foreach (var f in sym.Filters)
            {
                switch (f)
                {
                    case BinanceSymbolLotSizeFilter lot:
                        step = lot.StepSize;
                        minQty = lot.MinQuantity;
                        break;

                    case BinanceSymbolMinNotionalFilter mn:
                        minNotional = mn.MinNotional;
                        break;

                    case BinanceSymbolPriceFilter pf:
                        tickSize = pf.TickSize;
                        break;
                }
            }

            if (tickSize == 0)
                tickSize = 0.0001m; // запасной вариант

            map[sym.Name] = (step, minQty, minNotional, tickSize);
        }

        _cache = map;
        _cacheUtc = DateTime.UtcNow;

        _logger.LogInformation("Exchange filters cached: {n} symbols", map.Count);
    }

    /// <summary>
    /// Получить только tickSize (для OrderExecutor)
    /// </summary>
    public async Task<decimal> GetTickSizeAsync(string symbol)
    {
        var (_, _, _, tick) = await GetFuturesFiltersAsync(symbol);
        return tick;
    }
}
