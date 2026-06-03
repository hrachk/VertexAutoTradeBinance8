using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class AiMarketRegimeService
    {
        private readonly ILogger<AiMarketRegimeService> _logger;

        private const int TrendLookback = 40;

        private const decimal StrongTrendSlopePct = 0.004m;
        private const decimal WeakTrendSlopePct = 0.002m;

        private const decimal HighVolatilityPct = 0.012m;
        private const decimal FlatSlopePct = 0.0015m;

        private readonly ConcurrentDictionary<string, MarketRegime> _regimes = new();

        private readonly Binance.Net.Clients.BinanceRestClient _client;

        public AiMarketRegimeService(ILogger<AiMarketRegimeService> logger)
        {
            _logger = logger;

            // один клиент на весь сервис
            _client = new Binance.Net.Clients.BinanceRestClient();
        }

        // ======================================================
        // SAFE KLINES LOADER
        // ======================================================

        public async Task<IReadOnlyList<BinanceFuturesUsdtKline>> LoadKlinesSafe(
            string symbol,
            KlineInterval interval,
            int limit)
        {
            try
            {
                var res = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    interval,
                    limit: limit
                ).ConfigureAwait(false);

                if (!res.Success || res.Data == null)
                {
                    _logger.LogWarning(
                        "[REGIME] LoadKlinesSafe FAIL {symbol} {tf}: {err}",
                        symbol,
                        interval,
                        res.Error);

                    return Array.Empty<BinanceFuturesUsdtKline>();
                }

                return res.Data
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
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[REGIME] LoadKlinesSafe EX {symbol} {tf}",
                    symbol,
                    interval);

                return Array.Empty<BinanceFuturesUsdtKline>();
            }
        }

        // ======================================================
        // REGIME DETECTION
        // ======================================================

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

            decimal firstClose = klines[start].ClosePrice;
            decimal lastClose = klines[last].ClosePrice;

            if (firstClose <= 0 || lastClose <= 0)
                return result;

            decimal priceChangePct = (lastClose - firstClose) / firstClose;
            result.TrendSlopePercent = priceChangePct;

            decimal atr = CalculateAtr(klines, 14, last);

            if (lastClose > 0)
                result.VolatilityPercent = atr / lastClose;

            // === deviation ===

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

            result.DeviationScore =
                std > 0 ? (lastClose - mean) / std : 0;

            // ======================================================
            // CLASSIFICATION
            // ======================================================

            if (priceChangePct >= StrongTrendSlopePct)
            {
                result.Regime = MarketRegime.StrongUpTrend;
            }
            else if (priceChangePct <= -StrongTrendSlopePct)
            {
                result.Regime = MarketRegime.StrongDownTrend;
            }
            else if (priceChangePct >= WeakTrendSlopePct)
            {
                result.Regime = MarketRegime.UpTrend;
            }
            else if (priceChangePct <= -WeakTrendSlopePct)
            {
                result.Regime = MarketRegime.DownTrend;
            }
            else
            {
                if (result.VolatilityPercent >= HighVolatilityPct)
                    result.Regime = MarketRegime.VolatileChop;
                else
                    result.Regime = MarketRegime.Range;
            }

            ConsoleReportFormatter.MarketRegimeReport(
                _logger,
                symbol,
                interval.ToString(),
                result);

            _regimes[symbol] = result.Regime;

            return result;
        }

        // ======================================================
        // API
        // ======================================================

        public bool IsStrongTrend(string symbol)
        {
            if (!_regimes.TryGetValue(symbol, out var r))
                return false;

            return r == MarketRegime.StrongUpTrend ||
                   r == MarketRegime.StrongDownTrend;
        }

        public bool IsRange(string symbol)
        {
            if (!_regimes.TryGetValue(symbol, out var r))
                return false;

            return r == MarketRegime.Range;
        }

        public bool IsTradable(string symbol)
        {
            if (!_regimes.TryGetValue(symbol, out var regime))
                return true;

            return regime != MarketRegime.VolatileChop;
        }

        // ======================================================
        // ATR
        // ======================================================

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