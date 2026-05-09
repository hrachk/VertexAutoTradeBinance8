using System.Collections.Generic;

namespace VertexAutoTradeBinance8.Models
{
    public class AiLearningState
    {
        public int Version { get; set; } = 2;

        public Dictionary<string, decimal> SymbolWinRate { get; set; } = new();
        public Dictionary<string, decimal> AvgAtr { get; set; } = new();
        public Dictionary<string, decimal> AvgSlDistance { get; set; } = new();
        public Dictionary<string, decimal> DynamicRiskWeight { get; set; } = new();

        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }
}
