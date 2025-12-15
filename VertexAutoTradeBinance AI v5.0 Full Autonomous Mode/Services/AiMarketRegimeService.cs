using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{ /// <summary>
  /// AI-детектор режима рынка: тренд / диапазон / пила.
  /// Работает по klines текущего таймфрейма.
  /// </summary>
    public class AiMarketRegimeService
    {
        private readonly ILogger<AiMarketRegimeService> _logger;
        private readonly IMarketDataFacade _market;
        // Сколько последних свечей анализируем для тренда.
        private const int TrendLookback = 40;
        // Минимальный наклон тренда, чтобы считать его "сильным".
        private const decimal StrongTrendSlopePct = 0.004m; // 0.4%

        // Минимальная волатильность (ATR/price), чтобы считать рынок "шумным".
        private const decimal HighVolatilityPct = 0.012m; // 1.2%

        // Порог "почти без тренда" — для флета/пилы.
        private const decimal FlatSlopePct = 0.0015m; // 0.15%

        public AiMarketRegimeService(
            ILogger<AiMarketRegimeService> logger,
            IMarketDataFacade market)
        {
            _logger = logger;
            _market = market;
        }

        // SAFE KLINES LOADER (ДЛЯ HighTimeframeSafetyFilter)
        public async Task<IReadOnlyList<BinanceFuturesUsdtKline>> LoadKlinesSafe(
            string symbol,
            KlineInterval interval,
            int limit,
            CancellationToken ct = default)
        {
            try
            {
                // ВАЖНО: больше никакого new BinanceRestClient() тут нет
                return await _market.GetKlinesAsync(symbol, interval, limit, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[REGIME] LoadKlinesSafe EX {symbol} {tf}", symbol, interval);
                return Array.Empty<BinanceFuturesUsdtKline>();
            }
        }

        public MarketRegimeResult DetectRegime(
            string symbol,
            KlineInterval interval,
            IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            // твоя логика детекта — оставляем 1:1
            // ...
            // (ниже без изменений)
            var result = new MarketRegimeResult
            {
                Symbol = symbol,
                Timeframe = interval.ToString(),
                Regime = MarketRegime.Unknown
            };

            if (klines.Count < TrendLookback + 5)
                return result;

            int last = klines.Count - 1;
            int start = Math.Max(0, last - TrendLookback);

            decimal firstClose = klines[start].ClosePrice;
            decimal lastClose = klines[last].ClosePrice;
            if (firstClose <= 0 || lastClose <= 0)
                return result;

            decimal priceChangePct = (lastClose - firstClose) / firstClose;
            result.TrendSlopePercent = priceChangePct;

            decimal atr = CalculateAtr(klines, 14, last);
            result.VolatilityPercent = atr / lastClose;

            decimal mean = 0;
            for (int i = start; i <= last; i++) mean += klines[i].ClosePrice;
            mean /= (last - start + 1);

            decimal variance = 0;
            for (int i = start; i <= last; i++)
            {
                decimal d = klines[i].ClosePrice - mean;
                variance += d * d;
            }
            variance /= Math.Max(1, last - start);
            decimal std = (decimal)Math.Sqrt((double)variance);

            result.DeviationScore = std > 0 ? (lastClose - mean) / std : 0;

            if (priceChangePct >= StrongTrendSlopePct && result.VolatilityPercent >= FlatSlopePct)
                result.Regime = MarketRegime.StrongUpTrend;
            else if (priceChangePct <= -StrongTrendSlopePct && result.VolatilityPercent >= FlatSlopePct)
                result.Regime = MarketRegime.StrongDownTrend;
            else
            {
                if (Math.Abs(priceChangePct) <= FlatSlopePct)
                    result.Regime = (result.VolatilityPercent >= HighVolatilityPct) ? MarketRegime.VolatileChop : MarketRegime.Range;
                else
                    result.Regime = (result.VolatilityPercent >= HighVolatilityPct) ? MarketRegime.VolatileChop : MarketRegime.Range;
            }

            ConsoleReportFormatter.MarketRegimeReport(_logger, symbol, interval.ToString(), result);
            return result;
        }

        private static decimal CalculateAtr(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period, int lastIndex)
        {
            if (klines.Count < period + 1) return 0;
            int start = Math.Max(1, lastIndex - period + 1);

            decimal sum = 0;
            int count = 0;

            for (int i = start; i <= lastIndex; i++)
            {
                var cur = klines[i];
                var prev = klines[i - 1];

                decimal tr1 = cur.HighPrice - cur.LowPrice;
                decimal tr2 = Math.Abs(cur.HighPrice - prev.ClosePrice);
                decimal tr3 = Math.Abs(cur.LowPrice - prev.ClosePrice);

                sum += Math.Max(tr1, Math.Max(tr2, tr3));
                count++;
            }

            return count > 0 ? sum / count : 0;
        }
    }
}
