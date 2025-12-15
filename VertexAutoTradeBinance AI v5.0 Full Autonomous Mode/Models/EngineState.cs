using System.Collections.Concurrent;

namespace VertexAutoTradeBinance8.Models
{
    public class EngineState
    {
        public string Status { get; set; } = "Unknown";
        public string Mode { get; set; } = "Detecting";
        public decimal BalanceUsdt { get; set; }

        public string Symbol { get; set; } = "";
        public string Timeframe { get; set; } = "";

        public string MarketRegime { get; set; } = "";
        public string SmartRegime { get; set; } = "";

        public decimal Slope { get; set; }
        public decimal Volatility { get; set; }
        public decimal Confidence { get; set; }

 

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
    }
}
