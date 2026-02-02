namespace VertexAutoTradeBinance8.Web.Models;

public class EngineStateModel
{
    public string Status { get; set; } = "";
    public string Mode { get; set; } = "";

    public decimal BalanceUsdt { get; set; }
    public decimal RealizedPnlUsd { get; set; }
    public string Symbol { get; set; } = "";
    public string Timeframe { get; set; } = "";

    public string MarketRegime { get; set; } = "";
    public string SmartRegime { get; set; } = "";

    public decimal Slope { get; set; }
    public decimal Volatility { get; set; }

   
    public bool LiquidityDanger { get; set; }
    public string LiquidityReason { get; set; } = "";

    public bool SoftEntry { get; set; }
    public bool BlockedByLiquidity { get; set; }

    public DateTime LastUpdate { get; set; }

    //new 10/01/26
    public DateTime LastEngineTick { get; set; }
    public int CyclesPerMinute { get; set; }
    public int SupervisorChecksLastMinute { get; set; }
    public DateTime LastSupervisorAction { get; set; }
    public string LastSupervisorMessage { get; set; }
    public string LastEntryDecision { get; set; }
    // examples: BLOCKED_RANGE, WAITING_PULLBACK, LOW_CONFIDENCE
    public DateTime LastDecisionTime { get; set; }
    public int TrackedSymbols { get; set; }
    public int UniverseSize { get; set; }
    public int OpenPositions { get; set; }

    public decimal ConfidenceRaw { get; set; }
    public int ConfidencePercent { get; set; }
    public string ConfidenceLevel { get; set; }
    // LOW / MEDIUM / HIGH


}
