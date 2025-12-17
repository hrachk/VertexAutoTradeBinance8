using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using CryptoExchange.Net.Objects.Sockets;
using CryptoExchange.Net.Sockets;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.MarketData;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// WebSocket subscriber for Binance Futures USD-M klines.
    /// Normalizes WS data → BinanceFuturesUsdtKline
    /// Pushes into MarketDataKlineBuffer.
    /// </summary>
    public sealed class WsKlineSubscriber
    {
        private readonly BinanceSocketClient _socket;
        private readonly MarketDataKlineBuffer _buffer;
        private readonly ILogger<WsKlineSubscriber> _logger;

        // symbol:tf → subscription
        private readonly ConcurrentDictionary<string, UpdateSubscription> _subs = new();
        public event Action<string, KlineInterval, BinanceFuturesUsdtKline>? OnClosedKline;

        public WsKlineSubscriber(
            BinanceSocketClient socket,
            MarketDataKlineBuffer buffer,
            ILogger<WsKlineSubscriber> logger)
        {
            _socket = socket;
            _buffer = buffer;
            _logger = logger;
        }

        private static string Key(string symbol, KlineInterval tf)
            => $"{symbol}:{tf}";

        /// <summary>
        /// Subscribe to klines stream (idempotent).
        /// </summary>
        public async Task SubscribeAsync(
            string symbol,
            KlineInterval interval,
            CancellationToken ct = default)
        {
            var key = Key(symbol, interval);
            if (_subs.ContainsKey(key))
                return;

            _logger.LogInformation(
                "[WS][KLINES] Subscribing {symbol} {tf}",
                symbol, interval);

            var sub = await _socket.UsdFuturesApi.ExchangeData
                .SubscribeToKlineUpdatesAsync(
                    symbol,
                    interval,
                   data =>
                   {
                       try
                       {
                           var k = data.Data;

                           // ⛔ ONLY CLOSED CANDLES
                           if (!k.Data.Final)
                               return;

                           var candle = new BinanceFuturesUsdtKline
                           {
                               OpenTime = k.Data.OpenTime,
                               CloseTime = k.Data.CloseTime,
                               OpenPrice = k.Data.OpenPrice,
                               HighPrice = k.Data.HighPrice,
                               LowPrice = k.Data.LowPrice,
                               ClosePrice = k.Data.ClosePrice,
                               Volume = k.Data.Volume,
                               QuoteVolume = k.Data.QuoteVolume,
                               TradeCount = k.Data.TradeCount,
                               TakerBuyBaseVolume = k.Data.TakerBuyBaseVolume,
                               TakerBuyQuoteVolume = k.Data.TakerBuyQuoteVolume
                           };

                           _buffer.Upsert(symbol, interval, candle);

                           // 🔥 РЕАКТИВНЫЙ PUSH
                           OnClosedKline?.Invoke(symbol, interval, candle);
                       }
                       catch (Exception ex)
                       {
                           _logger.LogError(
                               ex,
                               "[WS][KLINES][{symbol}][{tf}] handler error",
                               symbol, interval);
                       }
                   },
                    ct:ct);

            if (!sub.Success)
            {
                _logger.LogError(
                    "[WS][KLINES] Subscribe FAILED {symbol} {tf}: {err}",
                    symbol,
                    interval,
                    sub.Error?.Message);

                return;
            }

            _subs[key] = sub.Data;
        }

        /// <summary>
        /// Unsubscribe from klines stream.
        /// </summary>
        public async Task UnsubscribeAsync(
            string symbol,
            KlineInterval interval)
        {
            var key = Key(symbol, interval);
            if (!_subs.TryRemove(key, out var sub))
                return;

            try
            {
                await _socket.UnsubscribeAsync(sub);
                _logger.LogInformation(
                    "[WS][KLINES] Unsubscribed {symbol} {tf}",
                    symbol, interval);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[WS][KLINES] Unsubscribe error {symbol} {tf}",
                    symbol, interval);
            }
        }

        /// <summary>
        /// Shutdown all WS subscriptions.
        /// </summary>
        public async Task StopAllAsync()
        {
            foreach (var kv in _subs)
            {
                try
                {
                    await _socket.UnsubscribeAsync(kv.Value);
                }
                catch
                {
                    // ignore
                }
            }

            _subs.Clear();
        }
    }
}
