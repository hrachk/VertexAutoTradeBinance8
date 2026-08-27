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

    /// <summary>CORE setup that opened the trade, e.g. CORE_TREND_LONG.</summary>
    public string Setup { get; set; } = "";

    /// <summary>Max favorable excursion in price units (best mark vs entry while open).</summary>
    public decimal Mfe { get; set; }

    /// <summary>Max adverse excursion in price units (worst mark vs entry while open).</summary>
    public decimal Mae { get; set; }
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

    /// <summary>Prefer SL beyond recent swing structure (not ATR-only pad).</summary>
    public bool PreferStructureSl { get; set; }

    /// <summary>
    /// Setups with repeated pure-SL and low MFE — soft-skip on next emit only for these reasons.
    /// Conf of the symbol is NOT cut; other setups still fire.
    /// </summary>
    public List<string> SoftSkipSetups { get; set; } = new();
}

public sealed class SymbolMemoryFile
{
    public Dictionary<string, SymbolAdjustments> BySymbol { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
