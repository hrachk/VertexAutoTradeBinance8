using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class TradeStateManager
    {
        private readonly ConcurrentDictionary<string, SymbolState> _states = new();

        public SymbolState Get(string symbol)
            => _states.GetOrAdd(symbol, _ => new SymbolState());

        public void RegisterStop(string symbol)
        {
            var s = Get(symbol);
            s.LastStopTimeUtc = DateTime.UtcNow;
            s.LosingStreak++;
            s.HadStopInsideHtf = true;
        }

        public void RegisterWin(string symbol)
        {
            var s = Get(symbol);
            s.LosingStreak = 0;
        }

        public void RegisterHtfContext(string symbol, DateTime open)
        {
            var s = Get(symbol);

            // новая свеча HTF → сбрасываем внутрисвечные запреты
            if (s.LastHtfOpenTimeUtc != open)
            {
                s.LastHtfOpenTimeUtc = open;
                s.HadStopInsideHtf = false;
            }
        }

        public bool IsInCooldown(string symbol, int minutes)
        {
            var s = Get(symbol);
            if (s.LastStopTimeUtc == null)
                return false;

            return DateTime.UtcNow - s.LastStopTimeUtc < TimeSpan.FromMinutes(minutes);
        }

        public bool IsLosingStreakLimit(string symbol, int max)
        {
            var s = Get(symbol);
            return s.LosingStreak >= max;
        }

        public bool BlockedReentryLtf(string symbol, DateTime htfOpen)
        {
            var s = Get(symbol);
            return s.LastHtfOpenTimeUtc == htfOpen && s.HadStopInsideHtf;
        }
    } 
}
