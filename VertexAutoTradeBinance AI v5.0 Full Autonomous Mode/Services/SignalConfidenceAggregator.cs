using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.Strategy.Confidence
{
    /// <summary>
    /// Aggregates FINAL confidence for a concrete trade signal.
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
            if (smart == null)
                throw new ArgumentNullException(nameof(smart));

            // 1) BASE: Market regime confidence
            decimal regimeConf = Clamp01(smart.Confidence);

            // 2) PATTERN QUALITY
            decimal patternConf = GetPatternConfidence(signal);

            // 3) TF STRUCTURAL ALIGNMENT
            decimal tfConf = GetTfAlignmentConfidence(smart, tf);

            // 4) RISK CLAMPS
            decimal riskClamp = GetRiskClamp(smart, signal);

            // 5) FINAL AGGREGATION
            decimal baseScore = regimeConf * 0.40m + patternConf * 0.40m + tfConf * 0.20m;

            // synergy bonus
            decimal synergy = 0m;
            if (patternConf >= 0.75m && regimeConf >= 0.50m)
                synergy = 0.10m;
            else if (patternConf >= 0.65m)
                synergy = 0.05m;

            decimal final = Clamp01(baseScore + synergy);

            // apply risk
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

        // ================= COMPONENTS =================

        private static decimal GetPatternConfidence(TradeSignal s)
        {
            decimal conf = 0.50m;
            if (s?.Reason == null) return conf;

            string reason = s.Reason.ToUpperInvariant();

            // PA signals: highest quality — they passed Market Structure + MOMO + Volume + VWAP + S/R checks
            if (reason.Contains("PA_STRUCTURE"))
                conf = 0.82m;  // structure trade (HH/HL, LH/LL + S/R confluence)
            if (reason.Contains("_UPTREND_SR") || reason.Contains("_DOWNTREND_SR"))
                conf = 0.87m;  // confirmed structure + S/R-anchored TP (highest quality)
            else if (reason.Contains("PA_BREAKOUT"))
                conf = 0.78m;  // pattern breakout (triangle, parallel range)
            else if (reason.Contains("PA_SR_BOUNCE"))
                conf = 0.72m;  // S/R bounce (well-tested level)
            // Traditional signals
            else if (reason.Contains("PULLBACK"))
                conf = 0.80m;
            else if (reason.Contains("EARLY_TREND"))
                conf = 0.65m;
            else if (reason.Contains("LIQUIDITY"))
                conf = 0.60m;
            else if (reason.Contains("VOLATILITY"))
                conf = 0.55m;
            // PA signals without the above keywords — still better than default
            else if (reason.StartsWith("PA_"))
                conf = 0.70m;

            return Clamp01(conf);
        }

        private static decimal GetTfAlignmentConfidence(SmartRegimeInfo smart, KlineInterval tf)
        {
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
                conf = trendLike ? 0.52m : 0.44m;
            else if (tf == KlineInterval.FiveMinutes)
                conf = trendLike ? 0.69m : 0.54m;
            else if (tf == KlineInterval.FifteenMinutes)
                conf = 0.72m;
            else if (tf >= KlineInterval.OneHour)
                conf = 0.75m;

            if (rangeLike && tf <= KlineInterval.FiveMinutes)
                conf -= 0.10m;

            return Clamp01(conf);
        }

        private static decimal GetRiskClamp(SmartRegimeInfo smart, TradeSignal signal)
        {
            decimal clamp = 1.0m;

            if (signal != null)
            {
                // Range and SR-bounce signals are DESIGNED to go against the micro-trend
                // (buy at bottom of range, sell at top — even if slope is slightly positive).
                // Applying against-trend penalty kills these signals wrongly.
                bool isRangeOrBounce =
                    signal.Reason?.Contains("RANGE") == true ||
                    signal.Reason?.Contains("PA_SR_BOUNCE") == true;

                // Only penalise when: not a range/bounce AND slope is meaningful (>0.3%)
                bool againstTrend =
                    !isRangeOrBounce &&
                    Math.Abs(smart.TrendSlopePercent) > 0.3m &&
                    ((smart.TrendSlopePercent > 0 && signal.Side == SignalSide.Sell) ||
                     (smart.TrendSlopePercent < 0 && signal.Side == SignalSide.Buy));

                if (againstTrend)
                    clamp *= 0.70m;
            }

            if (smart.IsDangerChopZone)
                clamp *= 0.60m;

            if (smart.Confidence < 0.40m)
                clamp *= 0.85m;

            return Math.Clamp(clamp, 0.50m, 1.0m);
        }

        private static decimal Clamp01(decimal v) => Math.Clamp(v, 0m, 1m);
    }

    public sealed record ConfidenceResult(
        decimal Final,
        decimal Regime,
        decimal Pattern,
        decimal Alignment,
        decimal RiskClamp
    );
}
