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

        // CRITICAL FIX: this loop previously only ever bootstrapped
        // KlineInterval.OneMinute via REST — but the strategy actually
        // trades on FiveMinutes/FifteenMinutes (see StrategyEngine's
        // own tracked timeframes). Those two were relying ENTIRELY on
        // whatever the restored file buffer (RestoreAsync above)
        // happened to contain, with zero REST fallback if that file
        // was empty, missing, or simply didn't have enough bars yet —
        // meaning a fresh start (or any start where the snapshot file
        // wasn't current) had to accumulate 40-60+ bars purely from
        // live WebSocket ticks before most signal-generation checks
        // would pass at all. On 5-minute bars, 60 bars = 5 hours of
        // real wall-clock time from a cold buffer; on 15-minute bars,
        // 15 hours. This is the direct, precise explanation for the
        // reported "no signals for 2-3 hours after restart" — REST
        // bootstrap now explicitly covers every timeframe the strategy
        // actually needs, not just the one nothing trades on directly.
        var tfsToBootstrap = new[]
        {
            KlineInterval.OneMinute,
            KlineInterval.FiveMinutes,
            KlineInterval.FifteenMinutes,
        };

        foreach (var symbol in pinned)
        {
            if (ct.IsCancellationRequested)
                break;

            foreach (var tf in tfsToBootstrap)
            {
                if (ct.IsCancellationRequested)
                    break;

                // если уже есть данные — REST не нужен
                if (_buffer.Count(symbol, tf) >= 60)
                    continue;

                try
                {
                    var klines = await _market.GetKlines(symbol, tf, 200);

                    foreach (var k in klines)
                        _buffer.Upsert(symbol, tf, k);

                    _logger.LogInformation(
                        "[BOOT] {symbol} {tf} klines bootstrapped: {count}",
                        symbol, tf, klines.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[BOOT] {symbol} {tf} bootstrap failed", symbol, tf);
                }

                await Task.Delay(200, ct);
            }
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
