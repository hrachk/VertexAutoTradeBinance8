namespace VertexAutoTradeBinance8.Models
{
    /// <summary>
    /// Результат корреляционного анализа двух инструментов.
    /// </summary>
    public class PairCorrelation
    {
        /// <summary>Первый символ, например "BTCUSDT".</summary>
        public string SymbolA { get; set; } = string.Empty;

        /// <summary>Второй символ, например "ETHUSDT".</summary>
        public string SymbolB { get; set; } = string.Empty;

        /// <summary>
        /// Коэффициент корреляции Пирсона в диапазоне [-1; 1].
        /// 1  = идеально вместе, -1 = идеально противоположно, 0 = нет связи.
        /// </summary>
        public decimal Correlation { get; set; }

        /// <summary>Размер окна (кол-во точек, по которым считали корреляцию).</summary>
        public int Window { get; set; }

        /// <summary>Время расчёта (UTC).</summary>
        public DateTime CalculatedAt { get; set; }
    }
}
