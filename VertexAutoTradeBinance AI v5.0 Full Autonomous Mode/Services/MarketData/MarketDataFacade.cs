using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using System.Collections.Concurrent;
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

       
        private readonly ConcurrentDictionary<string, DateTime> _lastRestFetchUtc = new();
        private readonly ConcurrentDictionary<string, bool> _restBackfilled = new();
        private static readonly SemaphoreSlim _globalRestLimiter = new(3, 3);
        private readonly ConcurrentDictionary<string, int> _wsBars = new();

        private volatile bool _hasSnapshotState;
        private volatile bool _readyBySnapshot;

        public bool HasSnapshotState => _hasSnapshotState;
        public bool ReadyBySnapshot => _readyBySnapshot;

        public event Action<string, KlineInterval>? OnWarm;
        public event Action<string, KlineInterval, BinanceFuturesUsdtKline>? WsClosedKline;

        private const int WarmBars = 20;
        private const int FastWarmBars = 10;
        private static readonly TimeSpan RestCooldown = TimeSpan.FromMinutes(1);

        // === UNIVERSE SUBSCRIPTIONS ===
        private readonly object _universeLock = new();
        private HashSet<string> _universe = new(StringComparer.OrdinalIgnoreCase);

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
            // 🔥 HARD LINK: MarketState → MarketData
            if (_marketState.IsRestored)
                MarkSnapshotReady();

            _marketState.OnRestored += MarkSnapshotReady;
        }
        // =====================================================
        // SNAPSHOT READY (FINAL)
        // =====================================================

        public void ApplyUniverse(IReadOnlyList<string> symbols)
        {
            if (symbols == null || symbols.Count == 0)
                return;

            var target = new HashSet<string>(
                symbols.Select(s => s.Trim().ToUpperInvariant()),
                StringComparer.OrdinalIgnoreCase);

            lock (_universeLock)
            {
                var toAdd = target.Except(_universe).ToList();
                if (toAdd.Count == 0)
                    return;

                foreach (var sym in toAdd)
                {
                    // КЛЮЧЕВО: подписываемся СРАЗУ
                    _ = EnsureWsSubscribed(sym, KlineInterval.OneMinute, CancellationToken.None);
                    _ = EnsureWsSubscribed(sym, KlineInterval.FiveMinutes, CancellationToken.None);

                    _logger.LogInformation("[MD][UNIVERSE] subscribe {sym}", sym);
                }

                _universe = target;
            }
        }

        public void MarkSnapshotReady()
        {
            if (_readyBySnapshot)
                return;

            _hasSnapshotState = true;
            _readyBySnapshot = true;

            _logger.LogWarning(
                "[MD][STATE] SNAPSHOT READY → warmup & REST backfill DISABLED");
        }

        // =====================================================
        // SNAPSHOT RESTORE (SAFE)
        // =====================================================
        public async Task RestoreSnapshotStateAsync(CancellationToken ct)
        {
            try
            {
                _logger.LogWarning("[MD][RESTORE] Restoring market snapshot state...");

                var snapshot = _buf.LoadSnapshot();
                if (snapshot == null || snapshot.Count == 0)
                {
                    _logger.LogWarning("[MD][RESTORE] No snapshot found → cold start");
                    return;
                }

                bool restoredAny = false;

                foreach (var (key, candles) in snapshot)
                {
                    if (candles == null || candles.Count == 0)
                        continue;

                    var parts = key.Split(':');
                    if (parts.Length != 2)
                        continue;

                    if (!Enum.TryParse<KlineInterval>(parts[1], out var tf))
                        continue;

                    var symbol = parts[0];

                    foreach (var c in candles)
                        _buf.Upsert(symbol, tf, c);

                    _wsBars[key] = candles.Count;
                    _restBackfilled[key] = true;

                    restoredAny = true;

                    if (candles.Count >= FastWarmBars)
                        OnWarm?.Invoke(symbol, tf);

                    _logger.LogInformation(
                        "[MD][RESTORE] {symbol} {tf} restored bars={bars}",
                        symbol, tf, candles.Count);
                }

                if (restoredAny)
                {
                    _hasSnapshotState = true;
                    _readyBySnapshot = true;

                    _logger.LogWarning("[MD][RESTORE] Snapshot restored → READY (warmup accelerated)");
                }
                else
                {
                    _logger.LogWarning("[MD][RESTORE] Snapshot empty → cold start");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MD][RESTORE] Failed to restore snapshot");
            }
        }

        // =====================================================
        // WS EVENTS
        // =====================================================
        private void OnWsClosedKline(string symbol, KlineInterval tf, BinanceFuturesUsdtKline candle)
        {
            var key = Key(symbol, tf);
            var count = _wsBars.AddOrUpdate(key, 1, (_, v) => v + 1);

            if (count == FastWarmBars)
            {
                _logger.LogInformation("[MD][WS] warm READY {symbol} {tf}", symbol, tf);
                OnWarm?.Invoke(symbol, tf);
            }

            WsClosedKline?.Invoke(symbol, tf, candle);
        }

        private static string Key(string symbol, KlineInterval tf) => $"{symbol}:{tf}";

        // =====================================================
        // MAIN API
        // =====================================================
        public async Task<IReadOnlyList<BinanceFuturesUsdtKline>> GetKlinesAsync(
            string symbol,
            KlineInterval tf,
            int need,
            CancellationToken ct = default)
        {

            if (!_hasSnapshotState)
            {
                _logger.LogCritical(
                    "[MD][HARD-GUARD] GetKlinesAsync called BEFORE snapshot ready {symbol} {tf}",
                    symbol, tf);

                return _buf.GetLast(symbol, tf, need); // best-effort WS ONLY
            }


            var key = Key(symbol, tf);

            // 1) WS subscribe (idempotent)
            await EnsureWsSubscribed(symbol, tf, ct);

            // 2) берем что есть
            var ws = _buf.GetLast(symbol, tf, need);
            if (ws.Count >= need)
                return ws;

            // 3) REST — ТОЛЬКО если snapshot НЕ был
            if (_readyBySnapshot)
                return ws;



            var restLock = GetRestLock(key);
            if (!await restLock.WaitAsync(0, ct))
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

                await _globalRestLimiter.WaitAsync(ct);
                try
                {
                    var client = _factory.CreateRestClient();

                    var rest = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                        symbol, tf, limit: need,  ct: ct);

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
                    return _buf.GetLast(symbol, tf, need);
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
        // WARMUP LOGIC (FIXED)
        // =====================================================
        public bool IsInWarmup(string symbol, KlineInterval tf)
        {
            var key = Key(symbol, tf);

            if (!_wsBars.TryGetValue(key, out var bars))
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
        // WS SUBSCRIBE SINGLEFLIGHT
        // =====================================================
        private readonly ConcurrentDictionary<string, Task> _subTasks = new();

        //private Task EnsureWsSubscribed(string symbol, KlineInterval tf, CancellationToken ct)
        //{
        //    var key = Key(symbol, tf);

        //    return _subTasks.GetOrAdd(key, _ =>
        //        Task.Run(async () =>
        //        {
        //            await _ws.SubscribeAsync(symbol, tf, CancellationToken.None);
        //            _wsStartedUtc[key] = DateTime.UtcNow;
        //            _logger.LogInformation("[MD][WS] subscribe started {symbol} {tf}", symbol, tf);
        //        }));
        //}

        private Task EnsureWsSubscribed(string symbol, KlineInterval tf, CancellationToken ct)
        {
            var key = Key(symbol, tf);

            return _subTasks.GetOrAdd(key, _ => SubscribeInternal(symbol, tf));
        }

        private async Task SubscribeInternal(string symbol, KlineInterval tf)
        {
            try
            {
                await _ws.SubscribeAsync(symbol, tf, CancellationToken.None);

                _logger.LogInformation(
                    "[MD][WS] subscribe started {symbol} {tf}",
                    symbol, tf);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[MD][WS] subscribe FAILED {symbol} {tf}",
                    symbol, tf);

                throw;
            }
        }
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _restLocks = new();
        private SemaphoreSlim GetRestLock(string key) =>
            _restLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }
}
