namespace VertexAutoTradeBinance8.Web.Models
{
    public class MissedTradeRecord
    {
        public string Symbol { get; set; } = "";
        public DateTime Time { get; set; }

        public decimal Entry { get; set; }
        public decimal StopLoss { get; set; }

        // NEW: LONG / SHORT (Buy / Sell)
        public string Side { get; set; } = "";

        // NEW: TakeProfit list
        public List<decimal> TakeProfits { get; set; } = new();

        public string Reason { get; set; } = "";

        public decimal FreeBalance { get; set; }

        // NEW: attemptNotional — что бот пытался открыть
        public decimal AttemptNotional { get; set; }

        // NEW: Binance мин. требование для символа
        public decimal RequiredMinNotional { get; set; }

        // === NEW: MARKET REGIME METRICS ===
        public decimal ATR { get; set; }
        public decimal Vol { get; set; }
        public decimal Slope { get; set; }
        public int Confidence { get; set; }
        public string Regime { get; set; } = "";
        public string SmartType { get; set; } = "";

        public decimal Deviation { get; set; }
        public int Score { get; set; }

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
