using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Snapshot state for QUANT-REALTIME (v6)
    /// </summary>
    public class AiLearningSnapshot
    {
        public DateTime CreatedAtUtc { get; set; }
        public int SnapshotVersion { get; set; }
        public AiLearningMeta Meta { get; set; } = new();
        public List<AiSymbolStatsDto> Symbols { get; set; } = new();
    }

    public class AiLearningMeta
    {
        public string Engine { get; set; } = "";
        public int Symbols { get; set; }
        public int MarketStates { get; set; }
        public int Trades { get; set; }
    }
     
    public class AiSymbolStatsDto
    {
        public string Symbol { get; set; } = "";
        public List<AiRegimeStatsDto> Regimes { get; set; } = new();
    }

    public class AiRegimeStatsDto
    {
        public MarketRegime Regime { get; set; }
        public int Trades { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        public decimal AvgPnl { get; set; } 

        public decimal SumRr { get; set; }
        public decimal MaxRr { get; set; }
        public decimal MinRr { get; set; }
        public DateTime LastUpdateUtc { get; set; }
    }
}
