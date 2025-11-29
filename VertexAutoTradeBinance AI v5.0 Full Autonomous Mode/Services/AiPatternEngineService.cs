using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// AI-движок свечных паттернов:
    ///  • импульсные свечи
    ///  • пин-бары (хвосты, сбор ликвидности)
    ///  • V-развороты
    ///  • простая оценка силы/направления паттерна
    /// </summary>
    public class AiPatternEngineService
    {
        private readonly ILogger<AiPatternEngineService> _logger;

        public AiPatternEngineService(ILogger<AiPatternEngineService> logger)
        {
            _logger = logger;
        }

        public class PatternResult
        {
            public string Name { get; set; } = "None";
            /// <summary>0..1 — уверенность паттерна.</summary>
            public decimal Score { get; set; }
            /// <summary>+1 = бычий, -1 = медвежий, 0 = нейтральный.</summary>
            public int Direction { get; set; }
        }

        /// <summary>
        /// Основной метод анализа последних свечей.
        /// Возвращает лучший найденный паттерн (или null, если явного сигнала нет).
        /// </summary>
        public PatternResult? Analyze(
            string symbol,
            KlineInterval interval,
            IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines == null || klines.Count < 15)
                return null;

            int last = klines.Count - 1;
            var lastK = klines[last];
            var prevK = klines[last - 1];
            var prev2K = klines[last - 2];

            decimal avgBody = AverageBody(klines, 10);
            decimal avgRange = AverageRange(klines, 10);
            if (avgBody <= 0 || avgRange <= 0)
                return null;

            var candidates = new List<PatternResult>();

            DetectImpulse(lastK, avgBody, candidates);
            DetectPinBar(lastK, avgBody, avgRange, candidates);
            DetectVReversal(prev2K, prevK, lastK, avgBody, candidates);

            if (candidates.Count == 0)
                return null;

            var best = candidates.OrderByDescending(c => c.Score).First();

            _logger.LogInformation(
                "PatternEngine {Symbol} TF={TF}: {Name}, score={Score:F2}, dir={Dir}",
                symbol, interval, best.Name, best.Score, best.Direction);

            return best;
        }

        // -------------------- helpers --------------------

        private static decimal CandleBody(BinanceFuturesUsdtKline k)
            => Math.Abs(k.ClosePrice - k.OpenPrice);

        private static decimal CandleRange(BinanceFuturesUsdtKline k)
            => k.HighPrice - k.LowPrice;

        private static void CandleShadows(
            BinanceFuturesUsdtKline k,
            out decimal upper,
            out decimal lower)
        {
            var bodyHigh = Math.Max(k.OpenPrice, k.ClosePrice);
            var bodyLow = Math.Min(k.OpenPrice, k.ClosePrice);

            upper = k.HighPrice - bodyHigh;
            lower = bodyLow - k.LowPrice;
        }

        private static decimal AverageBody(
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            int count)
        {
            int last = klines.Count - 1;
            int start = Math.Max(0, last - count + 1);

            decimal sum = 0;
            int c = 0;

            for (int i = start; i <= last; i++)
            {
                sum += CandleBody(klines[i]);
                c++;
            }

            return c > 0 ? sum / c : 0;
        }

        private static decimal AverageRange(
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            int count)
        {
            int last = klines.Count - 1;
            int start = Math.Max(0, last - count + 1);

            decimal sum = 0;
            int c = 0;

            for (int i = start; i <= last; i++)
            {
                sum += CandleRange(klines[i]);
                c++;
            }

            return c > 0 ? sum / c : 0;
        }

        private static void DetectImpulse(
            BinanceFuturesUsdtKline last,
            decimal avgBody,
            List<PatternResult> outList)
        {
            var body = CandleBody(last);
            var range = CandleRange(last);
            if (range <= 0 || avgBody <= 0)
                return;

            // бычий импульс
            if (last.ClosePrice > last.OpenPrice && body >= avgBody * 1.8m)
            {
                // close возле high
                decimal upper, lower;
                CandleShadows(last, out upper, out lower);

                var nearHigh = upper <= range * 0.25m;
                var score = Math.Clamp((body / avgBody - 1m) / 2m, 0m, 1m);
                if (nearHigh) score += 0.15m;
                if (score > 1m) score = 1m;

                outList.Add(new PatternResult
                {
                    Name = "BullImpulse",
                    Direction = +1,
                    Score = score
                });
            }

            // медвежий импульс
            if (last.ClosePrice < last.OpenPrice && body >= avgBody * 1.8m)
            {
                decimal upper, lower;
                CandleShadows(last, out upper, out lower);

                var nearLow = lower <= range * 0.25m;
                var score = Math.Clamp((body / avgBody - 1m) / 2m, 0m, 1m);
                if (nearLow) score += 0.15m;
                if (score > 1m) score = 1m;

                outList.Add(new PatternResult
                {
                    Name = "BearImpulse",
                    Direction = -1,
                    Score = score
                });
            }
        }

        private static void DetectPinBar(
            BinanceFuturesUsdtKline last,
            decimal avgBody,
            decimal avgRange,
            List<PatternResult> outList)
        {
            var body = CandleBody(last);
            var range = CandleRange(last);
            if (range <= 0 || avgRange <= 0)
                return;

            CandleShadows(last, out var upper, out var lower);

            // бычий пин-бар (длинный нижний хвост, сбор ликвидности)
            if (lower >= body * 2m && lower >= upper * 2m && range >= avgRange * 0.8m)
            {
                var score = 0.4m;
                score += Math.Clamp(lower / range, 0m, 0.4m);
                if (last.ClosePrice > last.OpenPrice)
                    score += 0.1m;

                outList.Add(new PatternResult
                {
                    Name = "BullPinBar",
                    Direction = +1,
                    Score = Math.Min(1m, score)
                });
            }

            // медвежий пин-бар (длинный верхний хвост)
            if (upper >= body * 2m && upper >= lower * 2m && range >= avgRange * 0.8m)
            {
                var score = 0.4m;
                score += Math.Clamp(upper / range, 0m, 0.4m);
                if (last.ClosePrice < last.OpenPrice)
                    score += 0.1m;

                outList.Add(new PatternResult
                {
                    Name = "BearPinBar",
                    Direction = -1,
                    Score = Math.Min(1m, score)
                });
            }
        }

        private static void DetectVReversal(
            BinanceFuturesUsdtKline prev2,
            BinanceFuturesUsdtKline prev1,
            BinanceFuturesUsdtKline last,
            decimal avgBody,
            List<PatternResult> outList)
        {
            var body2 = CandleBody(prev2);
            var body1 = CandleBody(prev1);
            var body0 = CandleBody(last);

            // V-разворот вверх: сильная медвежья, маленькая, сильная бычья
            if (prev2.ClosePrice < prev2.OpenPrice &&
                last.ClosePrice > last.OpenPrice &&
                body2 >= avgBody * 1.5m &&
                body0 >= avgBody * 1.5m &&
                body1 <= avgBody * 0.7m &&
                last.ClosePrice > prev2.OpenPrice)
            {
                var score = 0.6m;
                score += Math.Clamp((body0 + body2) / (2m * avgBody) - 1m, 0m, 0.3m);

                outList.Add(new PatternResult
                {
                    Name = "VReversalUp",
                    Direction = +1,
                    Score = Math.Min(1m, score)
                });
            }

            // V-разворот вниз: сильная бычья, маленькая, сильная медвежья
            if (prev2.ClosePrice > prev2.OpenPrice &&
                last.ClosePrice < last.OpenPrice &&
                body2 >= avgBody * 1.5m &&
                body0 >= avgBody * 1.5m &&
                body1 <= avgBody * 0.7m &&
                last.ClosePrice < prev2.OpenPrice)
            {
                var score = 0.6m;
                score += Math.Clamp((body0 + body2) / (2m * avgBody) - 1m, 0m, 0.3m);

                outList.Add(new PatternResult
                {
                    Name = "VReversalDown",
                    Direction = -1,
                    Score = Math.Min(1m, score)
                });
            }
        }
    }
}
