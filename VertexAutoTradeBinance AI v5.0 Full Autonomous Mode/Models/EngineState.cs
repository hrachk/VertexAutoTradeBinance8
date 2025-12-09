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

        // --- Smart regime extras ---
        public decimal CorrelationToBtc { get; set; }
        public bool DangerChop { get; set; }
        public bool AllowAggressive { get; set; }
        public bool AllowCounter { get; set; }

        // --- Liquidity ---
        public bool LiquidityDanger { get; set; }
        public string LiquidityReason { get; set; } = "";
        public string LiquidityType { get; set; } = "";

        // --- Order decisions / results ---
        public decimal? AiGrade { get; set; }
        public bool? AiAllow { get; set; }
        public decimal? CalculatedQty { get; set; }
        public bool? OrderSuccess { get; set; }
        public string? OrderError { get; set; }

        // --- Strategy flags ---
        public bool SoftEntry { get; set; }
        public bool BlockedByLiquidity { get; set; }

        public DateTime LastUpdate { get; set; }
    }
}
