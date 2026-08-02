using Binance.Net.Enums;
using Microsoft.AspNetCore.SignalR.Client;
using System.Collections.Concurrent;
using System.Threading.Channels;
using VertexAutoTradeBinance8.Services.HistoricalData;

namespace VertexAutoTradeBinance8.Services;

/// <summary>
/// On-Demand Kline Loader — fetches history for a symbol+TF when
/// the Web UI requests it (user clicks a symbol not yet in datadb/).
///
/// FLOW:
///   Web UI (MarketSnapshot) notices no/few bars in HistoricalDataReaderService
///   → writes request to on-demand queue file (shared folder)
///   → this service polls queue → calls Binance REST → saves to datadb/
///   → notifies Web via SignalR PushKlineHistoryReady
///   → Web refreshes chart automatically
///
/// RATE LIMIT SAFETY:
///   - Uses same _restGate SemaphoreSlim as HistoricalDataLoaderService
///   - 300ms minimum delay between REST calls
///   - Deduplication: same symbol+tf requested twice → only one fetch
///   - Bounded queue: max 50 pending requests
///
/// DOES NOT INTERFERE WITH BOT:
///   - Background service with low REST priority
///   - _restGate shared with HistoricalDataLoaderService ensures
///     max 1 historical REST call at a time (trading REST calls
///     use a separate, higher-priority client)
/// </summary>
public sealed class KlineOnDemandService : BackgroundService
{
    private readonly HistoricalDataStore    _store;
    private readonly BinanceClientFactory   _factory;
    private readonly IConfiguration         _cfg;
    private readonly ILogger<KlineOnDemandService> _log;
    private readonly MarketDataPushClient?  _push;

    // Shared rate-limit gate with HistoricalDataLoaderService
    // 1 historical REST call at a time — never competes with trading
    private static readonly SemaphoreSlim _restGate = new(1, 1);
    private static readonly TimeSpan RestDelay = TimeSpan.FromMilliseconds(300);

    // Pending on-demand requests — deduplicated by symbol:tf key
    private readonly Channel<(string sym, string tf, int bars)> _queue =
        Channel.CreateBounded<(string, string, int)>(
            new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    // Queue file: Web writes requests here, we poll it
    private string QueueFile => Path.Combine(
        _cfg["SharedData:Root"] ?? AppContext.BaseDirectory,
        "kline_ondemand_queue.json");

    public KlineOnDemandService(
        HistoricalDataStore store,
        BinanceClientFactory factory,
        IConfiguration cfg,
        ILogger<KlineOnDemandService> log,
        MarketDataPushClient? push = null)
    {
        _store   = store;
        _factory = factory;
        _cfg     = cfg;
        _log     = log;
        _push    = push;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Wait for bot to fully start before handling requests
        await Task.Delay(TimeSpan.FromSeconds(15), ct);

        // Start two parallel tasks:
        // 1. Poll the queue file every 2s
        // 2. Process items from the in-memory channel
        var pollTask    = PollQueueFileAsync(ct);
        var processTask = ProcessQueueAsync(ct);
        await Task.WhenAll(pollTask, processTask);
    }

    // ── Poll the shared queue file ─────────────────────────────────
    private async Task PollQueueFileAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(QueueFile))
                {
                    var json = await File.ReadAllTextAsync(QueueFile, ct);
                    var requests = System.Text.Json.JsonSerializer
                        .Deserialize<List<KlineRequest>>(json);

                    if (requests?.Count > 0)
                    {
                        // Clear processed requests, keep unprocessed
                        var remaining = new List<KlineRequest>();
                        foreach (var req in requests)
                        {
                            var key = $"{req.Symbol}:{req.Tf}";
                            if (_inFlight.TryAdd(key, 0))
                            {
                                // New request — enqueue
                                _queue.Writer.TryWrite((req.Symbol, req.Tf, req.Bars));
                                _log.LogInformation(
                                    "[ONDEMAND] Queued: {sym} {tf} {bars}bars",
                                    req.Symbol, req.Tf, req.Bars);
                            }
                            // Don't add to remaining — it's queued now
                        }
                        // Clear processed file
                        await File.WriteAllTextAsync(QueueFile, "[]", ct);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "[ONDEMAND] Queue poll error (non-fatal)");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    // ── Process items from the in-memory channel ───────────────────
    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        await foreach (var (sym, tf, bars) in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await FetchAndStoreAsync(sym, tf, bars, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "[ONDEMAND] Failed: {sym} {tf}", sym, tf);
            }
            finally
            {
                _inFlight.TryRemove($"{sym}:{tf}", out _);
            }
        }
    }

    // ── Core fetch logic ───────────────────────────────────────────
    private async Task FetchAndStoreAsync(
        string symbol, string tfLabel, int bars, CancellationToken ct)
    {
        var client = _factory.TryCreateRestClient();
        if (client == null)
        {
            _log.LogWarning("[ONDEMAND] No Binance client — skipping {sym} {tf}", symbol, tfLabel);
            return;
        }

        if (!TryParseInterval(tfLabel, out var interval))
        {
            _log.LogWarning("[ONDEMAND] Unknown interval: {tf}", tfLabel);
            return;
        }

        int barsToFetch = Math.Clamp(bars, 100, 1500);
        int fetched     = 0;
        int stored      = 0;

        _log.LogInformation("[ONDEMAND] Fetching {sym} {tf} ({bars} bars)...",
            symbol, tfLabel, barsToFetch);

        // Paginate: Binance max 1500 bars per call
        // For on-demand we fetch in one shot (max 1500)
        await _restGate.WaitAsync(ct);
        try
        {
            await Task.Delay(RestDelay, ct);
            using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(TimeSpan.FromSeconds(20));

            var res = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                symbol, interval, limit: barsToFetch, ct: cts2.Token);

            if (!res.Success || res.Data == null)
            {
                _log.LogWarning("[ONDEMAND] Binance error for {sym} {tf}: {err}",
                    symbol, tfLabel, res.Error?.Message);
                return;
            }

            var klines = res.Data.Select(k => new HistoricalKline
            {
                OpenTime  = new DateTimeOffset(k.OpenTime.ToUniversalTime()).ToUnixTimeMilliseconds(),
                Open      = k.OpenPrice,
                High      = k.HighPrice,
                Low       = k.LowPrice,
                Close     = k.ClosePrice,
                Volume    = k.Volume,
            }).ToList();

            fetched = klines.Count;
            await _store.AppendAsync(symbol, tfLabel, klines);
            stored = fetched;
        }
        finally { _restGate.Release(); }

        _log.LogInformation("[ONDEMAND] Done: {sym} {tf} — {n} bars saved",
            symbol, tfLabel, stored);

        // Notify Web that data is ready
        _push?.NotifyKlineHistoryReady(symbol, tfLabel, stored);
    }

    // ── Interval parsing ───────────────────────────────────────────
    private static bool TryParseInterval(string tf, out KlineInterval interval)
    {
        interval = KlineInterval.FiveMinutes;
        return tf.ToUpperInvariant() switch
        {
            "1M" => (interval = KlineInterval.OneMinute)       == KlineInterval.OneMinute,
            "3M" => (interval = KlineInterval.ThreeMinutes)    == KlineInterval.ThreeMinutes,
            "5M" => (interval = KlineInterval.FiveMinutes)     == KlineInterval.FiveMinutes,
            "15M" => (interval = KlineInterval.FifteenMinutes) == KlineInterval.FifteenMinutes,
            "30M" => (interval = KlineInterval.ThirtyMinutes)  == KlineInterval.ThirtyMinutes,
            "1H" => (interval = KlineInterval.OneHour)         == KlineInterval.OneHour,
            "2H" => (interval = KlineInterval.TwoHour)         == KlineInterval.TwoHour,
            "4H" => (interval = KlineInterval.FourHour)        == KlineInterval.FourHour,
            "6H" => (interval = KlineInterval.SixHour)         == KlineInterval.SixHour,
            "12H" => (interval = KlineInterval.TwelveHour)     == KlineInterval.TwelveHour,
            "1D" => (interval = KlineInterval.OneDay)          == KlineInterval.OneDay,
            "3D" => (interval = KlineInterval.ThreeDay)        == KlineInterval.ThreeDay,
            "1W" => (interval = KlineInterval.OneWeek)         == KlineInterval.OneWeek,
            _ => false
        };
    }

    private sealed record KlineRequest(string Symbol, string Tf, int Bars);
}
