using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class TradeSignalMemoryService
    {
        private readonly ConcurrentDictionary<string, TradeSignal> _signals = new(StringComparer.OrdinalIgnoreCase);

        public void Save(TradeSignal signal)
        {
            if (signal == null || string.IsNullOrWhiteSpace(signal.Symbol))
                return;
            _signals[signal.Symbol] = signal;
        }

        public TradeSignal? GetLastSignal(string symbol)
        {
            _signals.TryGetValue(symbol, out var value);
            return value;
        }

        public void Clear(string symbol) => _signals.TryRemove(symbol, out _);

        /// <summary>Бот-управляемый сигнал (не manual virtual).</summary>
        public bool HasBotSignal(string symbol)
        {
            var s = GetLastSignal(symbol);
            return s != null && !s.IsManual;
        }
    }
}
