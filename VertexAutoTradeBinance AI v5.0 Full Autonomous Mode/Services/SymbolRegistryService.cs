using Binance.Net.Clients;
using Binance.Net.Enums;
using Microsoft.Extensions.Configuration;

namespace VertexAutoTradeBinance8.Services;

public class SymbolRegistryService
{
    private readonly IConfiguration _cfg;
    private readonly BinanceClientFactory _factory;
    private readonly ILogger<SymbolRegistryService> _logger;

    public IReadOnlyList<string> ActiveSymbols { get; private set; } = new List<string>();
    private readonly TimeSpan RefreshInterval;// = TimeSpan.FromMinutes(10); // Интервал в 10 минут

    public SymbolRegistryService(
        IConfiguration cfg,
        BinanceClientFactory factory,
        ILogger<SymbolRegistryService> logger)
    {
        _cfg = cfg;
        _factory = factory;
        _logger = logger;
        RefreshInterval = TimeSpan.FromMinutes(_cfg.GetValue<int>("SymbolSelection:Auto:RefreshInterval")); // читаем интервал из конфигурации
    }

    public async Task LoadAsync(CancellationToken stoppingToken)
    {
        string mode = _cfg["SymbolSelection:Mode"] ?? "Manual";

        if (mode == "Manual")
        {
            ActiveSymbols = _cfg.GetSection("SymbolSelection:Manual").Get<string[]>()!;
            _logger.LogInformation($"[SYMBOLS] Manual mode → {string.Join(", ", ActiveSymbols)}");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Автоматическое обновление списка символов...");

            await LoadAuto();
            // Ожидаем 10 минут перед следующим обновлением
            await Task.Delay(RefreshInterval, stoppingToken);
        }

      
    }

    private async Task LoadAuto()
    {
        var minVolume = _cfg.GetValue<decimal>("SymbolSelection:Auto:Min24hVolume");
        var minPrice = _cfg.GetValue<decimal>("SymbolSelection:Auto:MinPrice");
        var take = _cfg.GetValue<int>("SymbolSelection:Auto:TopVolumeCount");

        using var client = _factory.CreateRestClient();

        try
        {
            var tickers = await client.UsdFuturesApi.ExchangeData.GetTickersAsync();

            if (!tickers.Success || tickers.Data == null)
            {
                _logger.LogError("Failed to load futures tickers");
                return;
            }

            ActiveSymbols = tickers.Data
                .Where(t => t.LastPrice >= minPrice)
                .Where(t => t.QuoteVolume >= minVolume)
                .OrderByDescending(t => t.QuoteVolume)
                .Take(take)
                .Select(t => t.Symbol)
                .Where(s => s != "AIAUSDT")         // твой вечный бан
                .ToList();

            _logger.LogInformation(
                $"[SYMBOLS] Auto mode → {ActiveSymbols.Count} монет: {string.Join(", ", ActiveSymbols)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при загрузке тикеров с Binance.");
        }
    }
}
