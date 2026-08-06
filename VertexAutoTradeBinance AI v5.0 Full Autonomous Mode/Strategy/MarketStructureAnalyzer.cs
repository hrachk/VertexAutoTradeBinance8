// ═══════════════════════════════════════════════════════════════════════════
// MarketStructureAnalyzer.cs
// Professional price action analysis — market structure, S/R levels,
// pattern detection, momentum score.
//
// Inspired by professional trading systems (JonyDong approach):
//  - Market Structure: LH/LL (bearish), HH/HL (bullish)
//  - Support/Resistance: structural swing levels with volume confirmation
//  - Patterns: Parallel Range, Symmetric Triangle, Channel
//  - Momentum Score (MOMO): RSI + ROC + MACD composite 0-100
//  - Volume Relative: current vs 20-bar average (xAvg)
//  - R-Multiple TP: TP1=1R, TP2=2R, TP3=3R from SL distance
// ═══════════════════════════════════════════════════════════════════════════

using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using System.Collections.Generic;
using System.Linq;
using System;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Strategy
{
    // ── Market Structure ───────────────────────────────────────────────────

    public enum MarketStructure
    {
        Unknown,
        BullishHHHL,    // HH/HL — Higher Highs, Higher Lows (bullish)
        BearishLHLL,    // LH/LL — Lower Highs, Lower Lows (bearish)
        Ranging,        // Mixed structure
        Breaking,       // Structure just broken (BoS / ChoCH)
    }

    public enum ChartPattern
    {
        None,
        ParallelRange,      // Price bouncing between parallel levels
        SymmetricTriangle,  // Converging highs and lows → breakout
        AscendingTriangle,  // Flat top + rising lows
        DescendingTriangle, // Flat bottom + falling highs
        Channel,            // Parallel trend channel
    }

    public record SwingPoint(decimal Price, long TimeMs, bool IsHigh, int BarIndex);

    public record SrLevel(
        decimal Price,
        int Touches,
        bool IsResistance,
        decimal Volume,
        double Strength // 0-1
    );

    public record MarketStructureInfo
    {
        public MarketStructure Structure  { get; init; } = MarketStructure.Unknown;
        public ChartPattern    Pattern    { get; init; } = ChartPattern.None;
        public int             PatternConfidence { get; init; } = 0; // 0-100 like JonyDong "99"
        public string          PatternLabel { get; init; } = "";     // "PARALLEL RANGE", "SYM TRIANGLE"
        public string          StructLabel  { get; init; } = "";     // "LH/LL", "HH/HL"
        public string          PatternAction { get; init; } = "";    // "WAIT BREAK DOWN", "WAIT BREAK UP"
        public decimal         MomoScore  { get; init; } = 0m;      // 0-100 like MOMO: 54
        public decimal         VolumeRatio { get; init; } = 1m;     // VOL: 0.0x, 1.5x
        public string          VwapPosition { get; init; } = "";    // "ABV", "BLW", "BLM"
        public decimal         Vwap       { get; init; } = 0m;
        public List<SrLevel>   SrLevels   { get; init; } = [];
        public List<SwingPoint> Swings    { get; init; } = [];
        public decimal         NearestSupport { get; init; } = 0m;
        public decimal         NearestResistance { get; init; } = 0m;
        public decimal         UpperChannel { get; init; } = 0m;
        public decimal         LowerChannel { get; init; } = 0m;
        public bool            IsWaitingBreakout { get; init; } = false;
    }

    // ── Main Analyzer ──────────────────────────────────────────────────────

    public static class MarketStructureAnalyzer
    {
        // ── Entry point ───────────────────────────────────────────────────

        public static MarketStructureInfo Analyze(
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            int lookback = 50)
        {
            if (klines == null || klines.Count < 30)
                return new MarketStructureInfo();

            int n = klines.Count;
            int start = Math.Max(0, n - lookback);
            var window = klines.Skip(start).ToList();

            // Core analysis
            var swings     = DetectSwings(window, period: 3);
            var structure  = DetectStructure(swings);
            var srLevels   = FindSrLevels(window, swings);
            var vwap       = CalcVwap(window);
            var momo       = CalcMomentumScore(window);
            var volRatio   = CalcVolumeRatio(window);
            var vwapPos    = CalcVwapPosition(window[^1].ClosePrice, vwap);
            var (pattern, pConf, pLabel, pAction) = DetectPattern(window, swings, structure);
            var (nearSup, nearRes) = FindNearestSrLevels(srLevels, window[^1].ClosePrice);
            var (upper, lower) = CalcChannel(swings);

            bool waitingBreakout = pConf >= 70 &&
                (pAction.Contains("WAIT") || pattern == ChartPattern.SymmetricTriangle);

            return new MarketStructureInfo
            {
                Structure      = structure,
                Pattern        = pattern,
                PatternConfidence = pConf,
                PatternLabel   = pLabel,
                PatternAction  = pAction,
                StructLabel    = StructureLabel(structure),
                MomoScore      = momo,
                VolumeRatio    = volRatio,
                VwapPosition   = vwapPos,
                Vwap           = vwap,
                SrLevels       = srLevels,
                Swings         = swings,
                NearestSupport = nearSup,
                NearestResistance = nearRes,
                UpperChannel   = upper,
                LowerChannel   = lower,
                IsWaitingBreakout = waitingBreakout,
            };
        }

        // ── Swing Detection ───────────────────────────────────────────────

        /// <summary>
        /// Detects swing highs and lows.
        /// A swing high = bar whose high is higher than `period` bars on each side.
        /// </summary>
        public static List<SwingPoint> DetectSwings(
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            int period = 3)
        {
            var result = new List<SwingPoint>();
            int n = klines.Count;

            for (int i = period; i < n - period; i++)
            {
                decimal high = klines[i].HighPrice;
                decimal low  = klines[i].LowPrice;
                long    ms   = (long)klines[i].OpenTime.ToUniversalTime()
                               .Subtract(DateTime.UnixEpoch).TotalMilliseconds;

                // Swing High: higher than 'period' bars on both sides
                bool isSwingHigh = true;
                bool isSwingLow  = true;
                for (int j = i - period; j <= i + period; j++)
                {
                    if (j == i) continue;
                    if (klines[j].HighPrice >= high) isSwingHigh = false;
                    if (klines[j].LowPrice  <= low)  isSwingLow  = false;
                }

                if (isSwingHigh)
                    result.Add(new SwingPoint(high, ms, IsHigh: true, BarIndex: i));
                if (isSwingLow)
                    result.Add(new SwingPoint(low,  ms, IsHigh: false, BarIndex: i));
            }

            return result.OrderBy(s => s.TimeMs).ToList();
        }

        // ── Market Structure Detection ────────────────────────────────────

        /// <summary>
        /// Detects LH/LL (bearish) or HH/HL (bullish) structure
        /// by comparing the last 3 swing highs and 3 swing lows.
        /// </summary>
        public static MarketStructure DetectStructure(List<SwingPoint> swings)
        {
            var highs = swings.Where(s => s.IsHigh).TakeLast(4).ToList();
            var lows  = swings.Where(s => !s.IsHigh).TakeLast(4).ToList();

            if (highs.Count < 2 || lows.Count < 2)
                return MarketStructure.Unknown;

            // Count HH vs LH
            int hhCount = 0, lhCount = 0;
            for (int i = 1; i < highs.Count; i++)
            {
                if (highs[i].Price > highs[i - 1].Price) hhCount++;
                else lhCount++;
            }

            // Count HL vs LL
            int hlCount = 0, llCount = 0;
            for (int i = 1; i < lows.Count; i++)
            {
                if (lows[i].Price > lows[i - 1].Price) hlCount++;
                else llCount++;
            }

            bool bullish = hhCount > lhCount && hlCount > llCount;
            bool bearish = lhCount > hhCount && llCount > hlCount;

            if (bullish) return MarketStructure.BullishHHHL;
            if (bearish) return MarketStructure.BearishLHLL;
            return MarketStructure.Ranging;
        }

        static string StructureLabel(MarketStructure s) => s switch
        {
            MarketStructure.BullishHHHL => "HH/HL",
            MarketStructure.BearishLHLL => "LH/LL",
            MarketStructure.Ranging     => "RANGE",
            MarketStructure.Breaking    => "BREAK",
            _                           => "—",
        };

        // ── S/R Level Detection ───────────────────────────────────────────

        /// <summary>
        /// Finds key support/resistance levels:
        /// - Swing highs/lows that have been tested 2+ times
        /// - Clusters within 0.3% of each other = same level
        /// </summary>
        public static List<SrLevel> FindSrLevels(
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            List<SwingPoint> swings,
            decimal clusterPct = 0.003m)
        {
            if (swings.Count == 0 || klines.Count == 0)
                return [];

            decimal avgPrice = klines.Average(k => k.ClosePrice);
            decimal cluster  = avgPrice * clusterPct;

            // Group swing points into clusters
            var levels = new List<SrLevel>();
            var used   = new HashSet<int>();

            for (int i = 0; i < swings.Count; i++)
            {
                if (used.Contains(i)) continue;

                var group = new List<SwingPoint> { swings[i] };
                for (int j = i + 1; j < swings.Count; j++)
                {
                    if (!used.Contains(j) &&
                        Math.Abs(swings[j].Price - swings[i].Price) <= cluster)
                    {
                        group.Add(swings[j]);
                        used.Add(j);
                    }
                }
                used.Add(i);

                if (group.Count < 2) continue; // must be tested at least twice

                decimal levelPrice = group.Average(s => s.Price);
                bool isResistance  = group.Average(s => s.IsHigh ? 1 : 0) >= 0.5;
                decimal strength   = (decimal)Math.Min(1.0, group.Count / 4.0);

                // Estimate volume at this level
                decimal vol = klines.Where(k =>
                    Math.Abs(k.HighPrice - levelPrice) <= cluster ||
                    Math.Abs(k.LowPrice  - levelPrice) <= cluster)
                    .Sum(k => k.Volume);

                levels.Add(new SrLevel(levelPrice, group.Count, isResistance, vol, (double)strength));
            }

            return levels.OrderBy(l => l.Price).ToList();
        }

        static (decimal nearSup, decimal nearRes) FindNearestSrLevels(
            List<SrLevel> levels, decimal currentPrice)
        {
            if (levels.Count == 0) return (0m, 0m);

            decimal nearSup = levels.Where(l => l.Price < currentPrice)
                                    .OrderByDescending(l => l.Price)
                                    .FirstOrDefault()?.Price ?? 0m;
            decimal nearRes = levels.Where(l => l.Price > currentPrice)
                                    .OrderBy(l => l.Price)
                                    .FirstOrDefault()?.Price ?? 0m;
            return (nearSup, nearRes);
        }

        // ── Pattern Detection ─────────────────────────────────────────────

        static (ChartPattern pattern, int conf, string label, string action)
            DetectPattern(
                IReadOnlyList<BinanceFuturesUsdtKline> klines,
                List<SwingPoint> swings,
                MarketStructure structure)
        {
            // Need at least 4 swings
            if (swings.Count < 4)
                return (ChartPattern.None, 0, "", "");

            var highs = swings.Where(s => s.IsHigh).TakeLast(5).ToList();
            var lows  = swings.Where(s => !s.IsHigh).TakeLast(5).ToList();

            if (highs.Count < 3 || lows.Count < 3)
                return (ChartPattern.None, 0, "", "");

            // Check for Symmetric Triangle: converging highs and lows
            bool highsConverging = IsDescending(highs.Select(h => h.Price).ToList());
            bool lowsConverging  = IsAscending(lows.Select(l => l.Price).ToList());
            if (highsConverging && lowsConverging)
            {
                int conf = CalcPatternConfidence(highs, lows, "triangle");
                return (ChartPattern.SymmetricTriangle, conf,
                    "SYM TRIANGLE",
                    structure == MarketStructure.BearishLHLL ? "WAIT BREAK DOWN" : "WAIT BREAK UP");
            }

            // Check for Parallel Range: flat-ish highs AND flat-ish lows
            bool highsFlat = IsFlat(highs.Select(h => h.Price).ToList(), tolerance: 0.015m);
            bool lowsFlat  = IsFlat(lows.Select(l => l.Price).ToList(),  tolerance: 0.015m);
            if (highsFlat && lowsFlat)
            {
                int conf = CalcPatternConfidence(highs, lows, "range");
                return (ChartPattern.ParallelRange, conf,
                    "PARALLEL RANGE",
                    structure == MarketStructure.BearishLHLL ? "WAIT BREAK DOWN" : "WAIT BREAK UP");
            }

            // Check for Descending Triangle: flat support + lower highs
            bool flatSupport = IsFlat(lows.Select(l => l.Price).ToList(), tolerance: 0.012m);
            if (highsConverging && flatSupport)
            {
                int conf = CalcPatternConfidence(highs, lows, "triangle");
                return (ChartPattern.DescendingTriangle, conf, "DESC TRIANGLE", "WAIT BREAK DOWN");
            }

            // Check for Ascending Triangle: flat resistance + higher lows
            bool flatResistance = IsFlat(highs.Select(h => h.Price).ToList(), tolerance: 0.012m);
            if (lowsConverging && flatResistance)
            {
                int conf = CalcPatternConfidence(highs, lows, "triangle");
                return (ChartPattern.AscendingTriangle, conf, "ASC TRIANGLE", "WAIT BREAK UP");
            }

            // Channel: parallel highs and lows with same slope direction
            decimal highSlope = CalcSlope(highs.Select(h => h.Price).ToList());
            decimal lowSlope  = CalcSlope(lows.Select(l => l.Price).ToList());
            if (Math.Abs(highSlope - lowSlope) < 0.002m && Math.Abs(highSlope) > 0.001m)
            {
                string action = highSlope > 0 ? "RIDE CHANNEL UP" : "RIDE CHANNEL DOWN";
                return (ChartPattern.Channel, 70, "CHANNEL", action);
            }

            return (ChartPattern.None, 0, "", "");
        }

        static int CalcPatternConfidence(
            List<SwingPoint> highs,
            List<SwingPoint> lows,
            string type)
        {
            // Confidence based on:
            // 1. How many touches (more = higher confidence)
            // 2. How clean the pattern is (less deviation = higher)
            int touches = highs.Count + lows.Count;
            decimal highDev = StdDev(highs.Select(h => h.Price).ToList());
            decimal lowDev  = StdDev(lows.Select(l => l.Price).ToList());
            decimal avgPrice = (highs.Average(h => h.Price) + lows.Average(l => l.Price)) / 2;
            decimal cleanness = avgPrice > 0
                ? 1m - Math.Min(1m, (highDev + lowDev) / avgPrice / 0.05m)
                : 0m;

            int baseConf = Math.Min(95, touches * 12);
            int confAdj  = (int)(baseConf * (double)cleanness);
            return Math.Max(50, confAdj);
        }

        static (decimal upper, decimal lower) CalcChannel(List<SwingPoint> swings)
        {
            if (swings.Count < 4) return (0m, 0m);
            var highs = swings.Where(s => s.IsHigh).TakeLast(3).ToList();
            var lows  = swings.Where(s => !s.IsHigh).TakeLast(3).ToList();
            if (highs.Count == 0 || lows.Count == 0) return (0m, 0m);
            return (highs.Max(h => h.Price), lows.Min(l => l.Price));
        }

        // ── Momentum Score (MOMO) ─────────────────────────────────────────

        /// <summary>
        /// Composite momentum score 0-100 (like JonyDong "MOMO: 54"):
        /// RSI(14) × 0.40 + NormalizedROC × 0.35 + NormalizedMACD × 0.25
        /// </summary>
        public static decimal CalcMomentumScore(IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines.Count < 26) return 50m;

            decimal rsi  = CalcRsi(klines, 14);
            decimal roc  = CalcRoc(klines, 10);  // Rate of Change %
            decimal macd = CalcMacdHistNorm(klines); // Normalized MACD histogram

            // RSI is already 0-100
            // ROC: normalize to 0-100 (clamp -5% to +5% → 0-100)
            decimal rocNorm = Math.Clamp(50m + roc * 10m, 0m, 100m);
            // MACD hist: normalize -1 to +1 → 0-100
            decimal macdNorm = Math.Clamp(50m + macd * 50m, 0m, 100m);

            return Math.Clamp(rsi * 0.40m + rocNorm * 0.35m + macdNorm * 0.25m, 0m, 100m);
        }

        static decimal CalcRsi(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period = 14)
        {
            if (klines.Count < period + 1) return 50m;
            int n = klines.Count;
            decimal gainSum = 0m, lossSum = 0m;
            for (int i = n - period; i < n; i++)
            {
                decimal diff = klines[i].ClosePrice - klines[i - 1].ClosePrice;
                if (diff > 0) gainSum += diff;
                else lossSum += Math.Abs(diff);
            }
            if (lossSum == 0) return 100m;
            decimal rs = gainSum / lossSum;
            return 100m - 100m / (1m + rs);
        }

        static decimal CalcRoc(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period)
        {
            int n = klines.Count;
            if (n < period + 1) return 0m;
            decimal prev = klines[n - 1 - period].ClosePrice;
            if (prev == 0) return 0m;
            return (klines[n - 1].ClosePrice - prev) / prev * 100m;
        }

        static decimal CalcMacdHistNorm(IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines.Count < 26) return 0m;
            int n = klines.Count;
            decimal ema12 = CalcEma(klines.Select(k => k.ClosePrice).ToList(), 12);
            decimal ema26 = CalcEma(klines.Select(k => k.ClosePrice).ToList(), 26);
            decimal macd  = ema12 - ema26;
            decimal signal = CalcEma(
                klines.TakeLast(9).Select(k => k.ClosePrice).ToList(), 9);
            decimal hist = macd - signal;
            decimal avgPrice = klines[n - 1].ClosePrice;
            return avgPrice > 0 ? hist / avgPrice : 0m;
        }

        static decimal CalcEma(List<decimal> prices, int period)
        {
            if (prices.Count < period) return prices.LastOrDefault();
            decimal k = 2m / (period + 1);
            decimal ema = prices.Take(period).Average();
            for (int i = period; i < prices.Count; i++)
                ema = prices[i] * k + ema * (1m - k);
            return ema;
        }

        // ── Volume Relative ───────────────────────────────────────────────

        /// <summary>
        /// Volume as multiple of 20-bar average (like JonyDong "VOL: 0.0x, 1.5x")
        /// </summary>
        public static decimal CalcVolumeRatio(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period = 20)
        {
            if (klines.Count < period + 1) return 1m;
            int n = klines.Count;
            decimal avgVol = klines.Skip(n - period - 1).Take(period).Average(k => k.Volume);
            if (avgVol <= 0) return 1m;
            return Math.Round(klines[n - 1].Volume / avgVol, 1);
        }

        // ── VWAP ──────────────────────────────────────────────────────────

        public static decimal CalcVwap(IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines.Count == 0) return 0m;
            decimal sumPV = 0m, sumV = 0m;
            foreach (var k in klines)
            {
                decimal typical = (k.HighPrice + k.LowPrice + k.ClosePrice) / 3m;
                sumPV += typical * k.Volume;
                sumV  += k.Volume;
            }
            return sumV > 0 ? sumPV / sumV : 0m;
        }

        /// <summary>ABV = above VWAP, BLW = below, BLM = VWAP below market (same as ABV)</summary>
        static string CalcVwapPosition(decimal price, decimal vwap)
        {
            if (vwap <= 0) return "";
            decimal pct = (price - vwap) / vwap;
            if (pct > 0.005m) return "ABV";   // more than 0.5% above VWAP
            if (pct < -0.005m) return "BLW";  // more than 0.5% below VWAP
            return "AT";                        // at VWAP
        }

        // ── R-Multiple TP Calculation ──────────────────────────────────────

        /// <summary>
        /// Calculate TP levels as R-multiples of the SL distance.
        /// TP1 = 1R, TP2 = 2R, TP3 = 3R (like JonyDong bot).
        /// Optionally clamp to nearest S/R levels for more accuracy.
        /// </summary>
        public static (decimal tp1, decimal tp2, decimal tp3) CalcRMultipleTps(
            decimal entry,
            decimal stopLoss,
            bool isLong,
            decimal r1 = 1.0m,
            decimal r2 = 2.0m,
            decimal r3 = 3.0m,
            List<SrLevel>? srLevels = null)
        {
            decimal risk = Math.Abs(entry - stopLoss);
            if (risk <= 0) return (0m, 0m, 0m);

            decimal sign = isLong ? 1m : -1m;
            decimal tp1 = entry + sign * risk * r1;
            decimal tp2 = entry + sign * risk * r2;
            decimal tp3 = entry + sign * risk * r3;

            // Snap TP to nearest S/R level within 15% of the target
            if (srLevels != null && srLevels.Count > 0)
            {
                tp1 = SnapToSrLevel(tp1, srLevels, risk * 0.15m, isLong);
                tp2 = SnapToSrLevel(tp2, srLevels, risk * 0.15m, isLong);
                tp3 = SnapToSrLevel(tp3, srLevels, risk * 0.20m, isLong);
            }

            return (tp1, tp2, tp3);
        }

        /// <summary>
        /// Find nearest S/R level to snap TP to — adds precision.
        /// If no level found within tolerance, returns original TP.
        /// </summary>
        static decimal SnapToSrLevel(
            decimal tp,
            List<SrLevel> levels,
            decimal tolerance,
            bool isLong)
        {
            var candidates = isLong
                ? levels.Where(l => !l.IsResistance && l.Price >= tp - tolerance && l.Price <= tp + tolerance)
                : levels.Where(l =>  l.IsResistance && l.Price >= tp - tolerance && l.Price <= tp + tolerance);

            var best = candidates.OrderByDescending(l => l.Strength).FirstOrDefault();
            return best?.Price ?? tp;
        }

        /// <summary>
        /// Calculate structural SL: behind the nearest swing point on the wrong side.
        /// More accurate than ATR-based SL.
        /// </summary>
        public static decimal CalcStructuralSl(
            decimal entry,
            bool isLong,
            List<SwingPoint> swings,
            decimal atr,
            decimal buffer = 0.0025m)
        {
            if (swings.Count == 0 || atr <= 0)
                return isLong ? entry * (1m - 0.020m) : entry * (1m + 0.020m);

            if (isLong)
            {
                // For LONG: SL below the nearest swing low below entry
                var swingLows = swings.Where(s => !s.IsHigh && s.Price < entry)
                                       .OrderByDescending(s => s.Price)
                                       .ToList();
                if (swingLows.Count > 0)
                {
                    decimal slBase = swingLows[0].Price;
                    // Buffer: small amount below the swing low
                    decimal sl = slBase - Math.Max(atr * 0.35m, slBase * buffer);
                    // Safety: SL must not be more than 3×ATR from entry
                    if (entry - sl > atr * 4.0m)
                        sl = entry - atr * 2.5m;
                    return sl;
                }
            }
            else
            {
                // For SHORT: SL above the nearest swing high above entry
                var swingHighs = swings.Where(s => s.IsHigh && s.Price > entry)
                                        .OrderBy(s => s.Price)
                                        .ToList();
                if (swingHighs.Count > 0)
                {
                    decimal slBase = swingHighs[0].Price;
                    decimal sl = slBase + Math.Max(atr * 0.35m, slBase * buffer);
                    if (sl - entry > atr * 4.0m)
                        sl = entry + atr * 2.5m;
                    return sl;
                }
            }

            // Fallback to ATR-based
            return isLong
                ? entry - atr * 2.0m
                : entry + atr * 2.0m;
        }

        // ── Math helpers ──────────────────────────────────────────────────

        static bool IsDescending(List<decimal> values)
        {
            if (values.Count < 2) return false;
            int descCount = 0;
            for (int i = 1; i < values.Count; i++)
                if (values[i] < values[i - 1]) descCount++;
            return descCount >= (values.Count - 1) * 0.7;
        }

        static bool IsAscending(List<decimal> values)
        {
            if (values.Count < 2) return false;
            int ascCount = 0;
            for (int i = 1; i < values.Count; i++)
                if (values[i] > values[i - 1]) ascCount++;
            return ascCount >= (values.Count - 1) * 0.7;
        }

        static bool IsFlat(List<decimal> values, decimal tolerance = 0.015m)
        {
            if (values.Count < 2) return false;
            decimal avg = values.Average();
            if (avg == 0) return false;
            return values.All(v => Math.Abs(v - avg) / avg <= tolerance);
        }

        static decimal CalcSlope(List<decimal> values)
        {
            if (values.Count < 2) return 0m;
            // Simple linear regression slope
            decimal n = values.Count;
            decimal sumX = 0m, sumY = 0m, sumXY = 0m, sumX2 = 0m;
            for (int i = 0; i < values.Count; i++)
            {
                sumX  += i;
                sumY  += values[i];
                sumXY += i * values[i];
                sumX2 += i * i;
            }
            decimal denom = n * sumX2 - sumX * sumX;
            if (denom == 0) return 0m;
            decimal slope = (n * sumXY - sumX * sumY) / denom;
            decimal avgY  = sumY / n;
            return avgY > 0 ? slope / avgY : 0m; // Normalize by price
        }

        static decimal StdDev(List<decimal> values)
        {
            if (values.Count < 2) return 0m;
            decimal avg = values.Average();
            decimal variance = values.Average(v => (v - avg) * (v - avg));
            return (decimal)Math.Sqrt((double)variance);
        }
    }
}
