using System.Collections.Concurrent;
using Binance.Net.Objects.Models.Spot;

namespace VertexAutoTradeBinance8.Services;

/// <summary>
/// Биржевые фильтры символа (step/minQty/minNotional/tick).
/// FIX: раньше на КАЖДЫЙ вызов скачивался весь exchangeInfo (~1–2 МБ) — десятки раз за цикл.
/// Теперь кэш на 30 минут + одна загрузка для всех символов.
/// </summary>
public class SymbolInfoService
{
    private readonly ILogger<SymbolInfoService> _logger;
    private readonly BinanceClientFactory _factory;

    private readonly ConcurrentDictionary<string, (decimal step, decimal minQty, decimal minNotional, decimal tickSize)> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTime _loadedAt = DateTime.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    public SymbolInfoService(ILogger<SymbolInfoService> logger, BinanceClientFactory factory)
    {
        _logger = logger;
        _factory = factory;
    }

    public async Task<(decimal step, decimal minQty, decimal minNotional, decimal tickSize)>
        GetFuturesFiltersAsync(string symbol)
    {
        if (DateTime.UtcNow - _loadedAt > Ttl || !_cache.ContainsKey(symbol))
            await RefreshAsync(force: DateTime.UtcNow - _loadedAt > Ttl);

        return _cache.TryGetValue(symbol, out var f) ? f : (0, 0, 0, 0);
    }

    public async Task<decimal> GetTickSizeAsync(string symbol)
    {
        var (_, _, _, tick) = await GetFuturesFiltersAsync(symbol);
        return tick;
    }

    private async Task RefreshAsync(bool force)
    {
        await _gate.WaitAsync();
        try
        {
            if (!force && _cache.Count > 0 && DateTime.UtcNow - _loadedAt < TimeSpan.FromSeconds(30))
                return; // недавно обновляли — символа просто нет на бирже

            var client = _factory.CreateRestClient();
            var info = await client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();
            if (!info.Success || info.Data == null)
            {
                _logger.LogError("Failed to load exchange info: {err}", info.Error);
                return;
            }

            foreach (var sym in info.Data.Symbols)
            {
                decimal step = 0, minQty = 0, minNotional = 0, tick = 0;
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
                            tick = pf.TickSize;
                            break;
                    }
                }
                if (tick <= 0) tick = 0.0001m;
                _cache[sym.Name] = (step, minQty, minNotional, tick);
            }

            _loadedAt = DateTime.UtcNow;
            _logger.LogInformation("[FILTERS] exchangeInfo cached for {n} symbols", _cache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FILTERS] exchangeInfo refresh failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------- rounding helpers (общие для всех сервисов) ----------------

    public static decimal RoundToTick(decimal price, decimal tick)
        => tick <= 0 ? price : Math.Round(price / tick, MidpointRounding.AwayFromZero) * tick;

    public static decimal FloorToStep(decimal qty, decimal step)
        => step <= 0 ? qty : Math.Floor(qty / step) * step;
}
