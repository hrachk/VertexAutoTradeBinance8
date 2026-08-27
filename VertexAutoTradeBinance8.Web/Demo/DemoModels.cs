namespace VertexAutoTradeBinance8.Web.Demo;

public sealed class DemoPosition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = ""; // "LONG" or "SHORT"
    public int Leverage { get; set; }
    public decimal Qty { get; set; }
    /// <summary>Qty at open (and after adds). TP.Pct is percent of this, not of remaining.</summary>
    public decimal InitialQty { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal Margin { get; set; }
    public DateTime OpenedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>CORE setup at open, e.g. CORE_TREND_LONG — for journal learning.</summary>
    public string Setup { get; set; } = "";
    /// <summary>Best favorable price move from entry (absolute) while position open.</summary>
    public decimal MaxFavorable { get; set; }
    /// <summary>Worst adverse price move from entry (absolute) while position open.</summary>
    public decimal MaxAdverse { get; set; }

    // Embedded protective levels — unlike the real exchange, demo SL/TP
    // aren't separate algo orders; they're monitored directly against
    // this position by DemoEngineService on every live price tick.
    public decimal? StopLoss { get; set; }
    public List<DemoTpLevel> TakeProfits { get; set; } = new();

    public decimal Notional(decimal markPrice) => Qty * markPrice;

    public decimal UnrealizedPnl(decimal markPrice)
    {
        decimal dir = Side == "LONG" ? 1m : -1m;
        return (markPrice - EntryPrice) * dir * Qty;
    }

    public decimal Roi(decimal markPrice) => Margin > 0 ? UnrealizedPnl(markPrice) / Margin : 0m;

    // Simplified isolated-margin liquidation estimate: the price move
    // (in the losing direction) that would erase the full margin.
    // Real exchanges adjust this slightly for maintenance margin rate
    // tiers, but for a demo account this approximation is more than
    // close enough to convey real risk, without needing the exchange's
    // full tiered-margin table.
    public decimal LiqPrice
    {
        get
        {
            if (Qty <= 0 || EntryPrice <= 0) return 0m;
            decimal priceMove = Margin / Qty;
            return Side == "LONG" ? EntryPrice - priceMove : EntryPrice + priceMove;
        }
    }
}

public sealed class DemoTpLevel
{
    public decimal Price { get; set; }
    public decimal Pct { get; set; } = 100m; // % of the position's ORIGINAL qty to close here
}

public enum DemoOrderType { Limit, StopMarket }

// A pending demo order — a limit entry or a stop-trigger entry that
// hasn't filled yet. Once price crosses the trigger, DemoEngineService
// converts this into a DemoPosition (or adds to an existing one).
public sealed class DemoPendingOrder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = ""; // "LONG" or "SHORT" — the position side this would open
    public DemoOrderType Type { get; set; }
    public decimal TriggerPrice { get; set; }
    public decimal Qty { get; set; }
    /// <summary>Qty at open (and after adds). TP.Pct is percent of this, not of remaining.</summary>
    public decimal InitialQty { get; set; }
    public int Leverage { get; set; }
    public decimal? StopLoss { get; set; }
    public List<DemoTpLevel> TakeProfits { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class DemoClosedTrade
{
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "";
    public decimal EntryPrice { get; set; }
    public decimal ExitPrice { get; set; }
    public decimal Qty { get; set; }
    /// <summary>Qty at open (and after adds). TP.Pct is percent of this, not of remaining.</summary>
    public decimal InitialQty { get; set; }
    public decimal RealizedPnl { get; set; }
    public string CloseReason { get; set; } = ""; // "SL" / "TP1" / "Manual" / etc
    public DateTime OpenedAtUtc { get; set; }
    public DateTime ClosedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class DemoDcaPurchaseRecord
{
    public string Symbol { get; set; } = "";
    public DateTime TimeUtc { get; set; }
    public decimal Price { get; set; }
    public decimal Qty { get; set; }
    /// <summary>Qty at open (and after adds). TP.Pct is percent of this, not of remaining.</summary>
    public decimal InitialQty { get; set; }
    public decimal UsdtSpent { get; set; }
    public bool DipBonusApplied { get; set; }
}

public sealed class DemoDcaState
{
    public DateTime LastCycleUtc { get; set; }
    public List<DemoDcaPurchaseRecord> History { get; set; } = new();
}

// Per-user persisted demo account state.
// Stored at {EnginesRoot}/client_{userId}/demo-account.json
// Isolated per registered user — never shared across accounts.
public sealed class DemoAccountState
{
    public decimal InitialBalance { get; set; } = 10_000m;
    /// <summary>
    /// Wallet balance (Binance-style). Margin is NOT deducted from this field;
    /// available = Balance - sum(position margins). Equity = Balance + unrealized PnL.
    /// </summary>
    public decimal Balance { get; set; } = 10_000m;
    /// <summary>0 = legacy (margin subtracted from Balance on open); 1 = wallet model.</summary>
    public int AccountingVersion { get; set; } = 1;
    public List<DemoPosition> Positions { get; set; } = new();
    public List<DemoPendingOrder> PendingOrders { get; set; } = new();
    public List<DemoClosedTrade> History { get; set; } = new();
}
