using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using CryptoExchange.Net.Objects.Sockets;
using System.Collections.Concurrent;
using VertexAutoTradeBinance8.MarketData;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// WebSocket subscriber for Binance Futures USD-M klines.
    /// WS lifecycle owned here. Socket created via BinanceClientFactory.
    /// </summary>
    public sealed class WsKlineSubscriber
    {
        private readonly BinanceSocketClient _socket;
        private readonly MarketDataKlineBuffer _buffer;
        private readonly ILogger<WsKlineSubscriber> _logger;

        // symbol:tf → subscription
        private readonly ConcurrentDictionary<string, UpdateSubscription> _subs = new();
        private readonly ConcurrentDictionary<string, Task> _subTasks = new();
        public event Action<string, decimal>? OnPrice;

        public event Action<string, KlineInterval, BinanceFuturesUsdtKline>? OnClosedKline;

        public WsKlineSubscriber(
            BinanceClientFactory factory,
            MarketDataKlineBuffer buffer,
            ILogger<WsKlineSubscriber> logger)
        {
            _buffer = buffer;
            _logger = logger;

            // 🔥 ВАЖНО: один socket на весь subscriber
            _socket = factory.CreateSocketClient();

            _logger.LogInformation("[WS][KLINES] Socket client created");
        }

        private static string Key(string symbol, KlineInterval tf)
            => $"{symbol}:{tf}";

        // ---------------------------------------------------------------------

        private async Task SubscribeCore(
            string symbol,
            KlineInterval interval,
            CancellationToken ct)
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
                var price = k.Data.ClosePrice;

                // 🔥 ALWAYS push realtime price
                OnPrice?.Invoke(symbol, price);

                // only closed candle below
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

                // 🔥 closed candle event stays
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
        ct: ct);

            //var sub = await _socket.UsdFuturesApi.ExchangeData
            //    .SubscribeToKlineUpdatesAsync(
            //        symbol,
            //        interval,
            //        data =>
            //        {
            //            try
            //            {
            //                var k = data.Data;

            //                // ⛔ only CLOSED candles
            //                if (!k.Data.Final)
            //                    return;

            //                var candle = new BinanceFuturesUsdtKline
            //                {
            //                    OpenTime = k.Data.OpenTime,
            //                    CloseTime = k.Data.CloseTime,
            //                    OpenPrice = k.Data.OpenPrice,
            //                    HighPrice = k.Data.HighPrice,
            //                    LowPrice = k.Data.LowPrice,
            //                    ClosePrice = k.Data.ClosePrice,
            //                    Volume = k.Data.Volume,
            //                    QuoteVolume = k.Data.QuoteVolume,
            //                    TradeCount = k.Data.TradeCount,
            //                    TakerBuyBaseVolume = k.Data.TakerBuyBaseVolume,
            //                    TakerBuyQuoteVolume = k.Data.TakerBuyQuoteVolume
            //                };

            //                _buffer.Upsert(symbol, interval, candle);

            //                // 🔥 reactive push
            //                OnClosedKline?.Invoke(symbol, interval, candle);
            //            }
            //            catch (Exception ex)
            //            {
            //                _logger.LogError(
            //                    ex,
            //                    "[WS][KLINES][{symbol}][{tf}] handler error",
            //                    symbol, interval);
            //            }
            //        },
            //        ct: ct);

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

        // ---------------------------------------------------------------------

        public Task SubscribeAsync(
            string symbol,
            KlineInterval interval,
            CancellationToken ct = default)
        {
            var key = Key(symbol, interval);
            return _subTasks.GetOrAdd(
                key,
                _ => SubscribeCore(symbol, interval, ct));
        }

        // ---------------------------------------------------------------------

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

        // ---------------------------------------------------------------------

        public async Task StopAllAsync()
        {
            foreach (var kv in _subs)
            {
                try
                {
                    await _socket.UnsubscribeAsync(kv.Value);
                }
                catch { }
            }

            _subs.Clear();
        }
    }
}
