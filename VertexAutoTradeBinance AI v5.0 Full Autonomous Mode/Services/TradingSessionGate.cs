using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Разрешает НОВЫЕ входы только в эффективных сессиях (London + New York + early start).
    /// Вне окон: observation only — supervisor / анализ продолжают работать.
    /// </summary>
    public class TradingSessionGate
    {
        private readonly ILogger<TradingSessionGate> _logger;
        private readonly IOptionsMonitor<TradingOptions> _optionsMon;
        private TradingOptions _options => _optionsMon.CurrentValue;
        private DateTime _lastLogUtc = DateTime.MinValue;

        public TradingSessionGate(
            ILogger<TradingSessionGate> logger,
            IOptionsMonitor<TradingOptions> options)
        {
            _logger = logger;
            _optionsMon = options;
        }

        public bool IsTradingAllowed(out string reason, out string? activeSession)
        {
            reason = "";
            activeSession = null;

            var cfg = _options.TradingSessions;

            // Weekend chop: Sat/Sun UTC — no new entries (major cause of SL cascades)
            if (cfg != null && cfg.BlockWeekends)
            {
                var dow = DateTime.UtcNow.DayOfWeek;
                if (dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday)
                {
                    reason = $"weekend block ({dow} UTC) — observation only";
                    return false;
                }
            }

            if (cfg == null || !cfg.Enabled)
            {
                reason = "sessions filter off";
                activeSession = "ALL";
                return true;
            }

            var windows = cfg.Windows;
            if (windows == null || windows.Count == 0)
            {
                reason = "no windows configured — allow";
                activeSession = "ALL";
                return true;
            }

            var now = DateTime.UtcNow;
            var earlyMin = Math.Max(0, cfg.EarlyStartMinutes);
            var matched = new List<string>();

            foreach (var w in windows)
            {
                if (!TryParseHm(w.StartUtc, out var start) || !TryParseHm(w.EndUtc, out var end))
                    continue;

                var effStart = SubtractMinutes(start, earlyMin);
                if (IsInWindow(now.TimeOfDay, effStart, end))
                    matched.Add(w.Name);
            }

            if (matched.Count > 0)
            {
                activeSession = string.Join("+", matched.Distinct());
                reason = $"in session [{activeSession}] UTC now={now:HH:mm}";
                return true;
            }

            var next = FindNextOpen(now, windows, earlyMin);
            reason = next.HasValue
                ? $"outside sessions — next open ~{next.Value:dd HH:mm} UTC (observation only)"
                : "outside sessions — observation only";
            return false;
        }

        public void LogStatusThrottled()
        {
            if ((DateTime.UtcNow - _lastLogUtc).TotalMinutes < 15)
                return;

            _lastLogUtc = DateTime.UtcNow;
            var allowed = IsTradingAllowed(out var reason, out _);
            _logger.LogInformation(
                allowed ? "[SESSION] TRADING ON — {reason}" : "[SESSION] OBSERVE ONLY — {reason}",
                reason);
        }

        private static bool IsInWindow(TimeSpan now, TimeSpan start, TimeSpan end)
        {
            if (start <= end)
                return now >= start && now < end;
            // crosses midnight
            return now >= start || now < end;
        }

        private static TimeSpan SubtractMinutes(TimeSpan start, int minutes)
        {
            var ticks = start.Ticks - TimeSpan.FromMinutes(minutes).Ticks;
            if (ticks < 0)
                ticks += TimeSpan.TicksPerDay;
            return TimeSpan.FromTicks(ticks);
        }

        private static DateTime? FindNextOpen(
            DateTime nowUtc,
            List<SessionWindowOptions> windows,
            int earlyMinutes)
        {
            DateTime? best = null;

            for (int day = 0; day <= 2; day++)
            {
                var date = nowUtc.Date.AddDays(day);
                foreach (var w in windows)
                {
                    if (!TryParseHm(w.StartUtc, out var start))
                        continue;

                    var eff = SubtractMinutes(start, earlyMinutes);
                    var candidate = date + eff;
                    if (candidate <= nowUtc)
                        continue;

                    if (best == null || candidate < best)
                        best = candidate;
                }
            }

            return best;
        }

        private static bool TryParseHm(string? s, out TimeSpan ts)
        {
            ts = default;
            if (string.IsNullOrWhiteSpace(s))
                return false;

            if (TimeSpan.TryParse(s, out ts))
                return true;

            var parts = s.Trim().Split(':');
            if (parts.Length >= 2 &&
                int.TryParse(parts[0], out var h) &&
                int.TryParse(parts[1], out var m) &&
                h is >= 0 and < 24 && m is >= 0 and < 60)
            {
                ts = new TimeSpan(h, m, 0);
                return true;
            }

            return false;
        }
    }
}
