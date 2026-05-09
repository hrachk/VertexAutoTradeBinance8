namespace VertexAutoTradeBinance8.Models
{
    public enum LiquidityClusterSide
    {
        Bid = 0,
        Ask = 1
    }

    /// <summary>
    /// Кластер ликвидности в стакане (крупная плотность ордеров).
    /// </summary>
    public class LiquidityCluster
    {
        public string Symbol { get; set; } = string.Empty;

        /// <summary>Bid / Ask сторона.</summary>
        public LiquidityClusterSide Side { get; set; }

        /// <summary>Цена уровня.</summary>
        public decimal Price { get; set; }

        /// <summary>Количество (qty) на уровне.</summary>
        public decimal Quantity { get; set; }

        /// <summary>Нотионал уровня: Price * Quantity (в USDT).</summary>
        public decimal Notional { get; set; }

        /// <summary>
        /// Расстояние от mid-цены в процентах (0.001 = 0.1%).
        /// Положительное значение — выше mid, отрицательное — ниже.
        /// </summary>
        public decimal DistanceFromMidPercent { get; set; }

        /// <summary>Является ли это "мажорным" кластером (сильно выделяется по объёму).</summary>
        public bool IsMajor { get; set; }
    }

    /// <summary>
    /// Результат анализа стакана для сигнала.
    /// </summary>
    public class LiquidityAnalysisResult
    {
        public string Symbol { get; set; } = string.Empty;

        public List<LiquidityCluster> Clusters { get; set; } = new();

        /// <summary>Суммарный нотионал по bid-стороне.</summary>
        public decimal BidNotional { get; set; }

        /// <summary>Суммарный нотионал по ask-стороне.</summary>
        public decimal AskNotional { get; set; }

        /// <summary>
        /// Дисбаланс: (Bid - Ask) / (Bid + Ask).
        /// > 0 — перевес покупателей; < 0 — продавцов.
        /// </summary>
        public decimal Imbalance { get; set; }

        /// <summary>Опасная ли сейчас структура стакана для входа.</summary>
        public bool IsDangerZone { get; set; }

        /// <summary>Рекомендуемый более безопасный стоп (если найден).</summary>
        public decimal? SuggestedStopLoss { get; set; }

        /// <summary>Рекомендуемая корректировка entry (если нужна).</summary>
        public decimal? SuggestedEntry { get; set; }

        /// <summary>Причина флага danger / изменения SL/Entry.</summary>
        public string? Reason { get; set; }
    }
}
