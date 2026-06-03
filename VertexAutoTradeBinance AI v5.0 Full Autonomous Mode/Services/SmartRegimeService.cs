using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// SmartRegimeService — надстройка над AiMarketRegimeService + корреляция с BTC.
    /// Оптимизирована под мажоры (BTC/ETH/BNB/SOL) и альты с разной волатильностью.
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

        public SmartRegimeService(
            ILogger<SmartRegimeService> logger,
            AiMarketRegimeService marketRegimeService,
            AiCorrelationService correlationService)
        {
            _logger = logger;
            _marketRegimeService = marketRegimeService;
            _correlationService = correlationService;
        }

        private static bool IsMajor(string symbol)
        {
            // Мажоры с более “чистой” динамикой и меньшими процентными наклонами
            return symbol is "BTCUSDT" or "ETHUSDT" or "BNBUSDT" or "SOLUSDT";
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

            // 3) Определяем SmartType с учётом мажоров
            var absSlope = Math.Abs(slope);
            var absVol = Math.Abs(vol);

            bool isRange = baseRegime == MarketRegime.Range;
            bool isStrongTrend = baseRegime == MarketRegime.StrongUpTrend ||
                                 baseRegime == MarketRegime.StrongDownTrend;
            bool isMajor = IsMajor(symbol);

            // Порог наклона для сильного тренда:
            // мажоры: ~0.4% (0.004), альты: 1% (0.01)
            decimal strongTrendSlopeThreshold = isMajor ? 0.004m : 0.01m;

            SmartRegimeType smartType;
            if (isRange && absVol < 0.004m)          // < 0.4% волы → узкий флэт/сжатие
            {
                smartType = SmartRegimeType.SmartSqueeze;
            }
            else if (isRange)
            {
                smartType = SmartRegimeType.SmartRange;
            }
            else if (isStrongTrend && absSlope > strongTrendSlopeThreshold)
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

            // 4) Уверенность (confidence) с разными весами для мажоров/альтов
            decimal confidence;

            if (isMajor)
            {
                // Для мажоров базовый уровень повыше, но меньше зависимость от огромных наклонов
                confidence = 0.4m;
                confidence += Math.Min(absSlope * 3m, 0.3m); // до +0.3 за наклон
            }
            else
            {
                confidence = 0.3m;
                confidence += Math.Min(absSlope * 5m, 0.4m); // до +0.4 за тренд
            }

            // Волатильность: для мажоров допустим чуть больший шум
            decimal volCap = isMajor ? 0.03m : 0.02m;
            confidence += Math.Min((volCap - Math.Min(absVol, volCap)) * 4m, 0.2m); // до +0.2 за умеренную волу

            if (smartType == SmartRegimeType.SmartChop)
                confidence -= 0.3m;

            // Корреляция с BTC усиливает уверенность в трендовом режиме
            // Для самого BTC corrToBtc = 0 и не влияет.
            if (smartType == SmartRegimeType.SmartTrend || smartType == SmartRegimeType.SmartStrongTrend)
            {
                var corrWeight = isMajor ? 0.15m : 0.3m;
                confidence += Math.Min(Math.Abs(corrToBtc) * corrWeight, 0.3m);
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

            // Для UI
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
