using System.Text.Json.Serialization;

namespace VertexAutoTradeBinance8.Models
{
    /// <summary>
    /// Режим рынка на инструменте.
    /// </summary>
    public enum MarketRegime
    {
        Unknown = 0,

        /// <summary>Сильный тренд вверх, импульсный.</summary>
        StrongUpTrend = 1,

        /// <summary>Сильный тренд вниз, импульсный.</summary>
        StrongDownTrend = 2,

        /// <summary>Флет/диапазон, движение внутри боковика.</summary>
        Range = 3,

        /// <summary>Шумная пила: высокая волатильность, но без направленного тренда.</summary>
        VolatileChop = 4,
        Squeeze = 5,
        UpTrend = 6,       // ← НУЖНЫ ДЛЯ v6
        DownTrend = 7 ,     // ← НУЖНЫ ДЛЯ v6
           
        ExpansionDistribution = 8   // 🔥 новый режим  protiv suka MM`s
    }

   

    /// <summary>
    /// Результат детекции режима рынка.
    /// </summary>
    public class MarketRegimeResult
    {
        public string Symbol { get; set; } = string.Empty;
        public string Timeframe { get; set; } = string.Empty;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public MarketRegime Regime { get; set; } = MarketRegime.Unknown;

        /// <summary>Угол наклона тренда (EMA) в % на N свечей.</summary>
        public decimal TrendSlopePercent { get; set; }

        /// <summary>Отношение ATR к цене (волатильность в %).</summary>
        public decimal VolatilityPercent { get; set; }

        /// <summary>Насколько далеко текущая цена от средней (z-score).</summary>
        public decimal DeviationScore { get; set; }

        public override string ToString()
            => $"{Regime} (slope={TrendSlopePercent:P2}, vol={VolatilityPercent:P2}, dev={DeviationScore:F2})";
        public decimal Confidence { get; set; }
    }
}
