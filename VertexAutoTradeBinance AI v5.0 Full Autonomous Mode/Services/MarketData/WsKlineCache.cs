using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using System;
using System.Collections.Concurrent;
using System.Linq;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services.MarketData;

public class WsKlineCache
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<BinanceFuturesUsdtKline>> _map = new();
    private readonly ConcurrentDictionary<string, int> _limits = new();

    private static string Key(string symbol, KlineInterval interval)
        => $"{symbol.ToUpperInvariant()}|{interval}";

    public void SetLimit(string symbol, KlineInterval interval, int limit)
        => _limits[Key(symbol, interval)] = Math.Clamp(limit, 50, 1500);

    public IReadOnlyList<BinanceFuturesUsdtKline> Get(string symbol, KlineInterval interval, int limit)
    {
        var k = Key(symbol, interval);
        if (!_map.TryGetValue(k, out var q) || q.IsEmpty)
            return Array.Empty<BinanceFuturesUsdtKline>();

        // Снимок очереди (дёшево и безопасно)
        var arr = q.ToArray();
        if (arr.Length == 0) return Array.Empty<BinanceFuturesUsdtKline>();

        // Берём последние limit
        if (limit <= 0) limit = 200;
        if (arr.Length <= limit) return arr;

        return arr.Skip(arr.Length - limit).ToArray();
    }

    public void Upsert(string symbol, KlineInterval interval, BinanceFuturesUsdtKline kline)
    {
        var key = Key(symbol, interval);
        var q = _map.GetOrAdd(key, _ => new ConcurrentQueue<BinanceFuturesUsdtKline>());

        // remove duplicates by OpenTime (очередь небольшая, делаем прагматично)
        // (не O(1), но на лимитах 200–500 ок)
        var snapshot = q.ToArray();
        if (snapshot.Any(x => x.OpenTime == kline.OpenTime))
        {
            // перестроим очередь, заменив свечу
            var rebuilt = new ConcurrentQueue<BinanceFuturesUsdtKline>();
            foreach (var x in snapshot)
                rebuilt.Enqueue(x.OpenTime == kline.OpenTime ? kline : x);
            _map[key] = rebuilt;
            Trim(key);
            return;
        }

        q.Enqueue(kline);
        Trim(key);
    }

    private void Trim(string key)
    {
        if (!_map.TryGetValue(key, out var q)) return;

        var limit = _limits.TryGetValue(key, out var l) ? l : 400;
        while (q.Count > limit && q.TryDequeue(out _)) { }
    }
}
