using Binance.Net.Enums;

namespace VertexAutoTradeBinance8.Models
{
    public enum SmartRegimeType
    {
        Unknown = 0,
        SmartRange = 1,
        SmartTrend = 2,
        SmartStrongTrend = 3,
        SmartSqueeze = 4,
        SmartChop = 5,
        SmartExhaustion = 6
    }

    public class SmartRegimeInfo
    {
        public string Symbol { get; set; } = string.Empty;
        public KlineInterval Interval { get; set; }

        // Базовый режим из AiMarketRegimeService
        public MarketRegime BaseRegime { get; set; }

        // Смарт-режим (с учётом волы, наклона, корреляции и т.п.)
        public SmartRegimeType SmartType { get; set; }

        // То, что уже считает AiMarketRegimeService
        public decimal TrendSlopePercent { get; set; }      // для логов {P2}
        public decimal VolatilityPercent { get; set; }      // для логов {P2}

        // Корреляция с BTCUSDT (если есть)
        public decimal CorrelationToBtc { get; set; }

        // 0..1 — уверенность в режиме
        public decimal Confidence { get; set; }

        // Флаги поведения
        public bool IsDangerChopZone { get; set; }
        public bool AllowAggressiveTrendEntries { get; set; }
        public bool AllowCounterTrendEntries { get; set; }
        public bool HighTfSafetyMode { get; set; } = false;


        public bool IsVolCompression { get; set; } = false;
        public bool IsControlledTrend { get; set; } = false;
        public string EntryProfile { get; set; } = string.Empty;
        public decimal RiskBias { get; set; }
 
        public decimal SafetyRiskMultiplier { get; set; } = 1m;

    }
}
