namespace VertexAutoTradeBinance8.Services.Learning;

public sealed class TradeJournalEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ClientId { get; set; } = "";
    public string Source { get; set; } = "Demo";
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "";
    public decimal EntryPrice { get; set; }
    public decimal ExitPrice { get; set; }
    public decimal Qty { get; set; }
    public int Leverage { get; set; }
    public decimal? StopLoss { get; set; }
    public List<decimal> TakeProfits { get; set; } = new();
    public decimal RealizedPnl { get; set; }
    public decimal RealizedR { get; set; }
    public string CloseReason { get; set; } = "";
    public decimal SignalConf { get; set; }
    public DateTime OpenedAtUtc { get; set; }
    public DateTime ClosedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class TradeJournalFile
{
    public List<TradeJournalEntry> Entries { get; set; } = new();
}

public sealed class SymbolAdjustments
{
    public string Symbol { get; set; } = "";
    public decimal SizeMult { get; set; } = 1m;
    public decimal SlPadAtr { get; set; } = 0m;
    public decimal TpScale { get; set; } = 1m;
    public decimal ConfMult { get; set; } = 1m;
    public decimal LevMult { get; set; } = 1m;
    public int RecentTrades { get; set; }
    public int RecentStops { get; set; }
    public int RecentWins { get; set; }
    public string Note { get; set; } = "";
}

public sealed class SymbolMemoryFile
{
    public Dictionary<string, SymbolAdjustments> BySymbol { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
