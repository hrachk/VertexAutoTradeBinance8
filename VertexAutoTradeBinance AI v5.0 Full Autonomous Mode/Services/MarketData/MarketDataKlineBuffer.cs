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
    ///
    /// ⚠ COMPAT NOTE:
    /// LoadSnapshot() is kept for backward compatibility.
    /// New persistence should use KlineBufferPersistence instead.
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

        private int GetMaxBarsForTf(KlineInterval tf)
        {
            return tf switch
            {
                KlineInterval.OneMinute => 800,
                KlineInterval.FiveMinutes => 500,
                KlineInterval.FifteenMinutes => 300,
                KlineInterval.OneHour => 200,
                KlineInterval.FourHour => 150,
                _ => _maxBars
            };
        }

        private static string MakeKey(string symbol, KlineInterval tf)
            => $"{symbol}:{tf}";

        // =====================================================
        // UPSERT (WS + REST SAFE)
        // =====================================================
        public void Upsert(string symbol, KlineInterval tf, BinanceFuturesUsdtKline kline)
        {
            if (kline == null)
                return;

            var key = MakeKey(symbol, tf);
            var list = _buffers.GetOrAdd(key, _ => new LinkedList<BinanceFuturesUsdtKline>());

            lock (list)
            {
                // WS update: replace last candle if same OpenTime
                if (list.Last != null && list.Last.Value.OpenTime == kline.OpenTime)
                    list.RemoveLast();

                list.AddLast(kline);

                // bounded memory
                var limit = GetMaxBarsForTf(tf);
                while (list.Count > limit)
                    list.RemoveFirst();
            }
        }

        // =====================================================
        // READ API
        // =====================================================
        public IReadOnlyList<BinanceFuturesUsdtKline> Snapshot(string symbol, KlineInterval tf)
        {
            var key = MakeKey(symbol, tf);
            if (!_buffers.TryGetValue(key, out var list))
                return Array.Empty<BinanceFuturesUsdtKline>();

            lock (list)
                return list.ToList();
        }

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
                if (list.Count <= count)
                    return list.ToList();

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

        public int Count(string symbol, KlineInterval tf)
        {
            var key = MakeKey(symbol, tf);
            if (!_buffers.TryGetValue(key, out var list))
                return 0;

            lock (list)
                return list.Count;
        }

        // =====================================================
        // MAINTENANCE
        // =====================================================
        public void Clear(string symbol, KlineInterval tf)
            => _buffers.TryRemove(MakeKey(symbol, tf), out _);

        public void ClearAll()
            => _buffers.Clear();

        // =====================================================
        // ⚠ LEGACY SNAPSHOT API (used by MarketDataFacade)
        // =====================================================
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

        public Dictionary<string, List<BinanceFuturesUsdtKline>> DumpAll()
        {
            var result = new Dictionary<string, List<BinanceFuturesUsdtKline>>();

            foreach (var kv in _buffers)
            {
                lock (kv.Value)
                    result[kv.Key] = kv.Value.ToList();
            }

            return result;
        }

        public Dictionary<string, List<BinanceFuturesUsdtKline>> ExportLast(int maxPerSeries)
        {
            var result = new Dictionary<string, List<BinanceFuturesUsdtKline>>();

            foreach (var kv in _buffers)
            {
                var list = kv.Value;
                lock (list)
                {
                    if (list.Count == 0)
                        continue;

                    var take = Math.Min(maxPerSeries, list.Count);
                    var arr = new BinanceFuturesUsdtKline[take];

                    var node = list.Last;
                    for (int i = take - 1; i >= 0 && node != null; i--)
                    {
                        arr[i] = node.Value;
                        node = node.Previous;
                    }

                    result[kv.Key] = arr.ToList();
                }
            }

            return result;
        }

        public void Restore(Dictionary<string, List<BinanceFuturesUsdtKline>> data)
        {
            if (data == null || data.Count == 0)
                return;

            foreach (var (key, klines) in data)
            {
                if (klines == null || klines.Count == 0)
                    continue;

                var list = _buffers.GetOrAdd(key, _ => new LinkedList<BinanceFuturesUsdtKline>());

                lock (list)
                {
                    list.Clear();
                    foreach (var k in klines.OrderBy(x => x.OpenTime))
                        list.AddLast(k);
                }
            }
        }
    }
}
