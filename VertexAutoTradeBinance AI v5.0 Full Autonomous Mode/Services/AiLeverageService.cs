using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
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

        public decimal Calculate(
            string symbol,
            KlineInterval interval,
            IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines == null || klines.Count < 30)
                return 1.0m;

            SmartRegimeInfo regime;
            try
            {
                regime = _smartRegime.Evaluate(symbol, interval, klines);
            }
            catch
            {
                return 1.0m;
            }

            decimal atr = CalculateAtrPct(klines);
            decimal multVol = atr switch
            {
                < 0.004m => 1.40m,
                < 0.008m => 1.20m,
                < 0.015m => 1.00m,
                < 0.030m => 0.75m,
                _ => 0.55m
            };

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

            var pred = _aiLearning.PredictTrend(
                symbol,
                regime.BaseRegime,
                regime.TrendSlopePercent,
                regime.VolatilityPercent);

            decimal multAiTrend;

            if (pred.Direction == 0)
                multAiTrend = 1.0m;
            else
            {
                multAiTrend = 1.0m + (pred.Confidence * 0.6m);
                if (pred.Direction > 0 && regime.TrendSlopePercent < 0)
                    multAiTrend *= 0.7m;

                if (pred.Direction < 0 && regime.TrendSlopePercent > 0)
                    multAiTrend *= 0.7m;

                if (regime.SmartType == SmartRegimeType.SmartSqueeze)
                    multAiTrend *= 0.75m;
            }

            decimal multWinrate = GetWinrateMultiplier(symbol, regime.BaseRegime);
            decimal result = multVol * multRegime * multAiTrend * multWinrate;

            if (result > 2.0m) result = 2.0m;
            if (result < 0.30m) result = 0.30m;

            _logger.LogInformation(
                "[LEV6][{symbol}] ATR%={atr:P2}, VolMult={mv:F2}, RegimeMult={mr:F2}, AiMult={ma:F2}, WinMult={mw:F2} → FINAL={fin:F2}",
                symbol, atr, multVol, multRegime, multAiTrend, multWinrate, result);

            return result;
        }

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

            return atr / price;
        }

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

            if (weight >= 1.10m)
                return 1.20m;

            if (weight >= 1.00m)
                return 1.05m;

            if (weight >= 0.85m)
                return 0.95m;

            return 0.80m;
        }
    }
}
