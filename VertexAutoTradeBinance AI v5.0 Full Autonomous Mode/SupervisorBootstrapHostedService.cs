using Binance.Net.Enums;
using VertexAutoTradeBinance8.MarketData;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.Bootstrap;

public sealed class SupervisorBootstrapHostedService : BackgroundService
{
    private readonly SymbolRegistryService _symbols;
    private readonly MarketDataService _market;
    private readonly MarketDataKlineBuffer _buffer;
    private readonly KlineBufferPersistence _persistence;
    private readonly ILogger<SupervisorBootstrapHostedService> _logger;
    private readonly IBootGate _bootGate;

    public SupervisorBootstrapHostedService(
     SymbolRegistryService symbols,
     MarketDataService market,
     MarketDataKlineBuffer buffer,
     KlineBufferPersistence persistence,
     IBootGate bootGate,
     ILogger<SupervisorBootstrapHostedService> logger)
    {
        _symbols = symbols;
        _market = market;
        _buffer = buffer;
        _persistence = persistence;
        _bootGate = bootGate;
        _logger = logger;
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

            _bootGate.MarkReady();
            _logger.LogWarning("[BOOT] BootGate READY (no pinned)");

            return;
        }

        foreach (var symbol in pinned)
        {
            if (ct.IsCancellationRequested)
                break;

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

        _logger.LogWarning("[BOOT] Supervisor bootstrap finished OK.");
        _bootGate.MarkReady();
        _logger.LogWarning("[BOOT] BootGate READY.");


    }
    public override async Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("[BOOT] Saving kline buffer on shutdown");
        await _persistence.SaveAsync(ct);
        await base.StopAsync(ct);
    }
}
