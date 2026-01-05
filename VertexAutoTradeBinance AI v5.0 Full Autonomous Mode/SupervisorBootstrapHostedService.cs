using Binance.Net.Enums;
using VertexAutoTradeBinance8.MarketData;
using VertexAutoTradeBinance8.Services;

public sealed class SupervisorBootstrapHostedService : BackgroundService
{
    private readonly SymbolRegistryService _symbols;
    private readonly MarketDataService _market;
    private readonly MarketDataKlineBuffer _buffer;
    private readonly KlineBufferPersistence _persistence;
    private readonly ILogger<SupervisorBootstrapHostedService> _logger;

    public SupervisorBootstrapHostedService(
        SymbolRegistryService symbols,
        MarketDataService market,
        MarketDataKlineBuffer buffer,
        ILogger<SupervisorBootstrapHostedService> logger ,
        KlineBufferPersistence persistence)
    {
        _symbols = symbols;
        _market = market;
        _buffer = buffer;
        _logger = logger;
        _persistence = persistence;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("[BOOT] REST bootstrap start");

        await _symbols.LoadAsync(ct);

        // 🔄 RESTORE FIRST
        await _persistence.RestoreAsync(ct);

        var pinned = _symbols.PinnedSymbols;
        if (pinned.Count == 0)
        {
            _logger.LogWarning("[BOOT] No pinned symbols — skip REST bootstrap");
            return;
        }

        foreach (var symbol in pinned)
        {
            // если уже есть данные — REST не нужен
            if (_buffer.Count(symbol, KlineInterval.OneMinute) >= 30)
                continue;

            try
            {
                var klines = await _market.GetKlines(
                    symbol,
                    KlineInterval.OneMinute,
                    50);

                foreach (var k in klines)
                    _buffer.Upsert(symbol, KlineInterval.OneMinute, k);

                _logger.LogInformation(
                    "[BOOT] {symbol} klines bootstrapped: {count}",
                    symbol, klines.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[BOOT] {symbol} bootstrap failed", symbol);
            }

            await Task.Delay(200, ct);
        }

        _logger.LogInformation("[BOOT] REST bootstrap done");
    }
    public override async Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("[BOOT] Saving kline buffer on shutdown");
        await _persistence.SaveAsync(ct);
        await base.StopAsync(ct);
    }
}
