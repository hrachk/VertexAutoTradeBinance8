// ═══════════════════════════════════════════════════════════════════════════
// PriceActionStrategy.cs
// Professional price action strategy inspired by high-winrate bots:
//
//  Signal pipeline (like JonyDong bot):
//  1. Market Structure (LH/LL or HH/HL)   — defines allowed side
//  2. Pattern Detection (Parallel Range, SYM TRIANGLE, Channel)
//  3. S/R Level confirmation               — entry near structural level
//  4. MOMO score filter                    — min momentum threshold
//  5. Volume filter                        — avoid dead markets (VOL < 0.3x)
//  6. VWAP position                        — directional confirmation
//  7. Structural SL                        — placed BEHIND nearest swing point
//  8. R-Multiple TPs                       — TP1=1R, TP2=2R, TP3=3R
//
//  Reason codes on signal:
//    "PA_STRUCTURE_LONG"   / "PA_STRUCTURE_SHORT"
//    "PA_BREAKOUT_LONG"    / "PA_BREAKOUT_SHORT"
//    "PA_SR_BOUNCE_LONG"   / "PA_SR_BOUNCE_SHORT"
// ═══════════════════════════════════════════════════════════════════════════

using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using System.Collections.Generic;
using System.Linq;
using System;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Strategy
{
    public static class PriceActionStrategy
    {
        // ── Config ────────────────────────────────────────────────────────
        private const decimal MinMomoScore   = 55m;   // was 35 — only strong momentum   // min MOMO to enter
        private const decimal MinVolumeRatio = 1.0m;  // was 0.3 — require real volume  // min VOL x average
        private const decimal MinRR          = 2.0m;  // was 1.2 — only quality RR  // min risk:reward
        private const decimal MaxSlAtrMult   = 3.0m;  // SL never > 3×ATR from entry

        // ── Main entry ────────────────────────────────────────────────────

        public static TradeSignal? TryEntry(
            string symbol,
            KlineInterval interval,
            IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines == null || klines.Count < 40) return null;

            int n  = klines.Count;
            var c0 = klines[n - 1];
            var c1 = klines[n - 2];
            decimal atr = CalcAtr(klines, 14);
            if (atr <= 0) return null;

            // Full price action analysis
            var msi = MarketStructureAnalyzer.Analyze(klines, lookback: 60);

            // Global filters
            if (msi.MomoScore < MinMomoScore)   return null;
            if (msi.VolumeRatio < MinVolumeRatio) return null;

            // Try each setup type in priority order
            return TryStructureEntry(symbol, interval, klines, c0, c1, atr, msi)
                ?? TryPatternBreakout(symbol, interval, klines, c0, c1, atr, msi)
                ?? TrySrBounce(symbol, interval, klines, c0, c1, atr, msi);
        }

        // ── A: Structure Trade ────────────────────────────────────────────

        static TradeSignal? TryStructureEntry(
            string symbol, KlineInterval interval,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            BinanceFuturesUsdtKline c0, BinanceFuturesUsdtKline c1,
            decimal atr, MarketStructureInfo msi)
        {
            if (msi.Structure == MarketStructure.Unknown ||
                msi.Structure == MarketStructure.Ranging) return null;

            bool isLong  = msi.Structure == MarketStructure.BullishHHHL;
            bool isShort = msi.Structure == MarketStructure.BearishLHLL;
            if (!isLong && !isShort) return null;

            int n = klines.Count;
            decimal ema21 = CalcEma(klines, 21, n - 1);
            decimal zone  = atr * 0.5m;

            // Must touch EMA21 zone (pullback to structure)
            bool touchedEma =
                (c0.LowPrice <= ema21 + zone && c0.HighPrice >= ema21 - zone) ||
                (c1.LowPrice <= ema21 + zone && c1.HighPrice >= ema21 - zone);
            if (!touchedEma) return null;

            // Rejection candle in the direction of structure
            bool bullCandle = c0.ClosePrice > c0.OpenPrice &&
                              c0.ClosePrice > c1.ClosePrice &&
                              c0.ClosePrice > ema21;
            bool bearCandle = c0.ClosePrice < c0.OpenPrice &&
                              c0.ClosePrice < c1.ClosePrice &&
                              c0.ClosePrice < ema21;

            if (isLong  && !bullCandle) return null;
            if (isShort && !bearCandle) return null;

            // Candle body sanity
            decimal body = Math.Abs(c0.ClosePrice - c0.OpenPrice);
            if (body < atr * 0.08m || body > atr * 2.0m) return null;

            // VWAP direction filter
            if (msi.Vwap > 0)
            {
                if (isLong  && c0.ClosePrice < msi.Vwap * 0.995m) return null;
                if (isShort && c0.ClosePrice > msi.Vwap * 1.005m) return null;
            }

            return BuildSignal(symbol, interval, klines, c0, atr, msi,
                isLong: isLong,
                reason: isLong ? "PA_STRUCTURE_LONG" : "PA_STRUCTURE_SHORT",
                confidence: CalcStructureConfidence(msi));
        }

        // ── B: Pattern Breakout ───────────────────────────────────────────

        static TradeSignal? TryPatternBreakout(
            string symbol, KlineInterval interval,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            BinanceFuturesUsdtKline c0, BinanceFuturesUsdtKline c1,
            decimal atr, MarketStructureInfo msi)
        {
            if (msi.Pattern == ChartPattern.None)        return null;
            if (msi.PatternConfidence < 65)              return null;
            if (msi.UpperChannel <= 0 || msi.LowerChannel <= 0) return null;

            bool breakDown = c0.ClosePrice < msi.LowerChannel - atr * 0.1m &&
                             c0.ClosePrice < c1.ClosePrice;
            bool breakUp   = c0.ClosePrice > msi.UpperChannel + atr * 0.1m &&
                             c0.ClosePrice > c1.ClosePrice;

            if (!breakDown && !breakUp) return null;

            // Breakout must have expanding body
            decimal body     = Math.Abs(c0.ClosePrice - c0.OpenPrice);
            decimal prevBody = Math.Abs(c1.ClosePrice - c1.OpenPrice);
            if (body < prevBody * 0.8m) return null;

            // Structure agrees with breakout
            bool structAgrees = breakDown
                ? msi.Structure != MarketStructure.BullishHHHL
                : msi.Structure != MarketStructure.BearishLHLL;
            if (!structAgrees) return null;

            return BuildSignal(symbol, interval, klines, c0, atr, msi,
                isLong: breakUp,
                reason: breakUp ? "PA_BREAKOUT_LONG" : "PA_BREAKOUT_SHORT",
                confidence: (decimal)msi.PatternConfidence / 100m);
        }

        // ── C: S/R Bounce ─────────────────────────────────────────────────

        static TradeSignal? TrySrBounce(
            string symbol, KlineInterval interval,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            BinanceFuturesUsdtKline c0, BinanceFuturesUsdtKline c1,
            decimal atr, MarketStructureInfo msi)
        {
            if (msi.SrLevels.Count == 0) return null;

            decimal price = c0.ClosePrice;
            decimal touch = atr * 0.4m;

            var hitLevel = msi.SrLevels
                .Where(l => Math.Abs(l.Price - price) <= touch && l.Strength >= 0.4)
                .OrderByDescending(l => l.Strength)
                .FirstOrDefault();
            if (hitLevel == null) return null;
            if (hitLevel.Touches < 2) return null;

            bool isLong  = !hitLevel.IsResistance; // at support → long
            bool isShort =  hitLevel.IsResistance; // at resistance → short

            // Rejection candle with dominant wick
            decimal body = Math.Abs(c0.ClosePrice - c0.OpenPrice);
            decimal wick = isLong
                ? c0.ClosePrice - c0.LowPrice
                : c0.HighPrice - c0.ClosePrice;
            bool rejection = wick > body * 1.2m && body > atr * 0.05m;
            if (!rejection) return null;

            // Candle direction must agree
            if (isLong  && c0.ClosePrice < c0.OpenPrice) return null;
            if (isShort && c0.ClosePrice > c0.OpenPrice) return null;

            return BuildSignal(symbol, interval, klines, c0, atr, msi,
                isLong: isLong,
                reason: isLong ? "PA_SR_BOUNCE_LONG" : "PA_SR_BOUNCE_SHORT",
                confidence: Math.Min(0.85m, (decimal)hitLevel.Strength + 0.20m));
        }

        // ── Signal builder ────────────────────────────────────────────────

        static TradeSignal? BuildSignal(
            string symbol, KlineInterval interval,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            BinanceFuturesUsdtKline c0,
            decimal atr, MarketStructureInfo msi,
            bool isLong, string reason, decimal confidence)
        {
            // Entry: close + small buffer
            decimal entry = isLong
                ? c0.ClosePrice + atr * 0.08m
                : c0.ClosePrice - atr * 0.08m;

            // Structural SL — behind nearest swing point
            decimal stopLoss = MarketStructureAnalyzer.CalcStructuralSl(
                entry, isLong, msi.Swings, atr);

            decimal slDist = Math.Abs(entry - stopLoss);

            // Expand SL if too tight
            if (slDist < atr * 1.0m)
            {
                stopLoss = isLong ? entry - atr * 1.0m : entry + atr * 1.0m;
                slDist   = atr * 1.0m;
            }
            // Skip if SL too wide
            if (slDist > atr * MaxSlAtrMult) return null;

            // R-Multiple TPs (1R / 2R / 3R)
            var (tp1, tp2, tp3) = MarketStructureAnalyzer.CalcRMultipleTps(
                entry, stopLoss, isLong,
                r1: 1.5m, r2: 2.5m, r3: 3.5m,
                srLevels: msi.SrLevels);

            // RR check
            decimal rr = Math.Abs(tp1 - entry) / slDist;
            if (rr < MinRR) return null;

            // Tags for logging/display
            string tags = "";
            if (msi.Pattern != ChartPattern.None && !string.IsNullOrEmpty(msi.PatternLabel))
                tags += $"_{msi.PatternLabel.Replace(" ", "_")}";
            if (msi.PatternConfidence >= 80)
                tags += $"_{msi.PatternConfidence}";
            if (msi.MomoScore >= 40)
                tags += $"_MOMO{(int)msi.MomoScore}";
            if (!string.IsNullOrEmpty(msi.StructLabel))
                tags += $"_{msi.StructLabel.Replace("/", "")}";

            decimal rratio = slDist > 0 ? Math.Abs(tp1 - entry) / slDist : 0m;

            return new TradeSignal
            {
                Symbol            = symbol,
                Side              = isLong ? SignalSide.Buy : SignalSide.Sell,
                Reason            = reason + tags,
                Atr               = atr,
                EntryPrice        = entry,
                StopLoss          = stopLoss,
                EntryRangeLow     = entry - atr * 0.10m,
                EntryRangeHigh    = entry + atr * 0.10m,
                TakeProfits       = new List<decimal> { tp1, tp2, tp3 },
                TakeProfit        = tp1,
                Confidence        = Math.Clamp(confidence, 0.30m, 0.95m),
                IsSuperSignal     = msi.PatternConfidence >= 90 && msi.MomoScore >= 60m,
                Time              = DateTime.UtcNow,
                Timeframe         = interval.ToString(),
                // PA enrichment fields
                MarketStructure   = msi.StructLabel,
                PatternLabel      = msi.PatternLabel,
                PatternConfidence = msi.PatternConfidence,
                PatternAction     = msi.PatternAction,
                MomoScore         = msi.MomoScore,
                VolumeRatio       = msi.VolumeRatio,
                VwapPosition      = msi.VwapPosition,
                NearestSupport    = msi.NearestSupport > 0 ? msi.NearestSupport : null,
                NearestResistance = msi.NearestResistance > 0 ? msi.NearestResistance : null,
                RRatio            = rratio,
            };
        }

        // ── Confidence ────────────────────────────────────────────────────

        static decimal CalcStructureConfidence(MarketStructureInfo msi)
        {
            decimal score = 0.50m;
            if (msi.Structure == MarketStructure.BullishHHHL ||
                msi.Structure == MarketStructure.BearishLHLL)  score += 0.10m;
            if (msi.MomoScore >= 70)       score += 0.12m;
            else if (msi.MomoScore >= 50)  score += 0.06m;
            if (msi.VolumeRatio >= 1.5m)   score += 0.08m;
            else if (msi.VolumeRatio >= 0.8m) score += 0.04m;
            if (!string.IsNullOrEmpty(msi.VwapPosition) && msi.VwapPosition != "AT")
                score += 0.05m;
            if (msi.PatternConfidence >= 80) score += 0.10m;
            else if (msi.PatternConfidence >= 60) score += 0.05m;
            return Math.Clamp(score, 0.35m, 0.90m);
        }

        // ── Helpers ───────────────────────────────────────────────────────

        static decimal CalcAtr(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period)
        {
            if (klines.Count < period + 1) return 0m;
            int n = klines.Count;
            decimal sum = 0m;
            for (int i = n - period; i < n; i++)
            {
                decimal prev = klines[i - 1].ClosePrice;
                decimal tr = Math.Max(
                    klines[i].HighPrice - klines[i].LowPrice,
                    Math.Max(Math.Abs(klines[i].HighPrice - prev),
                             Math.Abs(klines[i].LowPrice  - prev)));
                sum += tr;
            }
            return sum / period;
        }

        static decimal CalcEma(
            IReadOnlyList<BinanceFuturesUsdtKline> klines, int period, int endIndex)
        {
            if (endIndex < period) return klines[endIndex].ClosePrice;
            decimal k     = 2m / (period + 1);
            int     start = Math.Max(0, endIndex - period * 3);
            decimal ema   = klines[start].ClosePrice;
            for (int i = start + 1; i <= endIndex; i++)
                ema = klines[i].ClosePrice * k + ema * (1m - k);
            return ema;
        }
    }
}
