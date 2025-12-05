namespace VertexAutoTradeBinance8.Web.Models
{
    public class MissedTradeRecord
    {
        public string Symbol { get; set; } = "";
        public DateTime Time { get; set; }

        public decimal Entry { get; set; }
        public decimal StopLoss { get; set; }

        public string Reason { get; set; } = "";

        public decimal FreeBalance { get; set; }
        public decimal RequiredNotional { get; set; }
        public decimal MinNotional { get; set; }
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
