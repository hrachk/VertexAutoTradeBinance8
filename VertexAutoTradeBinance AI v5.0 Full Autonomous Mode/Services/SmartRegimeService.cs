using System;
using System.Collections.Generic;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// SmartRegimeService — надстройка над AiMarketRegimeService + корреляция с BTC.
    /// Не ломает существующую логику, даёт более умную картину рынка.
    /// </summary>
    public class SmartRegimeService
    {
        private readonly ILogger<SmartRegimeService> _logger;
        private readonly AiMarketRegimeService _marketRegimeService;
        private readonly AiCorrelationService _correlationService;

        public SmartRegimeService(
            ILogger<SmartRegimeService> logger,
            AiMarketRegimeService marketRegimeService,
            AiCorrelationService correlationService)
        {
            _logger = logger;
            _marketRegimeService = marketRegimeService;
            _correlationService = correlationService;
        }

        public SmartRegimeInfo Evaluate(
            string symbol,
            KlineInterval interval,
            IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines == null || klines.Count == 0)
            {
                return new SmartRegimeInfo
                {
                    Symbol = symbol,
                    Interval = interval,
                    BaseRegime = MarketRegime.Range,
                    SmartType = SmartRegimeType.Unknown,
                    Confidence = 0m
                };
            }

            // 1) Базовый режим от AiMarketRegimeService
            var regimeInfo = _marketRegimeService.DetectRegime(symbol, interval, klines);
            var baseRegime = regimeInfo.Regime;
            var slope = regimeInfo.TrendSlopePercent;      // ~0.10 => 10%
            var vol = regimeInfo.VolatilityPercent;        // ~0.02 => 2%

            // 2) Корреляция к BTCUSDT (если не BTC)
            decimal corrToBtc = 0m;
            if (!string.Equals(symbol, "BTCUSDT", StringComparison.OrdinalIgnoreCase))
            {
                var corr = _correlationService.GetCorrelation("BTCUSDT", symbol);
                if (corr is decimal dc)
                    corrToBtc = dc;
            }

            // 3) Определяем SmartType
            var absSlope = Math.Abs(slope);
            var absVol = Math.Abs(vol);

            SmartRegimeType smartType;

            bool isRange = baseRegime == MarketRegime.Range;
            bool isStrongTrend = baseRegime == MarketRegime.StrongUpTrend ||
                                 baseRegime == MarketRegime.StrongDownTrend;

            if (isRange && absVol < 0.004m)          // < 0.4% волы → узкий флэт/сжатие
            {
                smartType = SmartRegimeType.SmartSqueeze;
            }
            else if (isRange)
            {
                smartType = SmartRegimeType.SmartRange;
            }
            else if (isStrongTrend && absSlope > 0.01m) // >1% наклон → сильный тренд
            {
                smartType = SmartRegimeType.SmartStrongTrend;
            }
            else if (!isRange && absVol > 0.02m && absSlope < 0.003m)
            {
                // вола высокая, наклон почти 0 → рубка/хаос
                smartType = SmartRegimeType.SmartChop;
            }
            else
            {
                smartType = SmartRegimeType.SmartTrend;
            }

            // 4) Уверенность
            // базово от наклона + штраф за хаос
            decimal confidence = 0.3m;

            confidence += Math.Min(absSlope * 5m, 0.4m); // до +0.4 за тренд
            confidence += Math.Min((0.02m - Math.Min(absVol, 0.02m)) * 5m, 0.2m); // +0.2 за умеренную волу

            if (smartType == SmartRegimeType.SmartChop)
                confidence -= 0.3m;

            // корреляция с BTC усиливает уверенность в трендовом режиме
            if (smartType == SmartRegimeType.SmartTrend || smartType == SmartRegimeType.SmartStrongTrend)
            {
                confidence += Math.Min(Math.Abs(corrToBtc) * 0.3m, 0.3m);
            }

            // ограничение 0..1
            if (confidence < 0m) confidence = 0m;
            if (confidence > 1m) confidence = 1m;

            // 5) Флаги поведения
            bool dangerChop = smartType == SmartRegimeType.SmartChop;
            bool allowAggressive = smartType == SmartRegimeType.SmartStrongTrend && confidence >= 0.6m;
            bool allowCounter = isRange || smartType == SmartRegimeType.SmartSqueeze;

            var info = new SmartRegimeInfo
            {
                Symbol = symbol,
                Interval = interval,
                BaseRegime = baseRegime,
                SmartType = smartType,
                TrendSlopePercent = slope,
                VolatilityPercent = vol,
                CorrelationToBtc = corrToBtc,
                Confidence = confidence,
                IsDangerChopZone = dangerChop,
                AllowAggressiveTrendEntries = allowAggressive,
                AllowCounterTrendEntries = allowCounter
            };

            _logger.LogInformation(
                "[SMART][{Symbol}][{TF}] base={Base} smart={Smart} slope={Slope:P2} vol={Vol:P2} corrBTC={Corr:F2} conf={Conf:P0}",
                symbol,
                interval,
                info.BaseRegime,
                info.SmartType,
                info.TrendSlopePercent,
                info.VolatilityPercent,
                info.CorrelationToBtc,
                info.Confidence);

            return info;
        }
    }
}
