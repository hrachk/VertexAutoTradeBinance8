using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using VertexAutoTradeBinance8.MarketData;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services.MarketState;

namespace VertexAutoTradeBinance8.Services
{
    public sealed class MarketDataFacade
    {
        private readonly MarketDataKlineBuffer _buf;
        private readonly MarketStateService _marketState;
        private readonly WsKlineSubscriber _ws;

        // Подключает RealtimeMomentumDetector к WS подписчику
        public void SetMomentumDetector(RealtimeMomentumDetector detector)
            => _ws.SetMomentumDetector(detector);

        // Синхронный доступ к кэшированным свечам (без API вызова)
        // Используется в RealtimeMomentumDetector для ATR расчёта
        public IReadOnlyList<BinanceFuturesUsdtKline>? GetCachedKlines(
            string symbol, KlineInterval interval)
        {
            var snap = _buf.Snapshot(symbol, interval);
            return snap.Count > 0 ? snap : null;
        }
        private readonly BinanceClientFactory _factory;
        private readonly ILogger<MarketDataFacade> _logger;

        // REST control
        private readonly ConcurrentDictionary<string, DateTime> _lastRestFetchUtc = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _restBackfilled = new(StringComparer.OrdinalIgnoreCase);

        // Global REST limiter (hard cap)
        private static readonly SemaphoreSlim _globalRestLimiter = new(3, 3);


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

        // Warmup accounting (used as "availability bars" — snapshot counts too)
        private readonly ConcurrentDictionary<string, int> _barsAvailable = new(StringComparer.OrdinalIgnoreCase);

        // WS subscribe singleflight
        private readonly ConcurrentDictionary<string, Task> _subTasks = new(StringComparer.OrdinalIgnoreCase);

        // REST singleflight (per symbol+tf)
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _restLocks = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, decimal> _lastPrice = new();


        public event Action<string, decimal>? RealtimePrice;

        public MarketDataFacade(
            MarketDataKlineBuffer buffer,
            WsKlineSubscriber ws,
            BinanceClientFactory factory,
            ILogger<MarketDataFacade> logger,
            MarketStateService marketState
             )
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

            _ws.OnPrice += (symbol, price) =>
            {
                UpdateRealtimePrice(symbol, price);

                RealtimePrice?.Invoke(symbol, price);
            };
            // ✅ START CLEANUP LOOP (fire and forget)
            _ = Task.Run(CleanupLoop);
        }

        public void CleanupUnavailableSymbols(IReadOnlyCollection<string> activeSymbols)
        {
            var active = new HashSet<string>(
                activeSymbols.Select(NormalizeSymbol),
                StringComparer.OrdinalIgnoreCase);

            foreach (var key in _barsAvailable.Keys)
            {
                var symbol = key.Split(':')[0];

                if (!active.Contains(symbol))
                    _barsAvailable.TryRemove(key, out _);
            }

            foreach (var key in _restBackfilled.Keys)
            {
                var symbol = key.Split(':')[0];

                if (!active.Contains(symbol))
                    _restBackfilled.TryRemove(key, out _);
            }

            foreach (var key in _lastRestFetchUtc.Keys)
            {
                var symbol = key.Split(':')[0];

                if (!active.Contains(symbol))
                    _lastRestFetchUtc.TryRemove(key, out _);
            }

            foreach (var key in _subTasks.Keys)
            {
                var symbol = key.Split(':')[0];

                if (!active.Contains(symbol))
                    _subTasks.TryRemove(key, out _);
            }

            foreach (var key in _restLocks.Keys)
            {
                var symbol = key.Split(':')[0];

                if (!active.Contains(symbol))
                    _restLocks.TryRemove(key, out _);
            }

            foreach (var key in _lastPrice.Keys)
            {
                if (!active.Contains(key))
                    _lastPrice.TryRemove(key, out _);
            }
        }
        private async Task CleanupLoop()
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(10));

                try
                {
                    HashSet<string> universe;

                    lock (_universeLock)
                        universe = new HashSet<string>(_universe);

                    CleanupUnavailableSymbols(universe);

                    _logger.LogInformation(
                        "[MD][CLEANUP] symbols={count} bars={bars}",
                        universe.Count,
                        _barsAvailable.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[MD][CLEANUP] failed");
                }
            }
        }

        public void UpdateRealtimePrice(string symbol, decimal price)
        {
            _lastPrice[symbol] = price;
        }
        public decimal GetLastPrice(string symbol)
        {
            return _lastPrice.TryGetValue(symbol, out var p)
                ? p
                : 0m;
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
                    // NOTE: must cover every interval the /market dashboard's TF
                    // selector offers, or that TF silently never gets live pushes
                    // and falls back to the ~45s file snapshot.
                    _ = EnsureWsSubscribed(sym, KlineInterval.OneMinute, CancellationToken.None);
                    _ = EnsureWsSubscribed(sym, KlineInterval.FiveMinutes, CancellationToken.None);
                    _ = EnsureWsSubscribed(sym, KlineInterval.FifteenMinutes, CancellationToken.None);
                    _ = EnsureWsSubscribed(sym, KlineInterval.ThirtyMinutes, CancellationToken.None);
                    _ = EnsureWsSubscribed(sym, KlineInterval.OneHour, CancellationToken.None);
                    _ = EnsureWsSubscribed(sym, KlineInterval.FourHour, CancellationToken.None);
                    _ = EnsureWsSubscribed(sym, KlineInterval.OneDay, CancellationToken.None);

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

            var count = _barsAvailable.AddOrUpdate(key, 1, (_, v) => v + 1);

            if (count == FastWarmBars)
            {
                _logger.LogInformation("[MD][WS] warm READY {symbol} {tf}", symbol, tf);
                OnWarm?.Invoke(symbol, tf);
            }

            WsClosedKline?.Invoke(symbol, tf, candle);

            UpdateMarketState(symbol, tf, candle);
        }

        private readonly ConcurrentDictionary<string, long> _lastStateUpdate = new();

        private void UpdateMarketState(
      string symbol,
      KlineInterval tf,
      BinanceFuturesUsdtKline candle)
        {
            var key = Key(symbol, tf);
            var now = Stopwatch.GetTimestamp();

            var shouldUpdate = false;

            _lastStateUpdate.AddOrUpdate(
                key,
                _ =>
                {
                    shouldUpdate = true;
                    return now;
                },
                (_, last) =>
                {
                    var elapsedMs = (now - last) * 1000.0 / Stopwatch.Frequency;

                    if (elapsedMs < 200)
                        return last;

                    shouldUpdate = true;
                    return now;
                });

            if (!shouldUpdate)
                return;

            try
            {
                var snapshot = BuildSnapshot(symbol, tf, candle);

                if (snapshot == null)
                    return;

                _marketState.Update(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MD][STATE] update failed {symbol} {tf}", symbol, tf);
            }
        }

        private MarketStateSnapshot BuildSnapshot(
        string symbol,
        KlineInterval tf,
        BinanceFuturesUsdtKline candle)
        {
            const int required = 70; // EMA55 + запас
            var klines = _buf.GetLast(symbol, tf, required);

            if (klines.Count < required)
                return null;

            var atr = CalculateAtr(klines, 14);
            var ema21 = CalculateEma(klines, 21);
            var ema55 = CalculateEma(klines, 55);

            return new MarketStateSnapshot
            {
                Symbol = symbol,
                Timeframe = tf.ToString(),
                LastCloseTimeUtc = candle.CloseTime,

                LastPrice = candle.ClosePrice,

                Atr14 = atr,
                Ema21 = ema21,
                Ema55 = ema55,

                Volatility = atr,
                TrendSlope = ema21 - ema55,

                Regime =
                    ema21 > ema55 ? MarketRegime.UpTrend :
                    ema21 < ema55 ? MarketRegime.DownTrend :
                    MarketRegime.Range
            };
        }

        private static decimal CalculateAtr(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period)
        {
            if (klines.Count < period + 2)
                return 0;

            decimal sum = 0;

            for (int i = klines.Count - period; i < klines.Count; i++)
            {
                var cur = klines[i];
                var prev = klines[i - 1];

                decimal tr1 = cur.HighPrice - cur.LowPrice;
                decimal tr2 = Math.Abs(cur.HighPrice - prev.ClosePrice);
                decimal tr3 = Math.Abs(cur.LowPrice - prev.ClosePrice);

                decimal tr = Math.Max(tr1, Math.Max(tr2, tr3));

                sum += tr;
            }

            return sum / period;
        }

        private static decimal CalculateEma(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period)
        {
            if (klines.Count == 0)
                return 0;

            decimal k = 2m / (period + 1);

            decimal ema = klines[0].ClosePrice;

            for (int i = 1; i < klines.Count; i++)
            {
                ema = klines[i].ClosePrice * k + ema * (1 - k);
            }

            return ema;
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
                        // ❗ ВОТ ГЛАВНЫЙ ФИКС
                        UpdateMarketState(symbol, tf, candle);
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
