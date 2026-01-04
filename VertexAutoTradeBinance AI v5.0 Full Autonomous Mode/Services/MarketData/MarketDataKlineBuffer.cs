using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;

namespace VertexAutoTradeBinance8.MarketData
{
    /// <summary>
    /// Canonical in-memory kline buffer.
    /// Source-agnostic: WS + REST are both normalized to BinanceFuturesUsdtKline.
    /// Thread-safe. Bounded. PROD-ready.
    /// </summary>
    public sealed class MarketDataKlineBuffer
    {
        private const int DefaultMaxBars = 600;

        private readonly ConcurrentDictionary<string, LinkedList<BinanceFuturesUsdtKline>> _buffers
            = new();

        private readonly int _maxBars;

        public MarketDataKlineBuffer(int maxBars = DefaultMaxBars)
        {
            if (maxBars < 100)
                maxBars = 100;

            _maxBars = maxBars;
        }

        private static string MakeKey(string symbol, KlineInterval tf)
            => $"{symbol}:{tf}";

        /// <summary>
        /// Insert or replace last kline by OpenTime.
        /// Safe for WS (updates same candle) and REST (historical batch).
        /// </summary>
        public void Upsert(
            string symbol,
            KlineInterval tf,
            BinanceFuturesUsdtKline kline)
        {
            if (kline == null)
                return;

            var key = MakeKey(symbol, tf);
            var list = _buffers.GetOrAdd(key, _ => new LinkedList<BinanceFuturesUsdtKline>());

            lock (list)
            {
                // Replace last candle if same OpenTime (WS update)
                if (list.Last != null && list.Last.Value.OpenTime == kline.OpenTime)
                {
                    list.RemoveLast();
                }

                list.AddLast(kline);

                // Bound memory
                while (list.Count > _maxBars)
                    list.RemoveFirst();
            }
        }

        /// <summary>
        /// Snapshot (copy) of current buffer.
        /// </summary>
        public IReadOnlyList<BinanceFuturesUsdtKline> Snapshot(
            string symbol,
            KlineInterval tf)
        {
            var key = MakeKey(symbol, tf);
            if (!_buffers.TryGetValue(key, out var list))
                return Array.Empty<BinanceFuturesUsdtKline>();

            lock (list)
            {
                return list.ToList();
            }
        }

        /// <summary>
        /// Get last N klines (safe slice).
        /// </summary>
        public IReadOnlyList<BinanceFuturesUsdtKline> GetLast(
       string symbol,
       KlineInterval tf,
       int count)
        {
            if (count <= 0)
                return Array.Empty<BinanceFuturesUsdtKline>();

            var key = MakeKey(symbol, tf);
            if (!_buffers.TryGetValue(key, out var list))
                return Array.Empty<BinanceFuturesUsdtKline>();

            lock (list)
            {
                if (list.Count == 0)
                    return Array.Empty<BinanceFuturesUsdtKline>();

                if (list.Count <= count)
                    return list.ToList(); // редкий случай, ок

                // ✅ копируем ТОЛЬКО последние count
                var result = new BinanceFuturesUsdtKline[count];
                var node = list.Last;

                for (int i = count - 1; i >= 0 && node != null; i--)
                {
                    result[i] = node.Value;
                    node = node.Previous;
                }

                return result;
            }
        }


        /// <summary>
        /// Current bar count in buffer.
        /// </summary>
        public int Count(string symbol, KlineInterval tf)
        {
            var key = MakeKey(symbol, tf);
            if (!_buffers.TryGetValue(key, out var list))
                return 0;

            lock (list)
                return list.Count;
        }


        /// <summary>
        /// Clear buffer for symbol/timeframe (maintenance / reconnect).
        /// </summary>
        public void Clear(string symbol, KlineInterval tf)
        {
            var key = MakeKey(symbol, tf);
            _buffers.TryRemove(key, out _);
        }

        /// <summary>
        /// Clear everything (shutdown / full reset).
        /// </summary>
        public void ClearAll()
        {
            _buffers.Clear();
        }

        public Dictionary<string, List<BinanceFuturesUsdtKline>> LoadSnapshot()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "market-klines.snapshot.json");

            if (!File.Exists(path))
                return new();

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<
                    Dictionary<string, List<BinanceFuturesUsdtKline>>
                >(json) ?? new();
            }
            catch
            {
                return new();
            }
        }

    }
}
