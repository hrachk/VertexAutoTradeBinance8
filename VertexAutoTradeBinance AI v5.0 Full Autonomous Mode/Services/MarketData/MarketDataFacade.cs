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

        // Serialises RestoreSnapshotStateAsync so the lazy self-heal path in
        // GetKlinesAsync cannot run it concurrently with TradingWorker's
        // explicit call (or with itself, from parallel symbol warmups).
        private readonly SemaphoreSlim _restoreGate = new(1, 1);

        public bool HasSnapshotState => _restoreAttempted;   // semantic: restore attempt completed
        public bool ReadyBySnapshot => _readyBySnapshot;     // semantic: snapshot exists => rest backfill disabled

        /// <summary>
        /// Synchronous, read-only access to whatever's already in the
        /// live in-memory buffer for a symbol+timeframe — no REST call,
        /// no async needed. Used for multi-timeframe confirmation checks
        /// (e.g. does the 4h structure for THIS symbol agree with a
        /// signal generated on a lower timeframe), which need to stay
        /// synchronous to avoid changing the signature of every signal-
        /// generation function that would need to await otherwise.
        /// Returns an empty list if nothing is buffered yet for this
        /// pair — callers should treat that as "no data, don't block the
        /// signal on it" rather than a hard failure.
        /// </summary>
        public IReadOnlyList<BinanceFuturesUsdtKline> GetBufferedKlines(string symbol, KlineInterval tf)
            => _buf.Snapshot(symbol, tf);

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

        // ── REST Circuit Breaker ──────────────────────────────────────────
        // When a network-level error (DNS, TCP refused, timeout) is detected
        // we open the circuit for _circuitOpenDuration so subsequent symbols
        // in the same warmup batch don't each wait for their own timeout.
        // Pattern: Closed → (N consecutive network failures) → Open → (timer) → Closed
        private volatile bool _circuitOpen = false;
        private DateTime _circuitOpenUntil = DateTime.MinValue;
        private int _consecutiveNetworkFailures = 0;
        private const int CircuitOpenThreshold = 2;  // failures before opening
        private static readonly TimeSpan _circuitOpenDuration = TimeSpan.FromMinutes(2);


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

            HashSet<string> newlyAdded;
            lock (_universeLock)
            {
                newlyAdded = new HashSet<string>(target, StringComparer.OrdinalIgnoreCase);
                newlyAdded.ExceptWith(_universe);

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

            // CRITICAL FIX: this was the remaining gap after the
            // SupervisorBootstrapHostedService fix (which only covers
            // PinnedSymbols at Engine startup) — any symbol that enters
            // the universe via Auto-selection (not pinned), whether at
            // startup or later when the universe rotates, previously
            // got NOTHING but a WS subscription here: no historical
            // backfill at all, meaning it had to accumulate 40-60+ bars
            // purely from live ticks before the strategy's own
            // signal-generation checks would pass — the same multi-hour
            // cold-start problem, just for a different set of symbols
            // and recurring every time the universe changes, not only
            // at boot.
            //
            // Fix: proactively call the EXISTING GetKlinesAsync (below)
            // for each genuinely-new symbol on the strategy's two
            // tracked timeframes (5m/15m) the moment it enters the
            // universe — this is the exact same method the strategy
            // itself already lazily calls on first read, which already
            // has proper rate-limiting (_globalRestLimiter), singleflight
            // locking (per-symbol+tf SemaphoreSlim), and a cooldown
            // (CanUseRest) built in. No new fetch mechanism was
            // invented — this just calls the safe one earlier, instead
            // of waiting for the strategy to discover the gap on its
            // own and trigger it reactively bar-by-bar.
            if (newlyAdded.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    foreach (var sym in newlyAdded)
                    {
                        try
                        {
                            await GetKlinesAsync(sym, KlineInterval.FiveMinutes, 200, CancellationToken.None).ConfigureAwait(false);
                            await GetKlinesAsync(sym, KlineInterval.FifteenMinutes, 200, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[MD][UNIVERSE-BACKFILL] Failed for new symbol {symbol}", sym);
                        }
                    }
                });
            }
        }

        // =====================================================
        // SNAPSHOT RESTORE (SAFE, production)
        // - if snapshot found: mark ReadyBySnapshot=true (REST disabled)
        // - if not found: mark restoreAttempted=true but ReadyBySnapshot=false (REST allowed)
        // =====================================================
        public async Task RestoreSnapshotStateAsync(CancellationToken ct)
        {
            // Idempotent: whoever gets here first does the work, everyone
            // else returns immediately. Cheap fast-path before the gate.
            if (_restoreAttempted)
                return;

            await _restoreGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_restoreAttempted)
                    return;

                await RestoreSnapshotStateCoreAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _restoreGate.Release();
            }
        }

        private async Task RestoreSnapshotStateCoreAsync(CancellationToken ct)
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
                // Hosted-service start order is not guaranteed relative to
                // TradingWorker's explicit RestoreSnapshotStateAsync() call,
                // so an early caller used to land here, log FATAL, and then
                // proceed against a facade whose _barsAvailable /
                // _readyBySnapshot state had never been populated — silently
                // degrading every readiness decision that followed.
                //
                // Self-heal instead: run the restore now. It's idempotent and
                // gated, so the later explicit call becomes a no-op.
                _logger.LogWarning(
                    "[MD][SELF-HEAL] GetKlinesAsync before restore ({symbol} {tf}) — restoring now",
                    symbol, tf);

                try
                {
                    await RestoreSnapshotStateAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Never block market data on a restore failure — the WS
                    // path below still works from an empty buffer.
                    _logger.LogError(ex, "[MD][SELF-HEAL] Lazy restore failed — continuing cold");
                    _restoreAttempted = true;
                }
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
            //   for symbols/timeframes the snapshot already covers
            // - EXCEPTION: a symbol+timeframe with ZERO buffered candles
            //   was never part of what made the snapshot "authoritative"
            //   in the first place — it entered the universe AFTER
            //   startup (Auto-selection rotation, or a newly opened
            //   position, exactly the case that motivated this fix).
            //   Without this carve-out, such a symbol could sit at 0
            //   candles indefinitely on every timeframe REST would
            //   otherwise be able to backfill, since the only path back
            //   to real history was unconditionally blocked by a flag
            //   meant to protect symbols the snapshot DID restore.
            // - if cold start => REST allowed under cooldown + singleflight + global limiter
            if (_readyBySnapshot && ws.Count > 0)
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

                // ── Circuit breaker check ─────────────────────────────────
                if (_circuitOpen)
                {
                    if (DateTime.UtcNow < _circuitOpenUntil)
                    {
                        _logger.LogDebug("[MD][CB] Circuit OPEN — skipping REST for {symbol} {tf}", symbol, tf);
                        return ws;
                    }
                    // Time elapsed → half-open: try one request
                    _circuitOpen = false;
                    _consecutiveNetworkFailures = 0;
                    _logger.LogInformation("[MD][CB] Circuit HALF-OPEN — probing {symbol} {tf}", symbol, tf);
                }

                await _globalRestLimiter.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var restClient = _factory.CreateRestClient();

                    CryptoExchange.Net.Objects.WebCallResult<Binance.Net.Interfaces.IBinanceKline[]> rest;
                    try
                    {
                        rest = await restClient.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                            symbol: symbol,
                            interval: tf,
                            limit: need,
                            ct: ct).ConfigureAwait(false);
                    }
                    catch (Exception netEx) when (
                        netEx is System.Net.Http.HttpRequestException ||
                        netEx is TaskCanceledException ||
                        netEx is System.Net.Sockets.SocketException)
                    {
                        // Network-level failure (DNS, TCP refused, timeout)
                        int fails = Interlocked.Increment(ref _consecutiveNetworkFailures);
                        _logger.LogError("[MD][KLINES] Network error loading klines for {symbol}: {msg}", symbol, netEx.Message);
                        if (fails >= CircuitOpenThreshold)
                        {
                            _circuitOpen = true;
                            _circuitOpenUntil = DateTime.UtcNow.Add(_circuitOpenDuration);
                            _logger.LogCritical("[MD][CB] Circuit OPENED after {n} failures — REST paused for {min} min", fails, _circuitOpenDuration.TotalMinutes);
                        }
                        return ws;
                    }

                    if (!rest.Success || rest.Data == null)
                    {
                        // API-level failure (rate limit, auth, etc.) — don't trip circuit
                        _logger.LogWarning("[MD][KLINES] REST failed for {symbol} {tf}: {err}", symbol, tf, rest.Error?.Message);
                        return ws;
                    }

                    // Success → reset circuit breaker
                    Interlocked.Exchange(ref _consecutiveNetworkFailures, 0);

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

