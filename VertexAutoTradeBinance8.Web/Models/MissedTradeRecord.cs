using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Web.Models
{
    public class MissedTradeRecord
    {
        public string Symbol { get; set; } = "";
        public DateTime Time { get; set; }

        public string Stage { get; set; } = "";   // legacy field (was never populated)
        /// <summary>
        /// Maps from Engine's "event" JSON field (e.g. "REJECT_AI_BLOCK", "REJECT_COOLDOWN").
        /// Engine writes "Event" → JSON key "event" → this field.
        /// Stage was previously used for filtering but Stage ≠ Event → Stage was always "".
        /// </summary>
        public string Event { get; set; } = "";
        public string Side { get; set; } = "";

        public decimal Entry { get; set; }
        public decimal StopLoss { get; set; }
        public List<decimal> TakeProfits { get; set; } = new();
        public decimal? Exit { get; set; }

        /// <summary>Optional entry-zone bounds — null when the signal
        /// that produced this record didn't populate them (e.g. signals
        /// from functions other than TryPullbackEma21).</summary>
        public decimal? EntryRangeLow { get; set; }
        public decimal? EntryRangeHigh { get; set; }

        public string Reason { get; set; } = "";
        public string Tags { get; set; } = "";

        public decimal FreeBalance { get; set; }
        public decimal AttemptNotional { get; set; }
        public decimal RequiredMinNotional { get; set; }

        // === MARKET REGIME / AI ===
        public MarketRegime Regime { get; set; }
        public decimal ATR { get; set; }
        public decimal Vol { get; set; }
        public decimal Slope { get; set; }
        public int Confidence { get; set; }
        public int Score { get; set; }
        public decimal Deviation { get; set; }

        public string SmartType { get; set; } = "";

    }

    public static class MissedTradeExtensions
    {
        public static Dictionary<string, List<MissedTradeRecord>> GroupBySymbols(this IEnumerable<MissedTradeRecord> records)
            => records
                .GroupBy(x => x.Symbol)
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.ToList());

        public static Dictionary<DateTime, int> GroupByDay(this IEnumerable<MissedTradeRecord> records)
            => records
                .GroupBy(x => x.Time.Date)
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.Count());
    }

}
