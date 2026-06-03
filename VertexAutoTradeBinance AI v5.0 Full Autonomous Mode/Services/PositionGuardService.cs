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
    /// Возвращает +1 (Long), -1 (Short), 0 (нет позиции).
    /// Передаём symbol явно — запрашиваем только один символ,
    /// а не все позиции аккаунта (экономия rate limit weight).
    /// </summary>
    public async Task<int> GetCurrentPositionSideAsync(string symbol)
    {
        using var client = _factory.CreateRestClient();

        // ✅ Передаём symbol — Binance вернёт только эту пару (weight=1 вместо weight=5)
        var result = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol: symbol);
        if (!result.Success || result.Data == null)
        {
            _logger.LogWarning("[POSGUARD] Failed to get position for {symbol}: {err}", symbol, result.Error);
            return 0;
        }

        // В Hedge mode может быть Long + Short одновременно — берём суммарный net
        decimal netQty = result.Data
            .Where(p => p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
            .Sum(p => p.Quantity);

        if (netQty > 0)  return  1;   // LONG
        if (netQty < 0)  return -1;   // SHORT
        return 0;
    }

    /// <summary>
    /// Блокирует новые входы, если позиция уже открыта,
    /// но разрешает повторный вход ТОЛЬКО когда superSignal = true.
    /// </summary>
    public async Task<bool> ShouldBlockAsync(string symbol, bool superSignal)
    {
        int side = await GetCurrentPositionSideAsync(symbol);

        if (side == 0)    return false; // позиции нет → можно входить
        if (superSignal)  return false; // супер-сигнал → разрешаем повторный вход

        return true; // блокируем — позиция уже открыта
    }
}
