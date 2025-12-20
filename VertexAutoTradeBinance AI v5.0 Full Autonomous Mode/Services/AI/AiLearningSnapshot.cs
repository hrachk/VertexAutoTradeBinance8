using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Snapshot state for QUANT-REALTIME (v8)
    /// Единый DTO:
    ///  - Meta
    ///  - Symbols (агрегированная статистика по режимам)
    ///  - MarketStates (последние N состояний рынка)
    ///  - Trades (последние N сделок)
    /// </summary>
    public class AiLearningSnapshot
    {
        public DateTime CreatedAtUtc { get; set; }
        public int SnapshotVersion { get; set; }

        public AiLearningMeta Meta { get; set; } = new();

        public List<AiSymbolStatsDto> Symbols { get; set; } = new();

        // последние N состояний рынка для Dashboard / анализа
        public List<AiSelfLearningService.MarketState> MarketStates { get; set; } = new();

        // последние N сделок для Dashboard / анализа
        public List<AiSelfLearningService.TradeHistoryEntry> Trades { get; set; } = new();

        public List<DecisionGateSnapshot>? DecisionGates { get; set; }
    }

    public class DecisionGateSnapshot
    {
        public string Symbol { get; set; } = "";
        public MarketRegime Regime { get; set; }
        public string Gate { get; set; } = "";
        public int Hits { get; set; }
        public int Blocks { get; set; }
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
