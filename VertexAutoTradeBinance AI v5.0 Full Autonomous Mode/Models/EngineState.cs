using Binance.Net.Enums;
using System.Collections.Concurrent;

namespace VertexAutoTradeBinance8.Models
{
    public class EngineState
    {
        public string Status { get; set; } = "Unknown";
        public string Mode { get; set; } = "Detecting";
        public decimal BalanceUsdt { get; set; }

        public string Symbol { get; set; } = "";
        public KlineInterval Timeframe { get; set; }

        public string MarketRegime { get; set; } = "";
        public string SmartRegime { get; set; } = "";

        public decimal Slope { get; set; }
        public decimal Volatility { get; set; }
      
 

        // --- Liquidity ---
        public bool LiquidityDanger { get; set; }
        public string LiquidityReason { get; set; } = "";
  

        // --- Strategy flags ---
        public bool SoftEntry { get; set; }
        public bool BlockedByLiquidity { get; set; }

        public DateTime LastUpdate { get; set; } 

        public ConcurrentDictionary<string, SymbolState> Symbols { get; set; } = new();

        public decimal EquityUsd { get; set; }
        public decimal UsedMarginUsd { get; set; }

        public static string Key(string symbol) => symbol.ToUpperInvariant();


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
        public decimal EngineEquityUsd { get; set; }
        // === CAPITAL ANCHORS ===
        // депозит из Trading.Deposit (НЕ МЕНЯЕТСЯ во время работы)
        public decimal BaseDepositUsd { get; set; }
        // накопленный REALIZED PnL (только зафиксированный)
        public decimal RealizedPnlUsd { get; set; }
    }
}
