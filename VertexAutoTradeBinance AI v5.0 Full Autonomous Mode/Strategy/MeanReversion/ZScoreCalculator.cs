using Binance.Net.Objects.Models.Futures;

namespace VertexAutoTradeBinance8.Strategy.MeanReversion
{
    /// <summary>
    /// Statistical helpers for the mean-reversion strategy.
    ///
    /// Z-Score formula here is intentionally independent from
    /// AiMarketRegimeService.DeviationScore (which shares its window
    /// with TrendLookback). Mean-reversion needs its own configurable
    /// window — classic Bollinger-style 20 bars by default — so it
    /// reacts at a different sensitivity than the trend/regime detector.
    /// </summary>
    public static class ZScoreCalculator
    {
        /// <summary>
        /// Z-Score of the last close relative to the rolling mean/std
        /// of the previous <paramref name="window"/> closes (inclusive
        /// of the last bar itself, matching standard Bollinger convention).
        /// Returns 0 if there isn't enough data or std is zero (flat market).
        /// </summary>
        public static decimal ZScore(IReadOnlyList<BinanceFuturesUsdtKline> klines, int window = 20)
        {
            if (klines == null || klines.Count < window) return 0m;

            int last = klines.Count - 1;
            int start = last - window + 1;

            decimal mean = 0m;
            for (int i = start; i <= last; i++)
                mean += klines[i].ClosePrice;
            mean /= window;

            decimal variance = 0m;
            for (int i = start; i <= last; i++)
            {
                decimal d = klines[i].ClosePrice - mean;
                variance += d * d;
            }
            variance /= window;

            decimal std = (decimal)Math.Sqrt((double)variance);
            if (std <= 0m) return 0m;

            return (klines[last].ClosePrice - mean) / std;
        }

        /// <summary>
        /// Full band info (mean, std, upper/lower N-sigma bands, current Z)
        /// in one pass — used both for entry detection and for computing
        /// the mean-reversion target price (the "0σ" exit level).
        /// </summary>
        public static MeanReversionBands ComputeBands(
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            int window = 20,
            decimal entrySigma = 2.0m)
        {
            if (klines == null || klines.Count < window)
                return MeanReversionBands.Empty;

            int last = klines.Count - 1;
            int start = last - window + 1;

            decimal mean = 0m;
            for (int i = start; i <= last; i++)
                mean += klines[i].ClosePrice;
            mean /= window;

            decimal variance = 0m;
            for (int i = start; i <= last; i++)
            {
                decimal d = klines[i].ClosePrice - mean;
                variance += d * d;
            }
            variance /= window;

            decimal std = (decimal)Math.Sqrt((double)variance);
            decimal lastClose = klines[last].ClosePrice;
            decimal z = std > 0m ? (lastClose - mean) / std : 0m;

            return new MeanReversionBands
            {
                Mean = mean,
                Std = std,
                LastClose = lastClose,
                ZScore = z,
                UpperEntryBand = mean + std * entrySigma,
                LowerEntryBand = mean - std * entrySigma,
                Window = window
            };
        }

        /// <summary>
        /// Average volume over <paramref name="period"/> bars, excluding
        /// the current (last) bar — so "current volume vs average" is a
        /// fair comparison against history, not against itself.
        /// </summary>
        public static decimal AvgVolume(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period = 50)
        {
            if (klines == null || klines.Count < period + 1) return 0m;

            int last = klines.Count - 1;
            int start = last - period; // excludes `last`

            decimal sum = 0m;
            for (int i = start; i < last; i++)
                sum += klines[i].Volume;

            return sum / period;
        }

        /// <summary>
        /// True if the current bar's volume is a "surge" — at least
        /// <paramref name="multiplier"/>x the trailing average — which
        /// is the volume-confirmation leg of the strategy (filters out
        /// low-liquidity noise that produces a Z-score spike without
        /// real participation behind it).
        /// </summary>
        public static bool IsVolumeSurge(
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            int avgPeriod = 50,
            decimal multiplier = 1.5m)
        {
            if (klines == null || klines.Count < avgPeriod + 1) return false;

            decimal avg = AvgVolume(klines, avgPeriod);
            if (avg <= 0m) return false;

            decimal currentVol = klines[^1].Volume;
            return currentVol >= avg * multiplier;
        }

        /// <summary>
        /// ATR(period) at a given index — local copy since StrategyEngine's
        /// version is private. Same Wilder-style true-range average used
        /// throughout the rest of the codebase for consistency.
        /// </summary>
        public static decimal Atr(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period, int lastIndex)
        {
            if (klines == null || lastIndex < period) return 0m;

            decimal sum = 0m;
            for (int i = lastIndex - period + 1; i <= lastIndex; i++)
            {
                decimal high = klines[i].HighPrice;
                decimal low = klines[i].LowPrice;
                decimal prevClose = i > 0 ? klines[i - 1].ClosePrice : klines[i].OpenPrice;

                decimal tr = Math.Max(high - low,
                              Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
                sum += tr;
            }
            return sum / period;
        }

        /// <summary>
        /// Simple reversal-confirmation check: is the LAST closed candle
        /// actually turning back toward the mean, rather than the price
        /// just touching -2σ mid-fall (a "falling knife")?
        ///
        /// Long side: last candle is bullish (close > open) AND its low
        /// is the lowest of the last `lookback` bars (i.e. we just made
        /// a fresh local low and are now bouncing off it).
        /// Short side is the mirror image.
        /// </summary>
        public static bool HasReversalConfirmation(
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            bool isLongSetup,
            int lookback = 5)
        {
            if (klines == null || klines.Count < lookback + 1) return false;

            int last = klines.Count - 1;
            var lastCandle = klines[last];
            bool bullish = lastCandle.ClosePrice > lastCandle.OpenPrice;
            bool bearish = lastCandle.ClosePrice < lastCandle.OpenPrice;

            if (isLongSetup)
            {
                if (!bullish) return false;
                decimal lowestLow = decimal.MaxValue;
                for (int i = last - lookback + 1; i <= last; i++)
                    lowestLow = Math.Min(lowestLow, klines[i].LowPrice);
                // The low of THIS candle (or very close to it) should be
                // the lowest of the lookback window — confirms we just
                // bounced off a fresh local extreme, not mid-collapse.
                return lastCandle.LowPrice <= lowestLow * 1.0008m; // 0.08% tolerance
            }
            else
            {
                if (!bearish) return false;
                decimal highestHigh = decimal.MinValue;
                for (int i = last - lookback + 1; i <= last; i++)
                    highestHigh = Math.Max(highestHigh, klines[i].HighPrice);
                return lastCandle.HighPrice >= highestHigh * 0.9992m;
            }
        }
    }

    public sealed class MeanReversionBands
    {
        public decimal Mean { get; init; }
        public decimal Std { get; init; }
        public decimal LastClose { get; init; }
        public decimal ZScore { get; init; }
        public decimal UpperEntryBand { get; init; }
        public decimal LowerEntryBand { get; init; }
        public int Window { get; init; }

        public bool IsValid => Std > 0m && Window > 0;

        public static MeanReversionBands Empty => new();
    }
}
