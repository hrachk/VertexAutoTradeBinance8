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
                if (count == 20)
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
            var ws = _buf.Snapshot(symbol, tf);
            if (ws.Count >= need)
                return ws.TakeLast(need).ToList();

            // 3) warm-up gate — даём WS шанс наполниться
            if (IsInWarmup(key))
            {
                _logger.LogDebug(
                    "[MD][WARMUP] {symbol} {tf} have={have} need={need}",
                    symbol, tf, ws.Count, need);

                return ws; // возвращаем то, что есть
            }

            // 4) REST fallback (rate-limited)
            if (!CanUseRest(key))
            {
                _logger.LogWarning(
                    "[MD][REST-SKIP] cooldown active {symbol} {tf}",
                    symbol, tf);

                return ws;
            }

            _lastRestFetchUtc[key] = DateTime.UtcNow;

            _logger.LogWarning(
                "[MD][REST-BACKFILL] {symbol} {tf} need={need} have={have}",
                symbol, tf, ws.Count);

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
                    "[MD][REST-FAIL] {symbol} {tf}: {err}",
                    symbol, tf, rest.Error?.Message);

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


            return _buf.Snapshot(symbol, tf)
                .TakeLast(need)
                .ToList();
        }

        // ---------------------------------------------------------------------
        // INTERNAL
        // ---------------------------------------------------------------------
        private async Task EnsureWsSubscribed(
            string symbol,
            KlineInterval tf,
            CancellationToken ct)
        {
            var key = Key(symbol, tf);

            if (_wsStartedUtc.ContainsKey(key))
                return;

            await _ws.SubscribeAsync(symbol, tf, ct);
            _wsStartedUtc[key] = DateTime.UtcNow;

            _logger.LogInformation(
                "[MD][WS] warm-up started {symbol} {tf}",
                symbol, tf);

            OnWarm?.Invoke(symbol, tf);

        }

        private bool IsInWarmup(string key)
        {
            if (!_wsStartedUtc.TryGetValue(key, out var started))
                return true;

            return DateTime.UtcNow - started < WsWarmupTimeout;
        }

        private bool CanUseRest(string key)
        {
            if (!_lastRestFetchUtc.TryGetValue(key, out var last))
                return true;

            return DateTime.UtcNow - last > RestCooldown;
        }
    }
}
