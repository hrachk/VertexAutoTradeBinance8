using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class TradeSignalMemoryService
    {
        private readonly ConcurrentDictionary<string, TradeSignal> _signals = new();

        public void Save(TradeSignal signal)
        {
            _signals[signal.Symbol] = signal;
        }

        public TradeSignal? GetLastSignal(string symbol)
        {
            _signals.TryGetValue(symbol, out var value);
            return value;
        }
    }
}
