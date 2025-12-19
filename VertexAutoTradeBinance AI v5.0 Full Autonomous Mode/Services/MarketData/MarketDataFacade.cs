using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.MarketData;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// MarketDataFacade
    /// =================
    /// Единственная точка доступа к klines.
    ///
    /// Приоритет:
    ///  1) WS buffer (live)
    ///  2) REST backfill (строго ограничен)
    ///
    /// Гарантии:
    ///  - REST не спамится
    ///  - WS имеет время на warm-up
    ///  - StrategyEngine всегда получает List<BinanceFuturesUsdtKline>
    /// </summary>
    public sealed class MarketDataFacade
    {
        private readonly MarketDataKlineBuffer _buf;
        private readonly WsKlineSubscriber _ws;
        private readonly BinanceClientFactory _factory; 
        private readonly ILogger<MarketDataFacade> _logger;

        // warm-up gate: symbol:tf -> first WS timestamp
        private readonly ConcurrentDictionary<string, DateTime> _wsStartedUtc = new();

        // REST fallback limiter
        private readonly ConcurrentDictionary<string, DateTime> _lastRestFetchUtc = new();

        private static readonly TimeSpan WsWarmupTimeout = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan RestCooldown = TimeSpan.FromMinutes(1);

        private readonly ConcurrentDictionary<string, int> _wsBars = new();
        // PUSH events
        public event Action<string, KlineInterval>? OnWarm;
        public event Action<string, KlineInterval, BinanceFuturesUsdtKline>? WsClosedKline;
        public event Action<string, KlineInterval>? OnSubscribed; // NE


        public MarketDataFacade(
            MarketDataKlineBuffer buffer,
            WsKlineSubscriber ws,
            BinanceClientFactory factory,
            ILogger<MarketDataFacade> logger)
        {
            _buf = buffer;
            _ws = ws;
            _factory = factory;
            _logger = logger;


            _ws.OnClosedKline += (symbol, tf, kline) =>
            {
                var key = Key(symbol, tf);
                var count = _wsBars.AddOrUpdate(key, 1, (_, v) => v + 1);

                // 🔥 считаем warm по количеству баров, а не по времени
            

                // OnWarm
                if (count == WarmBars)
                {
                    _logger.LogInformation("[MD][WS] warm READY {symbol} {tf}", symbol, tf);
                    OnWarm?.Invoke(symbol, tf);
                }
            };

            // forward WS closed candles
            _ws.OnClosedKline += (symbol, tf, candle) =>
            {
                WsClosedKline?.Invoke(symbol, tf, candle);
            };
        }
       
        private static string Key(string symbol, KlineInterval tf)
            => $"{symbol}:{tf}";

        // ---------------------------------------------------------------------
        // PUBLIC API (используется StrategyEngine / SmartRegime / Supervisor)
        // ---------------------------------------------------------------------
        public async Task<IReadOnlyList<BinanceFuturesUsdtKline>> GetKlinesAsync(
     string symbol,
     KlineInterval tf,
     int need,
     CancellationToken ct = default)
        {
            var key = Key(symbol, tf);

            // 1) гарантируем WS подписку
            await EnsureWsSubscribed(symbol, tf, ct);

            // 2) пробуем WS snapshot
            var ws = _buf.GetLast(symbol, tf, need);

            if (IsInWarmup(symbol, tf))
            {
                _logger.LogDebug(
                    "[MD][WARMUP] block analysis {Symbol} {Timeframe} bars={Bars}/{Need}",
                    symbol, tf, ws.Count, need);

                return ws;
            }

            var restLock = GetRestLock(key);
            if (!await restLock.WaitAsync(0, ct))
                return ws; // backfill уже выполняется в другом потоке

            try
            {
                // повторная проверка WS после входа в lock
                ws = _buf.GetLast(symbol, tf, need);
                if (ws.Count >= need)
                {
                    return ws.Count > need
                        ? ws.Skip(ws.Count - need).Take(need).ToList()
                        : ws;
                }

                // 4) REST fallback (rate-limited)
                if (!CanUseRest(key))
                {
                    _logger.LogWarning(
                        "[MD][REST-SKIP] cooldown active {Symbol} {Timeframe}",
                        symbol, tf);

                    return ws;
                }

                _lastRestFetchUtc[key] = DateTime.UtcNow;

                _logger.LogWarning(
                    "[MD][REST-BACKFILL] {Symbol} {Timeframe} need={Need} have={Have}",
                    symbol, tf, need, ws.Count);

                using var client = _factory.CreateRestClient();

                var rest = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol: symbol,
                    interval: tf,
                    limit: need,
                    ct: ct
                );

                if (!rest.Success || rest.Data == null)
                {
                    _logger.LogError(
                        "[MD][REST-FAIL] {Symbol} {Timeframe}: {Error}",
                        symbol, tf, rest.Error?.Message ?? "unknown");

                    return ws;
                }

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

                return _buf.GetLast(symbol, tf, need);
            }
            finally
            {
                restLock.Release();
            }
        }



        private readonly ConcurrentDictionary<string, Task> _subTasks = new();

        private Task EnsureWsSubscribed(string symbol, KlineInterval tf, CancellationToken ct)
        {
            var key = Key(symbol, tf);
            return _subTasks.GetOrAdd(key, _ => SubscribeCore(symbol, tf, ct));
        }

        private async Task SubscribeCore(string symbol, KlineInterval tf, CancellationToken ct)
        {
            await _ws.SubscribeAsync(symbol, tf, ct);
            _wsStartedUtc[Key(symbol, tf)] = DateTime.UtcNow;
            _logger.LogInformation("[MD][WS] subscribe started {symbol} {tf}", symbol, tf);
            OnSubscribed?.Invoke(symbol, tf);
        }

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _restLocks = new();

        private SemaphoreSlim GetRestLock(string key) =>
            _restLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        private const int WarmBars = 20;

        public bool IsInWarmup(string symbol, KlineInterval tf)
        {
            var key = Key(symbol, tf);
            if (!_wsBars.TryGetValue(key, out var bars))
                return true;

            return bars < WarmBars;
        }


        private bool CanUseRest(string key)
        {
            if (!_lastRestFetchUtc.TryGetValue(key, out var last))
                return true;

            return DateTime.UtcNow - last > RestCooldown;
        }
    }
}
