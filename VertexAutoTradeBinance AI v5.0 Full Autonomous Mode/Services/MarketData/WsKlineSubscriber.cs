using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Objects.Models.Futures;
using CryptoExchange.Net.Objects.Sockets;
using System.Collections.Concurrent;
using VertexAutoTradeBinance8.MarketData;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Production-grade WS subscriber:
    /// - realtime price via OnPrice (EVERY update)
    /// - closed candles via OnClosedKline
    /// - thread-safe
    /// - single socket instance
    /// - idempotent subscriptions
    /// </summary>
    public sealed class WsKlineSubscriber
    {
        private readonly BinanceSocketClient _socket;
        private readonly MarketDataKlineBuffer _buffer;
        private readonly ILogger<WsKlineSubscriber> _logger;

        // active subscriptions
        private readonly ConcurrentDictionary<string, UpdateSubscription> _subs = new();

        // singleflight subscribe
        private readonly ConcurrentDictionary<string, Task> _subTasks = new();

        /// <summary>
        /// REALTIME price event (fires on EVERY kline update, not only closed)
        /// </summary>
        public event Action<string, decimal>? OnPrice;

        /// <summary>
        /// Closed candle event
        /// </summary>
        public event Action<string, KlineInterval, BinanceFuturesUsdtKline>? OnClosedKline;

        public WsKlineSubscriber(
            BinanceClientFactory factory,
            MarketDataKlineBuffer buffer,
            ILogger<WsKlineSubscriber> logger)
        {
            _socket = factory.CreateSocketClient();
            _buffer = buffer;
            _logger = logger;

            _logger.LogInformation("[WS] Socket initialized");
        }

        private static string Key(string symbol, KlineInterval tf)
            => $"{symbol}:{tf}";

        // ============================================================
        // CORE SUBSCRIBE
        // ============================================================
        private async Task SubscribeCore(
            string symbol,
            KlineInterval interval,
            CancellationToken ct)
        {
            var key = Key(symbol, interval);

            if (_subs.ContainsKey(key))
                return;

            _logger.LogInformation(
                "[WS] Subscribing {symbol} {tf}",
                symbol,
                interval);

            const int maxRetries = 5;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var result =
                        await _socket.UsdFuturesApi.ExchangeData
                            .SubscribeToKlineUpdatesAsync(
                                symbol,
                                interval,
                                HandleKline(symbol, interval),
                                ct: ct);

                    if (result.Success)
                    {
                        _subs[key] = result.Data;

                        _logger.LogInformation(
                            "[WS] Subscribe OK {symbol} {tf}",
                            symbol,
                            interval);

                        return;
                    }

                    _logger.LogWarning(
                        "[WS] Subscribe attempt {attempt}/{max} FAILED {symbol} {tf}: {err}",
                        attempt,
                        maxRetries,
                        symbol,
                        interval,
                        result.Error?.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "[WS] Subscribe attempt {attempt}/{max} EXCEPTION {symbol} {tf}",
                        attempt,
                        maxRetries,
                        symbol,
                        interval);
                }

                await Task.Delay(1000 * attempt, ct);
            }

            _logger.LogError(
                "[WS] Subscribe PERMANENT FAILED {symbol} {tf}",
                symbol,
                interval);
        }

        // ============================================================
        // HANDLER (HOT PATH)
        // ============================================================

        private Action<DataEvent<IBinanceStreamKlineData>> HandleKline(
            string symbol,
            KlineInterval interval)
        {
            return data =>
            {
                try
                {
                    var k = data.Data.Data;

                    var price = k.ClosePrice;

                    // 🔥 ALWAYS publish realtime price
                    //OnPrice?.Invoke(symbol, price);

                    //// only closed candle below
                    //if (!k.Final)
                    //    return;

                    //var candle = new BinanceFuturesUsdtKline
                    //{
                    //    OpenTime = k.OpenTime,
                    //    CloseTime = k.CloseTime,
                    //    OpenPrice = k.OpenPrice,
                    //    HighPrice = k.HighPrice,
                    //    LowPrice = k.LowPrice,
                    //    ClosePrice = k.ClosePrice,
                    //    Volume = k.Volume,
                    //    QuoteVolume = k.QuoteVolume,
                    //    TradeCount = k.TradeCount,
                    //    TakerBuyBaseVolume = k.TakerBuyBaseVolume,
                    //    TakerBuyQuoteVolume = k.TakerBuyQuoteVolume
                    //};

                    //_buffer.Upsert(symbol, interval, candle);

                    //OnClosedKline?.Invoke(symbol, interval, candle);
                    OnPrice?.Invoke(symbol, price);

                    // 🔥 ALWAYS update buffer (live candle)
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

                    _buffer.Upsert(symbol, interval, candle);

                    // closed candle event separately
                    if (k.Final)
                    {
                        OnClosedKline?.Invoke(symbol, interval, candle);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "[WS] Handler error {symbol} {tf}",
                        symbol,
                        interval);
                }
            };
        }

        // ============================================================
        // PUBLIC SUBSCRIBE
        // ============================================================

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

        // ============================================================
        // UNSUBSCRIBE
        // ============================================================

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
                    "[WS] Unsubscribed {symbol} {tf}",
                    symbol,
                    interval);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[WS] Unsubscribe FAILED {symbol} {tf}",
                    symbol,
                    interval);
            }
        }

        // ============================================================
        // STOP ALL
        // ============================================================

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
            _subTasks.Clear();

            _logger.LogWarning("[WS] All subscriptions stopped");
        }
    }
}
