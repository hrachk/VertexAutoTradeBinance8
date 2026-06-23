using Binance.Net.Enums;
using VertexAutoTradeBinance8.MarketData;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.Bootstrap;
using VertexAutoTradeBinance8.Services.HistoricalData;

public sealed class SupervisorBootstrapHostedService : BackgroundService
{
    private readonly SymbolRegistryService _symbols;
    private readonly MarketDataService _market;
    private readonly MarketDataKlineBuffer _buffer;
    private readonly KlineBufferPersistence _persistence;
    private readonly HistoricalDataStore _historicalStore;
    private readonly ILogger<SupervisorBootstrapHostedService> _logger;
    private readonly IBootGate _bootGate;

    public SupervisorBootstrapHostedService(
     SymbolRegistryService symbols,
     MarketDataService market,
     MarketDataKlineBuffer buffer,
     KlineBufferPersistence persistence,
     HistoricalDataStore historicalStore,
     IBootGate bootGate,
     ILogger<SupervisorBootstrapHostedService> logger)
    {
        _symbols = symbols;
        _market = market;
        _buffer = buffer;
        _persistence = persistence;
        _historicalStore = historicalStore;
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

                // CRITICAL FIX (per direct user feedback): check the
                // datadb/ archive BEFORE falling back to REST. The whole
                // point of building that archive was wasted if startup
                // still always waits on the network for data it might
                // already have sitting on disk. A local file read is
                // effectively instant compared to a REST round-trip —
                // if the archive already has enough bars, load them
                // straight into the live trading buffer and skip the
                // network call for this symbol+timeframe entirely.
                var tfLabel = TfToArchiveLabel(tf);
                if (tfLabel != null && _historicalStore.Has(symbol, tfLabel))
                {
                    try
                    {
                        var archived = await _historicalStore.LoadLastAsync(symbol, tfLabel, 200, ct);
                        if (archived.Count >= 60)
                        {
                            foreach (var k in archived)
                            {
                                _buffer.Upsert(symbol, tf, new Binance.Net.Objects.Models.Futures.BinanceFuturesUsdtKline
                                {
                                    OpenTime = DateTimeOffset.FromUnixTimeMilliseconds(k.OpenTime).UtcDateTime,
                                    OpenPrice = k.Open,
                                    HighPrice = k.High,
                                    LowPrice = k.Low,
                                    ClosePrice = k.Close,
                                    Volume = k.Volume,
                                });
                            }

                            // CRITICAL: the archive is only as fresh as the
                            // loader's last cycle (up to ~5 minutes old by
                            // default) — loading it alone and declaring this
                            // symbol+timeframe "ready" would mean the
                            // strategy could start analyzing on a gap of
                            // stale bars, producing exactly the kind of
                            // false signal risk flagged directly by the
                            // user: indicators (EMA/ATR/trend) computed
                            // against data that doesn't yet reflect the
                            // real current price. So: ALWAYS close that gap
                            // explicitly with one more REST call right here,
                            // asking only for bars from the archive's last
                            // timestamp forward to now — cheap (a handful of
                            // bars, not the full 200 again) precisely
                            // because the archive already did the expensive
                            // part. Only once this gap-fill completes is the
                            // symbol+timeframe genuinely caught up to the
                            // present moment.
                            var lastArchivedTime = DateTimeOffset.FromUnixTimeMilliseconds(archived[^1].OpenTime).UtcDateTime;
                            try
                            {
                                var freshKlines = await _market.GetKlines(symbol, tf, 50);
                                int gapFilled = 0;
                                foreach (var k in freshKlines)
                                {
                                    if (k.OpenTime > lastArchivedTime)
                                    {
                                        _buffer.Upsert(symbol, tf, k);
                                        gapFilled++;
                                    }
                                }

                                _logger.LogInformation(
                                    "[BOOT] {symbol} {tf} warm-started from archive ({archCount} bars) + live gap-fill ({gapCount} fresh bars since {lastTime:HH:mm:ss}) — current as of now",
                                    symbol, tf, archived.Count, gapFilled, lastArchivedTime);
                            }
                            catch (Exception gapEx)
                            {
                                // Archive data is loaded, but we couldn't
                                // confirm it's current — do NOT silently
                                // trust it. Fall through to the normal REST
                                // bootstrap below instead of skipping it,
                                // so this symbol+timeframe still ends up
                                // genuinely fresh rather than possibly stale.
                                _logger.LogWarning(gapEx,
                                    "[BOOT] {symbol} {tf} archive loaded but gap-fill failed — falling through to full REST bootstrap to guarantee freshness",
                                    symbol, tf);
                                goto archiveFallthrough;
                            }

                            continue; // genuinely caught up — skip the full REST bootstrap below
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[BOOT] {symbol} {tf} archive read failed — falling back to REST", symbol, tf);
                    }
                }

                archiveFallthrough:
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

    // Maps the strategy's KlineInterval enum to the same lowercase string
    // labels HistoricalDataLoaderService uses for archive filenames
    // (datadb/SYMBOL/TF.json) — only the timeframes this bootstrap loop
    // actually checks (1m/5m/15m) need an entry; returns null for
    // anything else rather than guessing.
    private static string? TfToArchiveLabel(KlineInterval tf) => tf switch
    {
        KlineInterval.OneMinute => "1m",
        KlineInterval.FiveMinutes => "5m",
        KlineInterval.FifteenMinutes => "15m",
        _ => null
    };
}
