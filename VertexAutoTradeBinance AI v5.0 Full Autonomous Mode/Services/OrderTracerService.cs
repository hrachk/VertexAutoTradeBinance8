using System.Collections.Concurrent;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Память для ордеров, которые поставил бот (SL/TP/прочие защитные).
    /// Нужна, чтобы потом можно было корректно чистить только "свои" ордера.
    /// </summary>
    public class OrderTracerService
    {
        // key: SYMBOL, value: набор ордеров бота
        private readonly ConcurrentDictionary<string, HashSet<long>> _orders =
            new(StringComparer.OrdinalIgnoreCase);

        public void Register(string symbol, long orderId)
        {
            var set = _orders.GetOrAdd(symbol, _ => new HashSet<long>());
            lock (set)
            {
                set.Add(orderId);
            }
        }

        public void Remove(string symbol, long orderId)
        {
            if (_orders.TryGetValue(symbol, out var set))
            {
                lock (set)
                {
                    set.Remove(orderId);
                }
            }
        }

        public IReadOnlyCollection<long> GetKnownOrderIds(string symbol)
        {
            if (_orders.TryGetValue(symbol, out var set))
            {
                lock (set)
                {
                    return set.ToList();
                }
            }

            return Array.Empty<long>();
        }

        public IReadOnlyCollection<string> GetAllSymbols()
        {
            return _orders.Keys.ToList();
        }

        public void ClearSymbol(string symbol)
        {
            _orders.TryRemove(symbol, out _);
        }
    }
}
