using System.Collections.Concurrent;
using System.Globalization;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.MarketData;
using VertexAutoTradeBinance8.Services.MarketState;

namespace VertexAutoTradeBinance8.Services
{
    public sealed class MarketDataFacade
    {
        private readonly MarketDataKlineBuffer _buf;
        private readonly MarketStateService _marketState;
        private readonly WsKlineSubscriber _ws;
        private readonly BinanceClientFactory _factory;
        private readonly ILogger<MarketDataFacade> _logger;

        // REST control
        private readonly ConcurrentDictionary<string, DateTime> _lastRestFetchUtc = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _restBackfilled = new(StringComparer.OrdinalIgnoreCase);

        // Global REST limiter (hard cap)
        private static readonly SemaphoreSlim _globalRestLimiter = new(3, 3);

        // Warmup accounting (used as "availability bars" — snapshot counts too)
        private readonly ConcurrentDictionary<string, int> _barsAvailable = new(StringComparer.OrdinalIgnoreCase);

        // Snapshot flags:
        //  - _restoreAttempted: RestoreSnapshotStateAsync already executed (cold-start allowed)
        //  - _readyBySnapshot: snapshot existed/restored => REST backfill disabled (your policy)
        private volatile bool _restoreAttempted;
        private volatile bool _readyBySnapshot;

        public bool HasSnapshotState => _restoreAttempted;   // semantic: restore attempt completed
        public bool ReadyBySnapshot => _readyBySnapshot;     // semantic: snapshot exists => rest backfill disabled

        public event Action<string, KlineInterval>? OnWarm;
        public event Action<string, KlineInterval, BinanceFuturesUsdtKline>? WsClosedKline;

        private const int WarmBars = 20;
        private const int FastWarmBars = 10;
        private static readonly TimeSpan RestCooldown = TimeSpan.FromMinutes(1);

        // === UNIVERSE SUBSCRIPTIONS ===
        private readonly object _universeLock = new();
        private HashSet<string> _universe = new(StringComparer.OrdinalIgnoreCase);

        // WS subscribe singleflight
        private readonly ConcurrentDictionary<string, Task> _subTasks = new(StringComparer.OrdinalIgnoreCase);

        // REST singleflight (per symbol+tf)
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _restLocks = new(StringComparer.OrdinalIgnoreCase);

        public MarketDataFacade(
            MarketDataKlineBuffer buffer,
            WsKlineSubscriber ws,
            BinanceClientFactory factory,
            ILogger<MarketDataFacade> logger,
            MarketStateService marketState)
        {
            _buf = buffer;
            _ws = ws;
            _factory = factory;
            _logger = logger;
            _marketState = marketState;

            _ws.OnClosedKline += OnWsClosedKline;

            // HARD LINK: MarketState → MarketData
            // If marketState says restored, we consider snapshot authoritative (REST disabled)
            if (_marketState.IsRestored)
                MarkSnapshotReady();

            _marketState.OnRestored += MarkSnapshotReady;
        }

        // =====================================================
        // SNAPSHOT READY (authoritative snapshot restored)
        // =====================================================
        public void MarkSnapshotReady()
        {
            if (_readyBySnapshot)
                return;

            _restoreAttempted = true;
            _readyBySnapshot = true;

            _logger.LogWarning("[MD][STATE] SNAPSHOT READY → REST backfill DISABLED, warmup accelerated");
        }

        // =====================================================
        // APPLY UNIVERSE (idempotent, fire-and-forget subscribe)
        // =====================================================
        public void ApplyUniverse(IReadOnlyList<string> symbols)
        {
            if (symbols == null || symbols.Count == 0)
                return;

            var target = new HashSet<string>(
                symbols
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => NormalizeSymbol(s)),
                StringComparer.OrdinalIgnoreCase);

            lock (_universeLock)
            {
                foreach (var sym in target)
                {
                    // Keep WS subscriptions alive (best-effort)
                    _ = EnsureWsSubscribed(sym, KlineInterval.OneMinute, CancellationToken.None);
                    _ = EnsureWsSubscribed(sym, KlineInterval.FiveMinutes, CancellationToken.None);
                    _ = EnsureWsSubscribed(sym, KlineInterval.FifteenMinutes, CancellationToken.None);
                }

                _universe = target;
            }
        }

        // =====================================================
        // SNAPSHOT RESTORE (SAFE, production)
        // - if snapshot found: mark ReadyBySnapshot=true (REST disabled)
        // - if not found: mark restoreAttempted=true but ReadyBySnapshot=false (REST allowed)
        // =====================================================
        public async Task RestoreSnapshotStateAsync(CancellationToken ct)
        {
            try
            {
                _logger.LogWarning("[MD][RESTORE] Restoring market snapshot state...");

                var snapshot = _buf.LoadSnapshot();
                if (snapshot == null || snapshot.Count == 0)
                {
                    // cold start, but restore attempt is done => allow REST fallback
                    _restoreAttempted = true;
                    _readyBySnapshot = false;

                    _logger.LogWarning("[MD][RESTORE] No snapshot found → COLD START (REST fallback ENABLED)");
                    return;
                }

                bool restoredAny = false;

                foreach (var (rawKey, candles) in snapshot)
                {
                    ct.ThrowIfCancellationRequested();

                    if (candles == null || candles.Count == 0)
                        continue;

                    // expected key: "SYMBOL:Timeframe"
                    var parts = rawKey.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length != 2)
                        continue;

                    if (!Enum.TryParse<KlineInterval>(parts[1], ignoreCase: true, out var tf))
                        continue;

                    var symbol = NormalizeSymbol(parts[0]);
                    var key = Key(symbol, tf);

                    foreach (var c in candles)
                        _buf.Upsert(symbol, tf, c);

                    // availability bars: snapshot contributes to warm readiness
                    _barsAvailable[key] = Math.Max(_barsAvailable.TryGetValue(key, out var prev) ? prev : 0, candles.Count);

                    // mark backfilled so we don't REST refetch for this key (policy)
                    _restBackfilled[key] = true;

                    restoredAny = true;

                    if (candles.Count >= FastWarmBars)
                        OnWarm?.Invoke(symbol, tf);

                    _logger.LogInformation("[MD][RESTORE] {symbol} {tf} restored bars={bars}", symbol, tf, candles.Count);
                }

                _restoreAttempted = true;

                if (restoredAny)
                {
                    _readyBySnapshot = true; // authoritative snapshot => REST disabled
                    _logger.LogWarning("[MD][RESTORE] Snapshot restored → READY (REST backfill DISABLED)");
                }
                else
                {
                    _readyBySnapshot = false; // snapshot file existed but no valid series => cold start behavior
                    _logger.LogWarning("[MD][RESTORE] Snapshot invalid/empty → COLD START (REST fallback ENABLED)");
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("[MD][RESTORE] Cancelled");
                throw;
            }
            catch (Exception ex)
            {
                // fail-safe: treat as cold-start but allow REST fallback (better than blindness)
                _restoreAttempted = true;
                _readyBySnapshot = false;

                _logger.LogError(ex, "[MD][RESTORE] Failed to restore snapshot → COLD START (REST fallback ENABLED)");
            }

            await Task.CompletedTask;
        }

        // =====================================================
        // WS EVENTS
        // =====================================================
        private void OnWsClosedKline(string symbol, KlineInterval tf, BinanceFuturesUsdtKline candle)
        {
            symbol = NormalizeSymbol(symbol);
            var key = Key(symbol, tf);

            // availability increments (ws closed bars)
            var count = _barsAvailable.AddOrUpdate(key, 1, (_, v) => v + 1);

            if (count == FastWarmBars)
            {
                _logger.LogInformation("[MD][WS] warm READY {symbol} {tf}", symbol, tf);
                OnWarm?.Invoke(symbol, tf);
            }

            WsClosedKline?.Invoke(symbol, tf, candle);
        }

        private static string NormalizeSymbol(string symbol) =>
            symbol.Trim().ToUpperInvariant();

        private static string Key(string symbol, KlineInterval tf) =>
            $"{NormalizeSymbol(symbol)}:{tf}";

        // =====================================================
        // MAIN API
        // =====================================================
        public async Task<IReadOnlyList<BinanceFuturesUsdtKline>> GetKlinesAsync(
            string symbol,
            KlineInterval tf,
            int need,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return Array.Empty<BinanceFuturesUsdtKline>();

            symbol = NormalizeSymbol(symbol);

            if (need <= 0)
                return Array.Empty<BinanceFuturesUsdtKline>();

            if (!_restoreAttempted)
            {
                // restore wasn't called yet -> best-effort WS only, but do NOT brick the system forever
                _logger.LogCritical("[MD][HARD-GUARD] GetKlinesAsync called BEFORE RestoreSnapshotStateAsync {symbol} {tf}", symbol, tf);
            }

            var key = Key(symbol, tf);

            // 1) WS subscribe (idempotent singleflight)
            await EnsureWsSubscribed(symbol, tf, ct).ConfigureAwait(false);

            // 2) return what we already have
            var ws = _buf.GetLast(symbol, tf, need);
            if (ws.Count >= need)
                return ws;

            // 3) REST policy:
            // - if snapshot is authoritative => REST backfill disabled
            // - if cold start => REST allowed under cooldown + singleflight + global limiter
            if (_readyBySnapshot)
                return ws;

            // If restore not attempted yet, allow REST too (cold start safety)
            // but still obey cooldown and locks
            var restLock = GetRestLock(key);

            if (!await restLock.WaitAsync(0, ct).ConfigureAwait(false))
                return ws;

            try
            {
                ws = _buf.GetLast(symbol, tf, need);
                if (ws.Count >= need)
                    return ws;

                if (_restBackfilled.ContainsKey(key))
                    return ws;

                if (!CanUseRest(key))
                    return ws;

                _lastRestFetchUtc[key] = DateTime.UtcNow;

                await _globalRestLimiter.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var restClient = _factory.CreateRestClient();

                    var rest = await restClient.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                        symbol: symbol,
                        interval: tf,
                        limit: need,
                        ct: ct).ConfigureAwait(false);

                    if (!rest.Success || rest.Data == null)
                        return ws;

                    foreach (var k in rest.Data)
                    {
                        var candle = new BinanceFuturesUsdtKline
                        {
                            OpenTime = k.OpenTime,
                            CloseTime = k.CloseTime,
                            OpenPrice = k.OpenPrice,
                            HighPrice = k.HighPrice,
                            LowPrice = k.LowPrice,
                            ClosePrice = k.ClosePrice,
                            Volume = k.Volume,
                            QuoteVolume = k.QuoteVolume,
                            TradeCount = k.TradeCount,
                            TakerBuyBaseVolume = k.TakerBuyBaseVolume,
                            TakerBuyQuoteVolume = k.TakerBuyQuoteVolume
                        };

                        _buf.Upsert(symbol, tf, candle);
                    }

                    _restBackfilled[key] = true;

                    // availability bars: after REST backfill, treat as warm enough
                    var got = _buf.GetLast(symbol, tf, need);
                    _barsAvailable[key] = Math.Max(_barsAvailable.TryGetValue(key, out var prev) ? prev : 0, got.Count);

                    if (tf is KlineInterval.OneHour or KlineInterval.FourHour or KlineInterval.OneDay or KlineInterval.OneWeek)
                    {
                        _logger.LogDebug(
                            "[MD][HTF] request {symbol} {tf} readyBySnapshot={ready}",
                            symbol, tf, _readyBySnapshot);
                    }
                     
                    return got;
                }
                finally
                {
                    _globalRestLimiter.Release();
                }
            }
            finally
            {
                restLock.Release();
            }
        }

        // =====================================================
        // WARMUP LOGIC
        // =====================================================
        public bool IsInWarmup(string symbol, KlineInterval tf)
        {
            symbol = NormalizeSymbol(symbol);
            var key = Key(symbol, tf);

            if (!_barsAvailable.TryGetValue(key, out var bars))
                return true;

            var required = _readyBySnapshot ? FastWarmBars : WarmBars;
            return bars < required;
        }

        private bool CanUseRest(string key)
        {
            if (!_lastRestFetchUtc.TryGetValue(key, out var last))
                return true;

            return DateTime.UtcNow - last > RestCooldown;
        }

        // =====================================================
        // WS SUBSCRIBE SINGLEFLIGHT (retry-safe)
        // =====================================================
        private Task EnsureWsSubscribed(string symbol, KlineInterval tf, CancellationToken ct)
        {
            var key = Key(symbol, tf);

            return _subTasks.GetOrAdd(key, _ =>
            {
                var task = SubscribeInternal(symbol, tf);

                // 🔁 IMPORTANT:
                // If subscription fails → remove task so future calls can retry
                task.ContinueWith(
                    _ => _subTasks.TryRemove(key, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                return task;
            });
        } 
        private async Task SubscribeInternal(string symbol, KlineInterval tf)
        {
            await _ws.SubscribeAsync(symbol, tf, CancellationToken.None)
                     .ConfigureAwait(false);

            _logger.LogInformation(
                "[MD][WS] subscribe started {symbol} {tf}",
                symbol, tf);
        }

        private SemaphoreSlim GetRestLock(string key) =>
            _restLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }
}
