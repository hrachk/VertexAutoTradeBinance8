using Binance.Net.Clients;
using Binance.Net.Enums;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services;

public class PositionGuardService
{
    private readonly ILogger<PositionGuardService> _logger;
    private readonly BinanceClientFactory _factory;

    public PositionGuardService(
        ILogger<PositionGuardService> logger,
        BinanceClientFactory factory)
    {
        _logger = logger;
        _factory = factory;
    }

    /// <summary>
    /// Проверяет: есть ли активная позиция по символу.
    /// Возвращает +1 (Long), -1 (Short), 0 (нет позиции)
    /// </summary>
    public async Task<int> GetCurrentPositionSideAsync(string symbol)
    {
        using var client = _factory.CreateRestClient();

        var result = await client.UsdFuturesApi.Account.GetPositionInformationAsync();
        if (!result.Success || result.Data == null)
            return 0; // нет позиции

        var pos = result.Data.FirstOrDefault(x => x.Symbol == symbol);
        if (pos == null)
            return 0;

        if (pos.Quantity > 0)
            return 1;   // LONG

        if (pos.Quantity < 0)
            return -1;  // SHORT

        return 0;
    }


    /// <summary>
    /// Блокирует новые входы, если позиция уже открыта,
    /// но разрешает повторный вход ТОЛЬКО когда superSignal = true.
    /// </summary>
    public async Task<bool> ShouldBlockAsync(string symbol, bool superSignal)
    {
        int side = await GetCurrentPositionSideAsync(symbol);

        if (side == 0)
            return false; // позиции нет → можно входить

        if (superSignal)
            return false; // супер-сигнал → разрешаем повторный вход

        return true; // блокируем — позиция уже открыта
    }

}
