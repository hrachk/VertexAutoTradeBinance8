namespace VertexAutoTradeBinance8.Models
{
    public class AiLearningRecord
    {
        //public DateTime Time { get; set; }
        //public string Symbol { get; set; } = string.Empty;

        //public string Reason { get; set; } = string.Empty;
        //public decimal Score { get; set; }
        //public decimal Confidence { get; set; }

        //public string Regime { get; set; } = string.Empty;
        //public string SmartType { get; set; } = string.Empty;

        //public decimal Atr { get; set; }
        //public decimal Slope { get; set; }
        //public decimal Volatility { get; set; }
        //public decimal Deviation { get; set; }

        public string Symbol { get; set; } = "";
        public DateTime Time { get; set; }

        // Основная причина обучения
        public string Reason { get; set; } = "";

        // Уверенность (0–100)
        public int Confidence { get; set; }

        // Тот же Confidence, но используем как Score
        public int Score { get; set; }

        // Рыночный режим (StrongUpTrend, Range и т.д.)
        public string Regime { get; set; } = "-";

        // Тип сигнала (SOFT_ENTRY, RR_BLOCK, BASE_REGIME и т.д.)
        public string SmartType { get; set; } = "-";

        // Волатильность (в %)
        public decimal VolatilityPercent { get; set; }

        // Наклон тренда (%)
        public decimal TrendSlopePercent { get; set; }

        // ATR сигнала
        public decimal Atr { get; set; }
    }
}
