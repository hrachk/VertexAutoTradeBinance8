using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Per-symbol trade memory for cooldown / losing-streak gates.
    /// Streaks DECAY with time so the bot does not permanently mute symbols
    /// after a bad session or a violent BTC impulse day.
    /// </summary>
    public class TradeStateManager
    {
        private readonly ConcurrentDictionary<string, SymbolState> _states = new();

        /// <summary>How long a losing-streak lockout lasts before auto-clear (hours).</summary>
        public static readonly TimeSpan StreakLockoutTtl = TimeSpan.FromHours(3);

        /// <summary>After this idle time without a new stop, streak counter decays by 1.</summary>
        public static readonly TimeSpan StreakDecayEvery = TimeSpan.FromHours(2);

        public SymbolState Get(string symbol)
            => _states.GetOrAdd(symbol, _ => new SymbolState());

        public void RegisterStop(string symbol)
        {
            var s = Get(symbol);
            DecayStreak(s);
            s.LastStopTimeUtc = DateTime.UtcNow;
            s.LosingStreak++;
            s.HadStopInsideHtf = true;
            s.StreakLockoutUntilUtc = DateTime.UtcNow + StreakLockoutTtl;
        }

        public void RegisterWin(string symbol)
        {
            var s = Get(symbol);
            s.LosingStreak = 0;
            s.StreakLockoutUntilUtc = null;
            s.LastStopTimeUtc = null; // clear stop cooldown on win
        }

        public void RegisterHtfContext(string symbol, DateTime open)
        {
            var s = Get(symbol);
            if (s.LastHtfOpenTimeUtc != open)
            {
                s.LastHtfOpenTimeUtc = open;
                s.HadStopInsideHtf = false;
            }
        }

        public bool IsInCooldown(string symbol, int minutes)
        {
            var s = Get(symbol);
            DecayStreak(s);
            if (s.LastStopTimeUtc == null)
                return false;
            return DateTime.UtcNow - s.LastStopTimeUtc.Value < TimeSpan.FromMinutes(minutes);
        }

        /// <summary>
        /// True only while lockout window is active AND streak still high.
        /// No longer permanent until a win (that froze symbols for days).
        /// </summary>
        public bool IsLosingStreakLimit(string symbol, int max)
        {
            var s = Get(symbol);
            DecayStreak(s);
            if (s.LosingStreak < max)
                return false;
            if (s.StreakLockoutUntilUtc == null || DateTime.UtcNow >= s.StreakLockoutUntilUtc)
            {
                // Lockout expired — soft reset half the streak so trading can resume
                s.LosingStreak = Math.Max(0, max / 2);
                s.StreakLockoutUntilUtc = null;
                return false;
            }
            return true;
        }

        public bool BlockedReentryLtf(string symbol, DateTime htfOpen)
        {
            var s = Get(symbol);
            return s.LastHtfOpenTimeUtc == htfOpen && s.HadStopInsideHtf;
        }

        private static void DecayStreak(SymbolState s)
        {
            if (s.LosingStreak <= 0 || s.LastStopTimeUtc == null)
                return;
            var elapsed = DateTime.UtcNow - s.LastStopTimeUtc.Value;
            if (elapsed < StreakDecayEvery)
                return;
            int steps = (int)(elapsed.TotalHours / StreakDecayEvery.TotalHours);
            if (steps <= 0) return;
            s.LosingStreak = Math.Max(0, s.LosingStreak - steps);
            if (s.LosingStreak == 0)
                s.StreakLockoutUntilUtc = null;
        }
    }
}
