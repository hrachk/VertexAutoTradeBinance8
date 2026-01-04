public class TradeJournalCard
{
    public string Symbol { get; set; }
    public string Side { get; set; }
    public decimal Entry { get; set; }
    public decimal? Sl { get; set; }
    public decimal? Tp { get; set; }

    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }

    public string Status { get; set; } // ATTEMPT / FAILED / PROFIT / LOSS
    public string? Reason { get; set; }
    public decimal? Result { get; set; }

    public List<string> Lifecycle { get; set; } = new();
}
