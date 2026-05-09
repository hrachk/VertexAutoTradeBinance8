using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// SmartRegimeService — надстройка над AiMarketRegimeService + корреляция с BTC.
    /// Сохраняет все существующие логические связи и метрики.
    /// </summary>
    public class SmartRegimeService
    {
        public MarketRegime LastBaseRegime { get; private set; } = MarketRegime.Range;
        public SmartRegimeType LastSmartRegime { get; private set; } = SmartRegimeType.Unknown;

        public decimal LastSlope { get; private set; }
        public decimal LastVolatility { get; private set; }
        public decimal LastConfidence { get; private set; }

        public decimal LastCorrelationToBtc { get; private set; }
        public bool LastDangerChop { get; private set; }
        public bool LastAllowAggressive { get; private set; }
        public bool LastAllowCounter { get; private set; }

        private readonly ILogger<SmartRegimeService> _logger;
        private readonly AiMarketRegimeService _marketRegimeService;
        private readonly AiCorrelationService _correlationService;

        public bool IsVolCompression { get; init; } = false;
        public bool IsControlledTrend { get; init; } = false;

        public string EntryProfile { get; init; } = "STD";
        public decimal RiskBias { get; init; } = 1.0m;

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

            // --- 1) Базовый режим
            var regimeInfo = _marketRegimeService.DetectRegime(symbol, interval, klines);
            var baseRegime = regimeInfo.Regime;
            var slope = regimeInfo.TrendSlopePercent;
            var vol = regimeInfo.VolatilityPercent;

            // --- 2) Корреляция к BTC
            decimal corrToBtc = 0m;
            if (!string.Equals(symbol, "BTCUSDT", StringComparison.OrdinalIgnoreCase))
            {
                var corr = _correlationService.GetCorrelation("BTCUSDT", symbol);
                corrToBtc = Math.Clamp(corr ?? 0m, -1m, 1m);
            }

            // --- 3) Вычисляем режимы
            var absSlope = Math.Abs(slope);
            var absVol = Math.Abs(vol);

            bool isRange = baseRegime == MarketRegime.Range;
            bool isStrongTrend = baseRegime == MarketRegime.StrongUpTrend || baseRegime == MarketRegime.StrongDownTrend;

            bool isVolCompression = (isRange && absVol < 0.004m) || (absVol < 0.006m && absSlope < 0.0025m);
            bool isControlledTrend = (!isRange && absSlope >= 0.006m && absSlope <= 0.020m && absVol <= 0.008m)
                                     || (isStrongTrend && absSlope >= 0.008m && absVol <= 0.010m);

            VolatilityRegime volRegime = VolatilityRegime.Normal;
            if (isVolCompression) volRegime = VolatilityRegime.Low;
            else if (absVol > 0.015m) volRegime = VolatilityRegime.High;

            // --- 4) Определяем SmartType с правильным приоритетом
            SmartRegimeType smartType;
            if (isStrongTrend && absSlope > 0.01m && absVol > 0.010m)
                smartType = SmartRegimeType.SmartStrongTrend;
            else if (isControlledTrend)
                smartType = SmartRegimeType.SmartTrend;
            else if (isVolCompression)
                smartType = SmartRegimeType.SmartSqueeze;
            else if (isRange)
                smartType = SmartRegimeType.SmartRange;
            else if (!isRange && absVol > 0.02m && absSlope < 0.003m)
                smartType = SmartRegimeType.SmartChop;
            else
                smartType = SmartRegimeType.SmartTrend;

            // --- 5) Вычисление confidence
            decimal confidence = 0.3m;
            confidence += Math.Min(absSlope * 5m, 0.4m);
            confidence += Math.Min((0.02m - Math.Min(absVol, 0.02m)) * 5m, 0.2m);

            if (smartType == SmartRegimeType.SmartChop)
                confidence -= 0.3m;

            if (smartType == SmartRegimeType.SmartTrend || smartType == SmartRegimeType.SmartStrongTrend)
                confidence += Math.Min(Math.Abs(corrToBtc) * 0.3m, 0.3m);

            confidence = Math.Clamp(confidence, 0m, 1m);

            // --- 6) Флаги поведения
            bool dangerChop = smartType == SmartRegimeType.SmartChop;
            bool allowAggressive = smartType == SmartRegimeType.SmartStrongTrend && confidence >= 0.6m;
            bool allowCounter = isRange || smartType == SmartRegimeType.SmartSqueeze;

            string entryProfile = isControlledTrend ? "CT" :
                                  (smartType == SmartRegimeType.SmartStrongTrend ? "EXP" : "STD");

            decimal riskBias = isControlledTrend ? 0.75m :
                               smartType == SmartRegimeType.SmartChop ? 0.60m :
                               smartType == SmartRegimeType.SmartStrongTrend ? 1.1m : 1.0m;

            // --- 7) Собираем SmartRegimeInfo
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
                AllowCounterTrendEntries = allowCounter,
                IsVolCompression = isVolCompression,
                IsControlledTrend = isControlledTrend,
                EntryProfile = entryProfile,
                RiskBias = riskBias,
                VolRegime = volRegime
            };

            _logger.LogInformation(
                "[SMART][{Symbol}][{TF}] base={Base} smart={Smart} slope={Slope:P2} vol={Vol:P2} corrBTC={Corr:F2} conf={Conf:P2} CT={CT} VC={VC} profile={Profile} riskBias={RiskBias:F2}",
                symbol, interval, info.BaseRegime, info.SmartType, info.TrendSlopePercent, info.VolatilityPercent,
                info.CorrelationToBtc, info.Confidence, info.IsControlledTrend, info.IsVolCompression,
                info.EntryProfile, info.RiskBias
            );

            // --- 8) UI / Last values
            LastBaseRegime = info.BaseRegime;
            LastSmartRegime = info.SmartType;
            LastSlope = info.TrendSlopePercent;
            LastVolatility = info.VolatilityPercent;
            LastConfidence = info.Confidence;
            LastCorrelationToBtc = info.CorrelationToBtc;
            LastDangerChop = info.IsDangerChopZone;
            LastAllowAggressive = info.AllowAggressiveTrendEntries;
            LastAllowCounter = info.AllowCounterTrendEntries;

            return info;
        }
    }
}