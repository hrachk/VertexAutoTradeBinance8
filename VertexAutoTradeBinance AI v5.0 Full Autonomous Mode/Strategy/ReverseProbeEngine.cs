using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Strategy
{
    /// <summary>
    /// ReverseProbeEngine (PRO)
    /// Micro-probe против предыдущего тренда:
    /// - ТОЛЬКО после защиты (BE или close)
    /// - 5–10% обычного риска
    /// - Без market-flip
    /// </summary>
    public class ReverseProbeEngine
    {


        private static readonly ConcurrentDictionary<string, DateTime> _lastProbeUtc = new();

        public bool CanProbeNow(string symbol)
        {
            if (_lastProbeUtc.TryGetValue(symbol, out var last) &&
                (DateTime.UtcNow - last) < TimeSpan.FromMinutes(5))
                return false;

            return true;
        }

        public void MarkProbe(string symbol)
        {
            _lastProbeUtc[symbol] = DateTime.UtcNow;
        }
         
    }
}
