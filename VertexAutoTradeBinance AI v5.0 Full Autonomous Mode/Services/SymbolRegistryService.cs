using Binance.Net.Clients;
using Microsoft.Extensions.Configuration;

namespace VertexAutoTradeBinance8.Services;

public class SymbolRegistryService
{
    private readonly IConfiguration _cfg;
    private readonly BinanceClientFactory _factory;
    private readonly ILogger<SymbolRegistryService> _logger;

    public IReadOnlyList<string> ActiveSymbols { get; private set; } = new List<string>();
    private readonly TimeSpan _refreshInterval;

    public SymbolRegistryService(
        IConfiguration cfg,
        BinanceClientFactory factory,
        ILogger<SymbolRegistryService> logger)
    {
        _cfg = cfg;
        _factory = factory;
        _logger = logger;

        _refreshInterval = TimeSpan.FromMinutes(
            _cfg.GetValue<int>("SymbolSelection:Auto:RefreshInterval"));
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        var mode = _cfg["SymbolSelection:Mode"] ?? "Manual";

        if (mode == "Manual")
        {
            ActiveSymbols = GetPinnedSymbols();
            _logger.LogInformation("[SYMBOLS] Manual → {list}", string.Join(", ", ActiveSymbols));
            return;
        }

        // 🔁 Auto refresh loop
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await LoadAutoAsync();
                await Task.Delay(_refreshInterval, ct);
            }
        }, ct);

        _logger.LogInformation("[SYMBOLS] Auto mode started (non-blocking)");
    }

    // ------------------------------------------------------------------

    private async Task LoadAutoAsync()
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
                _logger.LogError("[SYMBOLS] Failed to load futures tickers");
                return;
            }

            var auto = tickers.Data
                .Where(t => t.LastPrice >= minPrice)
                .Where(t => t.QuoteVolume >= minVolume)
                .OrderByDescending(t => t.QuoteVolume)
                .Take(take)
                .Select(t => t.Symbol)
                .Where(s => s != "AIAUSDT") // 🚫 навсегда
                .ToList();

            var pinned = GetPinnedSymbols();

            ActiveSymbols = auto
                .Union(pinned)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _logger.LogInformation(
                "[SYMBOLS] Auto={autoCnt}, Pinned={pinCnt}, Total={total} → {list}",
                auto.Count,
                pinned.Count,
                ActiveSymbols.Count,
                string.Join(", ", ActiveSymbols));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SYMBOLS] Auto load error");
        }
    }

    private List<string> GetPinnedSymbols()
    {
        return _cfg
            .GetSection("SymbolSelection:Pinned")
            .Get<string[]>()?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.ToUpperInvariant())
            .ToList()
            ?? new();
    }
}
