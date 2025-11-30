//  -----------------------------------------------------------------------------
//   STRATEGY ENGINE — PRO RANGE PATCH + LIQUIDITY GRAB + SMART BREAKOUT + FIX PACK
//   FIX 4.4: Adaptive SmartRegime Threshold (volatility + trend slope + regime).
//   Полностью совместимо с VertexAutoTradeBinance8.
//  -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.Strategy
{
    public class StrategyEngine
    {
        private readonly ILogger<StrategyEngine> _logger;
        private readonly AiCorrelationService _correlationService;
        private readonly AiLiquidityClusterService _liquidityClusterService;
        private readonly AiMarketRegimeService _marketRegimeService;
        private readonly AiPatternEngineService _patternEngineService;
        private readonly AiSelfLearningService _aiLearning;
        private readonly SmartRegimeService _smartRegimeService;

        public StrategyEngine(
            ILogger<StrategyEngine> logger,
            AiCorrelationService correlationService,
            AiLiquidityClusterService liquidityClusterService,
            AiMarketRegimeService marketRegimeService,
            AiPatternEngineService patternEngineService,
            AiSelfLearningService aiLearning,
            SmartRegimeService smartRegimeService)
        {
            _logger = logger;
            _correlationService = correlationService;
            _liquidityClusterService = liquidityClusterService;
            _marketRegimeService = marketRegimeService;
            _patternEngineService = patternEngineService;
            _aiLearning = aiLearning;
            _smartRegimeService = smartRegimeService;
        }

        // -------------------------------------------------------------------------
        // ATR/TP/SL настройки (без изменений)
        // -------------------------------------------------------------------------
        private static (decimal slMult, decimal tp1Mult, decimal tp2Mult, decimal tp3Mult) GetAtrConfig(KlineInterval interval)
        {
            return interval switch
            {
                KlineInterval.OneMinute or KlineInterval.FiveMinutes => (0.8m, 1.5m, 2.3m, 3.2m),
                KlineInterval.FifteenMinutes => (1.2m, 1.6m, 2.4m, 3.4m),
                KlineInterval.OneHour or KlineInterval.FourHour => (2.0m, 1.8m, 2.8m, 3.8m),
                KlineInterval.OneDay => (2.5m, 2.0m, 3.0m, 4.5m),
                _ => (1.0m, 1.5m, 2.3m, 3.2m)
            };
        }

        // -------------------------------------------------------------------------
        // EMA helper (без изменений)
        // -------------------------------------------------------------------------
        private static decimal Ema(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period, int index)
        {
            int start = Math.Max(0, index - period + 1);
            decimal k = 2m / (period + 1);

            decimal ema = klines[start].ClosePrice;
            for (int i = start + 1; i <= index; i++)
                ema = klines[i].ClosePrice * k + ema * (1 - k);

            return ema;
        }

        // -------------------------------------------------------------------------
        // ATR helper (без изменений)
        // -------------------------------------------------------------------------
        private static decimal Atr(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period, int lastIndex)
        {
            if (klines == null || klines.Count < period + 1) return 0;
            if (lastIndex <= 0 || lastIndex >= klines.Count) return 0;

            int start = lastIndex - period + 1;
            if (start <= 0) start = 1;

            decimal sumTr = 0;
            for (int i = start; i <= lastIndex; i++)
            {
                var k = klines[i];
                var prev = klines[i - 1];

                decimal tr1 = k.HighPrice - k.LowPrice;
                decimal tr2 = Math.Abs(k.HighPrice - prev.ClosePrice);
                decimal tr3 = Math.Abs(k.LowPrice - prev.ClosePrice);
                sumTr += Math.Max(tr1, Math.Max(tr2, tr3));
            }

            int bars = lastIndex - start + 1;
            return bars > 0 ? sumTr / bars : 0;
        }

        // -------------------------------------------------------------------------
        // Фильтры по свече (без изменений)
        // -------------------------------------------------------------------------
        private static decimal GetTrueRange(BinanceFuturesUsdtKline current, BinanceFuturesUsdtKline prev)
        {
            var tr1 = current.HighPrice - current.LowPrice;
            var tr2 = Math.Abs(current.HighPrice - prev.ClosePrice);
            var tr3 = Math.Abs(current.LowPrice - prev.ClosePrice);
            return Math.Max(tr1, Math.Max(tr2, tr3));
        }

        private static bool IsTooBigImpulseBar(BinanceFuturesUsdtKline current, BinanceFuturesUsdtKline prev, decimal atr)
        {
            if (atr <= 0) return false;
            return GetTrueRange(current, prev) >= atr * 1.8m;
        }

        private static bool IsTooSmallBody(BinanceFuturesUsdtKline current, decimal atr)
        {
            if (atr <= 0) return true;
            return Math.Abs(current.ClosePrice - current.OpenPrice) < atr * 0.15m;
        }

        // -------------------------------------------------------------------------
        // RANGE: LIQUIDITY GRAB (без изменений)
        // -------------------------------------------------------------------------
        private TradeSignal? TryLiquidityGrab(string symbol, KlineInterval interval, IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines == null || klines.Count < 30) return null;

            int last = klines.Count - 1;
            if (last < 1) return null;

            var c = klines[last];
            var prev = klines[last - 1];

            decimal atr = Atr(klines, 14, last);
            if (atr <= 0) return null;

            if (IsTooBigImpulseBar(c, prev, atr)) return null;
            if (IsTooSmallBody(c, atr)) return null;

            var (slMult, tp1Mult, tp2Mult, tp3Mult) = GetAtrConfig(interval);

            bool longGrab =
                c.LowPrice < prev.LowPrice &&
                c.ClosePrice > prev.LowPrice &&
                (prev.LowPrice - c.LowPrice) > atr * 0.4m &&
                c.ClosePrice > c.OpenPrice;

            if (longGrab)
            {
                decimal entry = prev.LowPrice + atr * 0.2m;
                decimal sl = c.LowPrice - atr * slMult;

                return new TradeSignal
                {
                    Symbol = symbol,
                    Side = SignalSide.Buy,
                    EntryPrice = entry,
                    StopLoss = sl,
                    Atr = atr,
                    TakeProfits = new()
                    {
                        entry + atr * tp1Mult,
                        entry + atr * tp2Mult,
                        entry + atr * tp3Mult
                    },
                    Time = c.CloseTime,
                    Timeframe = interval.ToString(),
                    Reason = "LIQUIDITY_GRAB_LONG",
                    IsSuperSignal = true
                };
            }

            bool shortGrab =
                c.HighPrice > prev.HighPrice &&
                c.ClosePrice < prev.HighPrice &&
                (c.HighPrice - prev.HighPrice) > atr * 0.4m &&
                c.ClosePrice < c.OpenPrice;

            if (shortGrab)
            {
                decimal entry = prev.HighPrice - atr * 0.2m;
                decimal sl = c.HighPrice + atr * slMult;

                return new TradeSignal
                {
                    Symbol = symbol,
                    Side = SignalSide.Sell,
                    EntryPrice = entry,
                    StopLoss = sl,
                    Atr = atr,
                    TakeProfits = new()
                    {
                        entry - atr * tp1Mult,
                        entry - atr * tp2Mult,
                        entry - atr * tp3Mult
                    },
                    Time = c.CloseTime,
                    Timeframe = interval.ToString(),
                    Reason = "LIQUIDITY_GRAB_SHORT",
                    IsSuperSignal = true
                };
            }

            return null;
        }

        // -------------------------------------------------------------------------
        // PULLBACK EMA21 (без изменений)
        // -------------------------------------------------------------------------
        private TradeSignal? TryPullbackEma21(string symbol, KlineInterval interval, IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines == null || klines.Count < 30) return null;

            int last = klines.Count - 1;
            if (last < 1) return null;

            var c = klines[last];
            var prev = klines[last - 1];

            decimal ema = Ema(klines, 21, last);
            decimal atr = Atr(klines, 14, last);
            if (atr <= 0) return null;

            if (IsTooBigImpulseBar(c, prev, atr)) return null;
            if (IsTooSmallBody(c, atr)) return null;

            var (slMult, tp1Mult, tp2Mult, tp3Mult) = GetAtrConfig(interval);

            bool bull =
                c.ClosePrice > c.OpenPrice &&
                c.LowPrice <= ema &&
                c.ClosePrice > ema;

            if (bull)
            {
                decimal entry = ema;
                decimal sl = c.LowPrice - atr * slMult;

                return new TradeSignal
                {
                    Symbol = symbol,
                    Side = SignalSide.Buy,
                    EntryPrice = entry,
                    StopLoss = sl,
                    Atr = atr,
                    Timeframe = interval.ToString(),
                    Time = c.CloseTime,
                    Reason = "PULLBACK_EMA21_LONG",
                    TakeProfits = new()
                    {
                        entry + atr * tp1Mult,
                        entry + atr * tp2Mult,
                        entry + atr * tp3Mult
                    }
                };
            }

            bool bear =
                c.ClosePrice < c.OpenPrice &&
                c.HighPrice >= ema &&
                c.ClosePrice < ema;

            if (bear)
            {
                decimal entry = ema;
                decimal sl = c.HighPrice + atr * slMult;

                return new TradeSignal
                {
                    Symbol = symbol,
                    Side = SignalSide.Sell,
                    EntryPrice = entry,
                    StopLoss = sl,
                    Atr = atr,
                    Timeframe = interval.ToString(),
                    Time = c.CloseTime,
                    Reason = "PULLBACK_EMA21_SHORT",
                    TakeProfits = new()
                    {
                        entry - atr * tp1Mult,
                        entry - atr * tp2Mult,
                        entry - atr * tp3Mult
                    }
                };
            }

            return null;
        }

        // -------------------------------------------------------------------------
        // MAIN SIGNAL GENERATOR (FIX 4.4 ADAPTIVE SMART REGIME)
        // -------------------------------------------------------------------------
        public TradeSignal? GenerateSignal(string symbol, KlineInterval interval, IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            _logger.LogInformation("\n[DEBUG][{Symbol}][{TF}] STRATEGY START", symbol, interval);

            if (klines == null)
            {
                _logger.LogError("[DEBUG][{Symbol}][{TF}] ERROR: klines == null → SKIP", symbol, interval);
                return null;
            }

            if (klines.Count == 0)
            {
                _logger.LogError("[DEBUG][{Symbol}][{TF}] ERROR: klines.Count == 0 → SKIP", symbol, interval);
                return null;
            }

            if (klines.Count < 30)
            {
                _logger.LogWarning("[DEBUG][{Symbol}][{TF}] TOO FEW BARS: {Count} < 30 → SKIP",
                    symbol, interval, klines.Count);
                return null;
            }

            // Корреляция с BTC (без изменений)
            if (!string.Equals(symbol, "BTCUSDT", StringComparison.OrdinalIgnoreCase))
            {
                var corr = _correlationService.GetCorrelation("BTCUSDT", symbol);
                if (corr is decimal c && Math.Abs(c) < 0.2m)
                {
                    _logger.LogInformation(
                        "[DEBUG][{Symbol}][{TF}] CorrFilter: {corr:F2} < 0.20 → SKIP",
                        symbol, interval, c);
                    return null;
                }
            }

            // === SMART REGIME EVALUATION ===
            var smart = _smartRegimeService.Evaluate(symbol, interval, klines);
            var regime = smart.BaseRegime;

            _logger.LogInformation(
                "[DEBUG][{Symbol}][{TF}] REGIME={Regime} smart={Smart} slope={Slope:P2} vol={Vol:P2} conf={Conf:P0}",
                symbol, interval, regime, smart.SmartType,
                smart.TrendSlopePercent, smart.VolatilityPercent, smart.Confidence);

            // === FIX 4.4 — ADAPTIVE THRESHOLD ===
            int GetAdaptiveThreshold(
                MarketRegime baseRegime,
                SmartRegimeType smartType,
                decimal vol,
                decimal slope)
            {
                int threshold = baseRegime switch
                {
                    MarketRegime.Range => 35,
                    MarketRegime.VolatileChop => 40,
                    MarketRegime.StrongUpTrend => 60,
                    MarketRegime.StrongDownTrend => 60,
                    MarketRegime.Unknown => 45,
                    _ => 45
                };



                // SmartRegime refinement
                if (smartType is SmartRegimeType.SmartRange or SmartRegimeType.SmartSqueeze)
                    threshold = Math.Min(threshold, 35);
                else if (smartType == SmartRegimeType.SmartStrongTrend)
                    threshold = Math.Max(threshold, 60);

                // volatility: 0.001 = 0.10%
                if (vol < 0.001m) threshold -= 10;
                else if (vol > 0.003m) threshold += 10;

                // slope: 0.007 = 0.70%
                if (Math.Abs(slope) > 0.007m)
                    threshold += 5;

                if (threshold < 25) threshold = 25;
                if (threshold > 80) threshold = 80;

                return threshold;
            }

            int adaptiveThreshold = GetAdaptiveThreshold(
                smart.BaseRegime,
                smart.SmartType,
                smart.VolatilityPercent,
                smart.TrendSlopePercent
            );

            if (smart.Confidence < adaptiveThreshold)
            {
                _logger.LogDebug(
                    "[{Symbol}] AdaptiveRegime: confidence={Conf}% < threshold={Thr}% → SKIP",
                    symbol, smart.Confidence, adaptiveThreshold);
                return null;
            }

            // ---------------------------------------------------------------------
            // SIGNAL GENERATION
            // ---------------------------------------------------------------------
            TradeSignal? baseSignal = null;

            bool isRangeLike =
                regime == MarketRegime.Range ||
                smart.SmartType == SmartRegimeType.SmartRange ||
                smart.SmartType == SmartRegimeType.SmartSqueeze;

            bool isStrongTrendLike =
                regime == MarketRegime.StrongUpTrend ||
                regime == MarketRegime.StrongDownTrend ||
                smart.SmartType == SmartRegimeType.SmartStrongTrend;

            if (isRangeLike)
            {
                baseSignal = TryLiquidityGrab(symbol, interval, klines)
                             ?? TryPullbackEma21(symbol, interval, klines);
            }
            else if (isStrongTrendLike)
            {
                baseSignal = TryPullbackEma21(symbol, interval, klines);
            }
            else
            {
                baseSignal = TryPullbackEma21(symbol, interval, klines);
            }

            if (baseSignal == null)
            {
                _logger.LogInformation("[DEBUG][{Symbol}][{TF}] GEN → NO SIGNAL", symbol, interval);
                return null;
            }

            // Pattern filter (без изменений)
            var pattern = _patternEngineService.Analyze(symbol, interval, klines);
            if (pattern != null)
            {
                if (pattern.Score >= 0.30m)
                {
                    bool matchLong = pattern.Direction == 1 && baseSignal.Side == SignalSide.Buy;
                    bool matchShort = pattern.Direction == -1 && baseSignal.Side == SignalSide.Sell;

                    if (!matchLong && !matchShort && pattern.Score >= 0.60m)
                    {
                        _logger.LogInformation(
                            "[DEBUG][{Symbol}][{TF}] Pattern block: dir={Dir} score={Score:F2}",
                            symbol, interval, pattern.Direction, pattern.Score);
                        return null;
                    }
                }
            }

            // Liquidity clusters (без изменений)
            baseSignal = _liquidityClusterService.FilterAndAdjust(baseSignal);
            if (baseSignal == null)
                return null;

            // AI learning risk
            var riskW = _aiLearning.GetDynamicRiskWeight(symbol, regime);
            baseSignal.Reason += $"|AIrisk={riskW:F2}";

            _logger.LogInformation(
                "[DEBUG][{Symbol}][{TF}] FINAL SIGNAL side={Side} entry={Entry:F2} sl={SL:F2} reason={Reason}",
                symbol, interval, baseSignal.Side, baseSignal.EntryPrice,
                baseSignal.StopLoss, baseSignal.Reason);

            return baseSignal;
        }
    }
}
