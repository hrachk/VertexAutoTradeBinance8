using Binance.Net.Objects.Models.Spot;

namespace VertexAutoTradeBinance8.Services;

public class SymbolInfoService
{
    private readonly ILogger<SymbolInfoService> _logger;
    private readonly BinanceClientFactory _factory;

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
        using var client = _factory.CreateRestClient();

        var exchangeInfo = await client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();

        if (!exchangeInfo.Success || exchangeInfo.Data == null)
        {
            _logger.LogError("Failed to load exchange info: {err}", exchangeInfo.Error);
            return (0, 0, 0, 0);
        }

        var sym = exchangeInfo.Data.Symbols.FirstOrDefault(s => s.Name == symbol);
        if (sym == null)
        {
            _logger.LogError("No symbol info for {Symbol}", symbol);
            return (0, 0, 0, 0);
        }

        decimal step = 0;
        decimal minQty = 0;
        decimal minNotional = 0;
        decimal tickSize = 0;

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

        _logger.LogInformation(
            "Filters {Symbol}: step={Step}, minQty={MinQty}, minNotional={MinNotional}, tickSize={Tick}",
            symbol, step, minQty, minNotional, tickSize
        );

        return (step, minQty, minNotional, tickSize);
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
