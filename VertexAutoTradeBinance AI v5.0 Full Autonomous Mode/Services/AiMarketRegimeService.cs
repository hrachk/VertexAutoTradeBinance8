using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class AiMarketRegimeService
    {
        private readonly ILogger<AiMarketRegimeService> _logger;
        private readonly Lazy<MarketDataService> _marketDataLazy;

        private const int TrendLookback = 40;
        private const decimal StrongTrendSlopePct = 0.004m;
        private const decimal WeakTrendSlopePct = 0.002m;
        private const decimal HighVolatilityPct = 0.012m;

        // Кеш режимов с TTL
        private readonly ConcurrentDictionary<string, (MarketRegime Regime, DateTime UpdatedUtc)> _regimes =
            new();

        // Локальный кеш Klines
        private readonly ConcurrentDictionary<string, (IReadOnlyList<BinanceFuturesUsdtKline> Kl, DateTime UpdatedUtc)> _klinesCache =
            new();
        private readonly TimeSpan _klinesCacheTtl = TimeSpan.FromSeconds(60);

        // Список символов для фонового обновления
        private IReadOnlyList<string> _symbols = Array.Empty<string>();

        public AiMarketRegimeService(ILogger<AiMarketRegimeService> logger, Lazy<MarketDataService> marketData)
        {
            _logger = logger;
            _marketDataLazy = marketData;
        }

        // ======================================================
        // SAFE KLINES LOADER
        // ======================================================
        public async Task<IReadOnlyList<BinanceFuturesUsdtKline>> LoadKlinesSafe(
            string symbol,
            KlineInterval interval,
            int limit)
        {
            if (_klinesCache.TryGetValue(symbol, out var cached) &&
                (DateTime.UtcNow - cached.UpdatedUtc) <= _klinesCacheTtl)
            {
                return cached.Kl;
            }

            IReadOnlyList<BinanceFuturesUsdtKline> klines;
            try
            {
                klines = await _marketDataLazy.Value.GetKlines(symbol, interval, limit);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[REGIME] Failed to load klines for {symbol} {interval}", symbol, interval);
                return cached.Kl ?? Array.Empty<BinanceFuturesUsdtKline>();
            }

            _klinesCache[symbol] = (klines, DateTime.UtcNow);
            return klines;
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

            decimal atr = _marketDataLazy.Value.CalculateAtr(klines, 14);
            result.VolatilityPercent = lastClose > 0 ? atr / lastClose : 0;

            // === deviation optimized ===
            decimal sum = 0, sumSq = 0;
            for (int i = start; i <= last; i++)
            {
                var c = klines[i].ClosePrice;
                sum += c;
                sumSq += c * c;
            }
            decimal mean = sum / (last - start + 1);
            decimal variance = sumSq / (last - start + 1) - mean * mean;
            decimal std = (decimal)Math.Sqrt(Math.Max(0, (double)variance));
            result.DeviationScore = std > 0 ? (lastClose - mean) / std : 0;

            // ======================================================
            // CLASSIFICATION
            // ======================================================

            var dynamicStrongSlope = result.VolatilityPercent * 0.8m;
            var dynamicWeakSlope = result.VolatilityPercent * 0.4m;


            MarketRegime newRegime;
            if (result.VolatilityPercent >= HighVolatilityPct)
            {
                newRegime = MarketRegime.VolatileChop;
            }
            else if (priceChangePct >= dynamicStrongSlope)
            {
                newRegime = MarketRegime.StrongUpTrend;
            }
            else if (priceChangePct <= -StrongTrendSlopePct)
            {
                newRegime = MarketRegime.StrongDownTrend;
            }
            else if (priceChangePct >= WeakTrendSlopePct)
            {
                newRegime = MarketRegime.UpTrend;
            }
            else if (priceChangePct <= -WeakTrendSlopePct)
            {
                newRegime = MarketRegime.DownTrend;
            }
            else
            {
                newRegime = MarketRegime.Range;
            }

            // Логируем только при изменении режима
            if (!_regimes.TryGetValue(symbol, out var old) || old.Regime != newRegime)
            {
                _logger.LogInformation("[REGIME] {symbol} {interval} -> {regime}", symbol, interval, newRegime);
            }

            _regimes[symbol] = (newRegime, DateTime.UtcNow);
            result.Regime = newRegime;



            return result;
        }

        // ======================================================
        // API
        // ======================================================
        public bool IsStrongTrend(string symbol)
        {
            return _regimes.TryGetValue(symbol, out var r) &&
                   (r.Regime == MarketRegime.StrongUpTrend || r.Regime == MarketRegime.StrongDownTrend);
        }

        public bool IsRange(string symbol)
        {
            return _regimes.TryGetValue(symbol, out var r) && r.Regime == MarketRegime.Range;
        }

        public bool IsTradable(string symbol)
        {
            return !_regimes.TryGetValue(symbol, out var r) || r.Regime != MarketRegime.VolatileChop;
        }

        // ======================================================
        // BACKGROUND UPDATE
        // ======================================================
        public void StartBackgroundUpdate(
            IReadOnlyList<string> symbols,
            KlineInterval interval,
            int klinesLimit = 100,
            int updateIntervalSeconds = 30,
            CancellationToken cancellationToken = default)
        {
            _symbols = symbols;

            _ = Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    foreach (var symbol in _symbols)
                    {
                        try
                        {
                            var klines = await LoadKlinesSafe(symbol, interval, klinesLimit);
                            DetectRegime(symbol, interval, klines);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[REGIME] Background update failed for {symbol}", symbol);
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(updateIntervalSeconds), cancellationToken);
                }
            }, cancellationToken);
        }
    }
}