using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.Strategy.Confidence
{
    /// <summary>
    /// Aggregates FINAL confidence for a concrete trade signal.
    /// Philosophy:
    /// - SmartRegime gives MARKET confidence
    /// - Pattern gives ENTRY confidence
    /// - TF alignment gives STRUCTURAL confidence
    /// - Risk clamps prevent casino behavior
    /// 
    /// Output is used by Gate2_Confidence WITHOUT changing gate semantics.
    /// </summary>
    public sealed class SignalConfidenceAggregator
    {
        private readonly SmartRegimeService _smartRegime;

        public SignalConfidenceAggregator(SmartRegimeService smartRegime)
        {
            _smartRegime = smartRegime;
        }

        public ConfidenceResult Evaluate(
            SmartRegimeInfo smart,
            TradeSignal signal,
            KlineInterval tf)
        {
            // ===============================
            // 1) BASE: Market regime confidence
            // ===============================
            decimal regimeConf = Clamp01(smart.Confidence);

            // ===============================
            // 2) PATTERN QUALITY
            // ===============================
            decimal patternConf = GetPatternConfidence(signal);

            // ===============================
            // 3) TF STRUCTURAL ALIGNMENT
            // ===============================
            decimal tfConf = GetTfAlignmentConfidence(smart, tf);

            // ===============================
            // 4) RISK CLAMPS (ANTI-CASINO)
            // ===============================
            decimal riskClamp = GetRiskClamp(smart, signal);

            // ===============================
            // 5) FINAL AGGREGATION
            // ===============================
            decimal final =
                regimeConf * 0.45m +
                patternConf * 0.35m +
                tfConf * 0.20m;

            final *= riskClamp;

            final = Clamp01(final);

            return new ConfidenceResult(
                Final: final,
                Regime: regimeConf,
                Pattern: patternConf,
                Alignment: tfConf,
                RiskClamp: riskClamp
            );
        }

        // ============================================================
        // -------------------- COMPONENTS ----------------------------
        // ============================================================

        private static decimal GetPatternConfidence(TradeSignal s)
        {
            // Base default
            decimal conf = 0.50m;

            if (s == null) return conf;

            // Strongest: structured pullback
            if (s.Reason.Contains("PULLBACK"))
                conf = 0.80m;

            // Early trend join (start of impulse)
            else if (s.Reason.Contains("EARLY_TREND"))
                conf = 0.65m;

            // Liquidity grab continuation
            else if (s.Reason.Contains("LIQUIDITY"))
                conf = 0.60m;

            // Volatility breakout (riskier)
            else if (s.Reason.Contains("VOLATILITY"))
                conf = 0.55m;

            return Clamp01(conf);
        }

        private static decimal GetTfAlignmentConfidence(
            SmartRegimeInfo smart,
            KlineInterval tf)
        {
            // Base
            decimal conf = 0.50m;

            bool trendLike =
                smart.BaseRegime == MarketRegime.UpTrend ||
                smart.BaseRegime == MarketRegime.DownTrend ||
                smart.BaseRegime == MarketRegime.StrongUpTrend ||
                smart.BaseRegime == MarketRegime.StrongDownTrend;

            bool rangeLike =
                smart.BaseRegime == MarketRegime.Range ||
                smart.SmartType == SmartRegimeType.SmartRange ||
                smart.SmartType == SmartRegimeType.SmartSqueeze;

            if (tf == KlineInterval.OneMinute)
            {
                // 1m needs confirmation
                conf = trendLike ? 0.55m : 0.45m;
            }
            else if (tf == KlineInterval.FiveMinutes)
            {
                conf = trendLike ? 0.70m : 0.55m;
            }
            else if (tf == KlineInterval.FifteenMinutes)
            {
                conf = 0.75m;
            }
            else if (tf >= KlineInterval.OneHour)
            {
                conf = 0.80m;
            }

            if (rangeLike && tf <= KlineInterval.FiveMinutes)
                conf -= 0.10m;

            return Clamp01(conf);
        }

        private static decimal GetRiskClamp(
            SmartRegimeInfo smart,
            TradeSignal signal)
        {
            decimal clamp = 1.00m;

            // Counter-trend penalty
            if (signal != null)
            {
                bool againstTrend =
                    (smart.TrendSlopePercent > 0 && signal.Side == SignalSide.Sell) ||
                    (smart.TrendSlopePercent < 0 && signal.Side == SignalSide.Buy);

                if (againstTrend)
                    clamp *= 0.70m;
            }

            // Chop / danger zones
            if (smart.IsDangerChopZone)
                clamp *= 0.60m;

            // Weak confidence regime
            if (smart.Confidence < 0.40m)
                clamp *= 0.85m;

            return Math.Clamp(clamp, 0.50m, 1.00m);
        }

        private static decimal Clamp01(decimal v)
            => Math.Clamp(v, 0m, 1m);
    }

    // ============================================================
    // DTO
    // ============================================================
    public sealed record ConfidenceResult(
        decimal Final,
        decimal Regime,
        decimal Pattern,
        decimal Alignment,
        decimal RiskClamp
    );
}
