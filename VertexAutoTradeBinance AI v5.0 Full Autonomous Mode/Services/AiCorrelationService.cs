using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// AI-сервис для расчёта матрицы корреляций между монетами.
    /// НЕ тянет данные сам – только математика.
    /// Данные (ряды цен/доходностей) передаёт внешний код (StrategyEngine, MarketDataService).
    /// </summary>
    public class AiCorrelationService
    {
        private readonly ILogger<AiCorrelationService> _logger;

        /// <summary>Последний рассчитанный снимок корреляций (read-only).</summary>
        public IReadOnlyList<PairCorrelation> LastSnapshot => _lastSnapshot;
        private List<PairCorrelation> _lastSnapshot = new();

        public AiCorrelationService(ILogger<AiCorrelationService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Рассчитать корреляции для заданных рядов цен/доходностей.
        /// На вход подаём словарь: символ → последовательность цен/лог-доходностей.
        /// </summary>
        /// <param name="seriesBySymbol">
        /// Ключ – символ ("BTCUSDT"), значение – список чисел (цены закрытия или доходности).
        /// Все ряды должны быть примерно одинаковой длины.
        /// </param>
        /// <param name="window">
        /// Размер окна (количество последних точек, по которым считать корреляцию).
        /// Если 0 или больше длины ряда – берём всю длину.
        /// </param>
        public IReadOnlyList<PairCorrelation> CalculateSnapshot(
            IDictionary<string, IReadOnlyList<decimal>> seriesBySymbol,
            int window = 100)
        {
            var result = new List<PairCorrelation>();
            var keys = seriesBySymbol.Keys.OrderBy(k => k).ToList();

            if (keys.Count < 2)
            {
                _logger.LogWarning("AiCorrelationService: not enough symbols to compute correlations.");
                _lastSnapshot = result;
                return result;
            }

            foreach (var a in keys)
            {
                if (!seriesBySymbol.TryGetValue(a, out var sa) || sa == null || sa.Count < 2)
                    continue;

                foreach (var b in keys)
                {
                    if (string.Compare(a, b, StringComparison.Ordinal) >= 0)
                        continue; // считаем только пары A < B

                    if (!seriesBySymbol.TryGetValue(b, out var sb) || sb == null || sb.Count < 2)
                        continue;

                    var corr = ComputeCorrelation(sa, sb, window);
                    if (corr is null)
                        continue;

                    result.Add(new PairCorrelation
                    {
                        SymbolA = a,
                        SymbolB = b,
                        Correlation = (decimal)corr.Value,
                        Window = window <= 0 ? Math.Min(sa.Count, sb.Count) : window,
                        CalculatedAt = DateTime.UtcNow
                    });
                }
            }

            _lastSnapshot = result;
            return result;
        }

        /// <summary>
        /// Получить корреляцию для конкретной пары символов из последнего снимка.
        /// </summary>
        public decimal? GetCorrelation(string symbolA, string symbolB)
        {
            if (_lastSnapshot == null || _lastSnapshot.Count == 0)
                return null;

            // нормализуем порядок (A < B)
            var a = symbolA;
            var b = symbolB;
            if (string.Compare(a, b, StringComparison.Ordinal) > 0)
                (a, b) = (b, a);

            var pair = _lastSnapshot.FirstOrDefault(p =>
                string.Equals(p.SymbolA, a, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.SymbolB, b, StringComparison.OrdinalIgnoreCase));

            return pair?.Correlation;
        }

        /// <summary>
        /// Проверить, сильно ли связаны два инструмента по модулю корреляции.
        /// </summary>
        public bool AreStronglyCoupled(string symbolA, string symbolB, decimal threshold = 0.8m)
        {
            var c = GetCorrelation(symbolA, symbolB);
            if (c is null) return false;
            return Math.Abs(c.Value) >= threshold;
        }

        /// <summary>
        /// Ядро: расчёт корреляции Пирсона между двумя рядами.
        /// Возвращает null, если данных мало или всё константа.
        /// </summary>
        private double? ComputeCorrelation(IReadOnlyList<decimal> a, IReadOnlyList<decimal> b, int window)
        {
            if (a == null || b == null || a.Count < 2 || b.Count < 2)
                return null;

            int n = Math.Min(a.Count, b.Count);
            if (window > 0 && window < n)
                n = window;

            if (n < 2) return null;

            // Берём последние n точек
            int offsetA = a.Count - n;
            int offsetB = b.Count - n;

            double sumX = 0, sumY = 0, sumX2 = 0, sumY2 = 0, sumXY = 0;

            for (int i = 0; i < n; i++)
            {
                double x = (double)a[offsetA + i];
                double y = (double)b[offsetB + i];

                sumX += x;
                sumY += y;
                sumX2 += x * x;
                sumY2 += y * y;
                sumXY += x * y;
            }

            double meanX = sumX / n;
            double meanY = sumY / n;

            double cov = sumXY - n * meanX * meanY;
            double varX = sumX2 - n * meanX * meanX;
            double varY = sumY2 - n * meanY * meanY;

            if (varX <= 0 || varY <= 0)
                return null; // один из рядов константный

            double corr = cov / Math.Sqrt(varX * varY);

            // численно могут вылезти 1.0000001 → обрежем
            if (corr > 1.0) corr = 1.0;
            if (corr < -1.0) corr = -1.0;

            return corr;
        }
    }
}
