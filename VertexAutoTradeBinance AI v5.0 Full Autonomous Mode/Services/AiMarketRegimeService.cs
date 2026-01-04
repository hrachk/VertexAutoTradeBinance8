using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// AI-детектор режима рынка: тренд / диапазон / пила.
    /// Работает по klines текущего таймфрейма.
    /// </summary>
    public class AiMarketRegimeService
    {
        private readonly ILogger<AiMarketRegimeService> _logger;

        // Сколько последних свечей анализируем для тренда.
        private const int TrendLookback = 40;

        // Минимальный наклон тренда, чтобы считать его "сильным".
        private const decimal StrongTrendSlopePct = 0.004m; // 0.4%

        // Минимальная волатильность (ATR/price), чтобы считать рынок "шумным".
        private const decimal HighVolatilityPct = 0.012m; // 1.2%

        // Порог "почти без тренда" — для флета/пилы.
        private const decimal FlatSlopePct = 0.0015m; // 0.15%
        private readonly ConcurrentDictionary<string, MarketRegime> _regimes = new();
        public AiMarketRegimeService(ILogger<AiMarketRegimeService> logger)
        {
            _logger = logger;
        }

        // =====================================================================
        // SAFE KLINES LOADER (ДЛЯ HighTimeframeSafetyFilter)
        // =====================================================================
        public async Task<IReadOnlyList<BinanceFuturesUsdtKline>> LoadKlinesSafe(
            string symbol,
            KlineInterval interval,
            int limit)
        {
            try
            {
                // Твой MarketDataService уже делает все правильно,
                // но мы не можем использовать его здесь — поэтому
                // тянем свечи через BinanceClientFactory (упрощённый вариант).
                using var client = new Binance.Net.Clients.BinanceRestClient();

                var res = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    interval,
                    limit: limit);

                if (!res.Success || res.Data == null)
                {

                    _logger.LogWarning(
                        "[REGIME] LoadKlinesSafe FAIL {symbol} {tf}: {err}",
                        symbol,
                        interval,
                        res.Error);
                    return Array.Empty<BinanceFuturesUsdtKline>();
                }
                   

                // === КОНВЕРТАЦИЯ В BinanceFuturesUsdtKline ===
                var list = res.Data
                    .Select(k => new BinanceFuturesUsdtKline
                    {
                        OpenTime = k.OpenTime,
                        OpenPrice = k.OpenPrice,
                        HighPrice = k.HighPrice,
                        LowPrice = k.LowPrice,
                        ClosePrice = k.ClosePrice,
                        Volume = k.Volume,
                        CloseTime = k.CloseTime,
                        QuoteVolume = k.QuoteVolume,
                        TakerBuyBaseVolume = k.TakerBuyBaseVolume,
                        TakerBuyQuoteVolume = k.TakerBuyQuoteVolume,
                         TradeCount = k.TradeCount
                    })
                    .ToList();

                return list;

            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[REGIME] LoadKlinesSafe EX {symbol} {tf}",
                    symbol,
                    interval);



                return Array.Empty<BinanceFuturesUsdtKline>();
            }
        }

        public MarketRegimeResult DetectRegime(
            string symbol,
            KlineInterval interval,
            IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            var result = new MarketRegimeResult
            {
                Symbol = symbol,
                Timeframe = interval.ToString(),
                Regime = MarketRegime.Unknown
            };

            if (klines.Count < TrendLookback + 5)
                return result;

            int last = klines.Count - 1;
            int start = last - TrendLookback;
            if (start < 0) start = 0;

            // --- базовые метрики ---
            decimal firstClose = klines[start].ClosePrice;
            decimal lastClose = klines[last].ClosePrice;

            if (firstClose <= 0 || lastClose <= 0)
                return result;

            decimal priceChangePct = (lastClose - firstClose) / firstClose;
            result.TrendSlopePercent = priceChangePct;

            // ATR как волатильность
            decimal atr = CalculateAtr(klines, 14, last);
            result.VolatilityPercent = atr / lastClose;

            // Простая оценка отклонения от средней
            decimal mean = 0;
            for (int i = start; i <= last; i++)
                mean += klines[i].ClosePrice;
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

            // --- классификация режима ---

            if (priceChangePct >= StrongTrendSlopePct && result.VolatilityPercent >= FlatSlopePct)
            {
                result.Regime = MarketRegime.StrongUpTrend;
            }
            else if (priceChangePct <= -StrongTrendSlopePct && result.VolatilityPercent >= FlatSlopePct)
            {
                result.Regime = MarketRegime.StrongDownTrend;
            }
            else
            {
                if (Math.Abs(priceChangePct) <= FlatSlopePct)
                {
                    if (result.VolatilityPercent >= HighVolatilityPct)
                        result.Regime = MarketRegime.VolatileChop;
                    else
                        result.Regime = MarketRegime.Range;
                }
                else
                {
                    if (result.VolatilityPercent >= HighVolatilityPct)
                        result.Regime = MarketRegime.VolatileChop;
                    else
                        result.Regime = MarketRegime.Range;
                }
            }

            // 🔥 Красивый отчёт вместо сырого ToString()
            ConsoleReportFormatter.MarketRegimeReport(
                _logger,
                symbol,
                interval.ToString(),
                result);

            return result;
        }

        public bool IsTradable(string symbol)
        {
            // если пока нет данных по режиму — НЕ БЛОКИРУЕМ
            if (!_regimes.TryGetValue(symbol, out var regime))
                return true;

            return regime switch
            {
                MarketRegime.VolatileChop => false, // ❌ пила / хаос
                _ => true
            };
        }

        // Локальный ATR, чтобы не тянуть MarketDataService
        private static decimal CalculateAtr(
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            int period,
            int lastIndex)
        {
            if (klines.Count < period + 1)
                return 0;

            int start = lastIndex - period + 1;
            if (start < 1) start = 1;

            decimal sum = 0;
            int count = 0;

            for (int i = start; i <= lastIndex; i++)
            {
                var cur = klines[i];
                var prev = klines[i - 1];

                decimal tr1 = cur.HighPrice - cur.LowPrice;
                decimal tr2 = Math.Abs(cur.HighPrice - prev.ClosePrice);
                decimal tr3 = Math.Abs(cur.LowPrice - prev.ClosePrice);

                decimal tr = Math.Max(tr1, Math.Max(tr2, tr3));
                sum += tr;
                count++;
            }

            return count > 0 ? sum / count : 0;
        }
    }
}
