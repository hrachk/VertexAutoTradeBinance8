using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Snapshot state for QUANT-REALTIME (v6)
    /// </summary>
    public class AiLearningSnapshot
    {
        public DateTime CreatedAtUtc { get; set; }

        public List<AiSymbolStatsDto> Symbols { get; set; } = new();

        public class AiSymbolStatsDto
        {
            public string Symbol { get; set; } = string.Empty;
            public List<AiRegimeStatsDto> Regimes { get; set; } = new();
        }

        public class AiRegimeStatsDto
        {
            public MarketRegime Regime { get; set; }
            public int Trades { get; set; }
            public int Wins { get; set; }

            public decimal SumRr { get; set; }
            public decimal MaxRr { get; set; }
            public decimal MinRr { get; set; }

            public DateTime LastUpdateUtc { get; set; }
        }
    }

    // ========================================================================
    // SNAPSHOT MODEL (общий для TradingWorker v6 и AiModelSnapshotService)
    // ========================================================================
    //public class AiLearningSnapshot
    //{
    //    public DateTime CreatedAtUtc { get; set; }
    //    public List<AiSymbolStatsDto> Symbols { get; set; } = new();

    //    public class AiSymbolStatsDto
    //    {
    //        public string Symbol { get; set; } = string.Empty;
    //        public List<AiRegimeStatsDto> Regimes { get; set; } = new();
    //    }

    //    public class AiRegimeStatsDto
    //    {
    //        public MarketRegime Regime { get; set; }
    //        public int Trades { get; set; }
    //        public int Wins { get; set; }

    //        public decimal SumRr { get; set; }
    //        public decimal MaxRr { get; set; }
    //        public decimal MinRr { get; set; }

    //        public DateTime LastUpdateUtc { get; set; }
    //    }
    //}
}
