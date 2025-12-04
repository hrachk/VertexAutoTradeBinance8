using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// AiLeverageService v6 (QUANT-REALTIME MAX)
    /// Управляет виртуальным плечом (risk multiplier), который учитывает:
    /// - Волатильность (ATR%)
    /// - Режим рынка (SmartRegime)
    /// - AI Trend Predictor
    /// - Исторический winrate режима (из AiSelfLearningService)
    /// - Ликвидность / squeeze риск
    /// 
    /// Возвращает multiplier (0.3m–2.0m), который RiskManager затем
    /// использует для расчёта реального размера позиции.
    /// 
    /// ПЛЕЧО НЕ УСТАНАВЛИВАЕТСЯ В БИНАНС, а влияет на DECISION-SIZE.
    /// </summary>
    public class AiLeverageService
    {
        private readonly ILogger<AiLeverageService> _logger;
        private readonly AiSelfLearningService _aiLearning;
        private readonly SmartRegimeService _smartRegime;

        public AiLeverageService(
            ILogger<AiLeverageService> logger,
            AiSelfLearningService aiLearning,
            SmartRegimeService smartRegime)
        {
            _logger = logger;
            _aiLearning = aiLearning;
            _smartRegime = smartRegime;
        }

        // =====================================================================
        // MAIN METHOD: вычисляет множитель риска (0.3 – 2.0)
        // =====================================================================
        public decimal Calculate(
            string symbol,
            KlineInterval interval,
            IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines == null || klines.Count < 30)
                return 1.0m;

            // 1) Режим рынка
            SmartRegimeInfo regime;
            try
            {
                regime = _smartRegime.Evaluate(symbol, interval, klines);
            }
            catch
            {
                return 1.0m;
            }

            // 2) ATR%
            decimal atr = CalculateAtrPct(klines);
            decimal multVol = atr switch
            {
                < 0.004m => 1.40m,   // очень низкая вола — плечо можно поднять
                < 0.008m => 1.20m,
                < 0.015m => 1.00m,
                < 0.030m => 0.75m,
                _ => 0.55m    // экстремальная волатильность — режем до минимума
            };

            // 3) SmartRegime influence
            decimal multRegime = regime.BaseRegime switch
            {
                MarketRegime.StrongUpTrend => 1.20m,
                MarketRegime.StrongDownTrend => 1.20m,

                MarketRegime.UpTrend => 1.05m,
                MarketRegime.DownTrend => 1.05m,

                MarketRegime.Range => 0.85m,

                MarketRegime.Unknown => 0.80m,
                MarketRegime.Squeeze => 0.60m,
                _ => 1.00m
            };

            // 4) AI Trend Predictor
            var pred = _aiLearning.PredictTrend(
                symbol,
                regime.BaseRegime,
                regime.TrendSlopePercent,
                regime.VolatilityPercent);

            decimal multAiTrend;

            if (pred.Direction == 0)
            {
                multAiTrend = 1.0m;
            }
            else
            {
                // чем выше confidence — тем выше плечо по тренду
                multAiTrend = 1.0m + (pred.Confidence * 0.6m);

                // если мы против тренда → порежем
                if (pred.Direction > 0 && regime.TrendSlopePercent < 0)
                    multAiTrend *= 0.7m;

                if (pred.Direction < 0 && regime.TrendSlopePercent > 0)
                    multAiTrend *= 0.7m;

                // squeeze → сузить плечо
                if (regime.SmartType == SmartRegimeType.SmartSqueeze)
                    multAiTrend *= 0.75m;
            }

            // 5) Исторический winrate режима
            decimal multWinrate = GetWinrateMultiplier(symbol, regime.BaseRegime);

            // Итоговый множитель
            decimal result = multVol * multRegime * multAiTrend * multWinrate;

            // Ограничения безопасности
            if (result > 2.0m) result = 2.0m;
            if (result < 0.30m) result = 0.30m;

            _logger.LogInformation(
                "[LEV6][{symbol}] ATR%={atr:P2}, VolMult={mv:F2}, RegimeMult={mr:F2}, AiMult={ma:F2}, WinMult={mw:F2} → FINAL={fin:F2}",
                symbol, atr, multVol, multRegime, multAiTrend, multWinrate, result);

            return result;
        }

        // =====================================================================
        // ATR%
        // =====================================================================
        private decimal CalculateAtrPct(IReadOnlyList<BinanceFuturesUsdtKline> kl)
        {
            if (kl.Count < 15)
                return 0.01m;

            decimal sum = 0;
            int start = kl.Count - 14;

            for (int i = start; i < kl.Count; i++)
            {
                var c = kl[i];
                var p = kl[i - 1];

                decimal tr = Math.Max(
                    c.HighPrice - c.LowPrice,
                    Math.Max(
                        Math.Abs(c.HighPrice - p.ClosePrice),
                        Math.Abs(c.LowPrice - p.ClosePrice)));

                sum += tr;
            }

            decimal atr = sum / 14m;
            decimal price = kl.Last().ClosePrice;

            return atr / price; // ATR%
        }

        // =====================================================================
        // WINRATE MULTIPLIER
        // =====================================================================
        private decimal GetWinrateMultiplier(string symbol, MarketRegime regime)
        {
            decimal weight;
            try
            {
                weight = _aiLearning.GetAiRiskAdjustment(symbol, regime);
            }
            catch
            {
                return 1.0m;
            }

            // weight = 0.65 – 1.35 (из AiSelfLearning v6)
            if (weight >= 1.10m)
                return 1.20m; // сильный режим → смелее

            if (weight >= 1.00m)
                return 1.05m; // норм

            if (weight >= 0.85m)
                return 0.95m; // небольшой риск

            return 0.80m;     // плохой winrate → уменьшить плечо
        }
    }
}
