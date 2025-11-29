//  -----------------------------------------------------------------------------
//   STRATEGY ENGINE — PRO RANGE PATCH + LIQUIDITY GRAB + SMART BREAKOUT + FIX PACK
//   Пакет FIX: SmartRegime confidence, ATR-фильтры, импульсные свечи, TP/SL по TF.
//   Совместимо с VertexAutoTradeBinance8, имена классов/методов не меняются.
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

        // -------------------------------------------------------------------------------------
        // ATR/TP/SL настройки по таймфрейму
        // -------------------------------------------------------------------------------------
        private static (decimal slMult, decimal tp1Mult, decimal tp2Mult, decimal tp3Mult) GetAtrConfig(KlineInterval interval)
        {
            return interval switch
            {
                KlineInterval.OneMinute or KlineInterval.FiveMinutes
                    => (0.8m, 1.5m, 2.3m, 3.2m),   // M1/M5: SL ~0.8 ATR, TP растянуты
                KlineInterval.FifteenMinutes
                    => (1.2m, 1.6m, 2.4m, 3.4m),   // M15: SL дальше, TP пошире
                KlineInterval.OneHour or KlineInterval.FourHour
                    => (2.0m, 1.8m, 2.8m, 3.8m),   // H1/H4: трендовые ходы
                KlineInterval.OneDay
                    => (2.5m, 2.0m, 3.0m, 4.5m),   // D1: большие ходы
                _ => (1.0m, 1.5m, 2.3m, 3.2m)
            };
        }

        // -------------------------------------------------------------------------------------
        // EMA helper
        // -------------------------------------------------------------------------------------
        private static decimal Ema(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period, int index)
        {
            int start = Math.Max(0, index - period + 1);
            decimal k = 2m / (period + 1);

            decimal ema = klines[start].ClosePrice;
            for (int i = start + 1; i <= index; i++)
            {
                ema = klines[i].ClosePrice * k + ema * (1 - k);
            }
            return ema;
        }

        // -------------------------------------------------------------------------------------
        // ATR helper (с защитой индексов)
        // -------------------------------------------------------------------------------------
        private static decimal Atr(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period, int lastIndex)
        {
            if (klines == null || klines.Count < period + 1)
                return 0;

            if (lastIndex <= 0 || lastIndex >= klines.Count)
                return 0;

            int start = lastIndex - period + 1;
            if (start <= 0)
                start = 1; // чтобы prev = klines[i-1] не ушёл в -1

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
            if (bars <= 0)
                return 0;

            return sumTr / bars;
        }

        // -------------------------------------------------------------------------------------
        // Фильтры по свече: импульс/до́джи
        // -------------------------------------------------------------------------------------
        private static decimal GetTrueRange(BinanceFuturesUsdtKline current, BinanceFuturesUsdtKline prev)
        {
            var tr1 = current.HighPrice - current.LowPrice;
            var tr2 = Math.Abs(current.HighPrice - prev.ClosePrice);
            var tr3 = Math.Abs(current.LowPrice - prev.ClosePrice);
            return Math.Max(tr1, Math.Max(tr2, tr3));
        }

        private static bool IsTooBigImpulseBar(BinanceFuturesUsdtKline current, BinanceFuturesUsdtKline prev, decimal atr)
        {
            if (atr <= 0)
                return false;

            var tr = GetTrueRange(current, prev);
            return tr >= atr * 1.8m; // > 1.8 ATR — бешеный импульс, лучше не лезть
        }

        private static bool IsTooSmallBody(BinanceFuturesUsdtKline current, decimal atr)
        {
            if (atr <= 0)
                return true;

            var body = Math.Abs(current.ClosePrice - current.OpenPrice);
            return body < atr * 0.15m; // до́джи/шум — не входим
        }

        // -------------------------------------------------------------------------------------
        // RANGE: LIQUIDITY GRAB ENTRY DETECTION
        // -------------------------------------------------------------------------------------
        private TradeSignal? TryLiquidityGrab(
            string symbol,
            KlineInterval interval,
            IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines == null || klines.Count < 30)
                return null;

            int last = klines.Count - 1;
            if (last < 1)
                return null;

            var c = klines[last];
            var prev = klines[last - 1];

            decimal atr = Atr(klines, 14, last);
            if (atr <= 0)
                return null;

            // защита от бешеного импульса и микросвечей
            if (IsTooBigImpulseBar(c, prev, atr))
                return null;

            if (IsTooSmallBody(c, atr))
                return null;

            var (slMult, tp1Mult, tp2Mult, tp3Mult) = GetAtrConfig(interval);

            // Long Grab — сильный хвост вниз + возврат в диапазон
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
                    TakeProfits = new List<decimal>
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

            // Short Grab — хвост вверх + возврат под уровень
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
                    TakeProfits = new List<decimal>
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

        // -------------------------------------------------------------------------------------
        // PULLBACK EMA21
        // -------------------------------------------------------------------------------------
        private TradeSignal? TryPullbackEma21(
            string symbol,
            KlineInterval interval,
            IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines == null || klines.Count < 30)
                return null;

            int last = klines.Count - 1;
            if (last < 1)
                return null;

            var c = klines[last];
            var prev = klines[last - 1];

            decimal ema = Ema(klines, 21, last);
            decimal atr = Atr(klines, 14, last);
            if (atr <= 0)
                return null;

            // фильтр по свечам
            if (IsTooBigImpulseBar(c, prev, atr))
                return null;

            if (IsTooSmallBody(c, atr))
                return null;

            var (slMult, tp1Mult, tp2Mult, tp3Mult) = GetAtrConfig(interval);

            bool bull = c.ClosePrice > c.OpenPrice &&
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
                    TakeProfits = new List<decimal>
                    {
                        entry + atr * tp1Mult,
                        entry + atr * tp2Mult,
                        entry + atr * tp3Mult
                    }
                };
            }

            bool bear = c.ClosePrice < c.OpenPrice &&
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
                    TakeProfits = new List<decimal>
                    {
                        entry - atr * tp1Mult,
                        entry - atr * tp2Mult,
                        entry - atr * tp3Mult
                    }
                };
            }

            return null;
        }

        // -------------------------------------------------------------------------------------
        // MAIN SIGNAL GENERATOR
        // -------------------------------------------------------------------------------------
        public TradeSignal? GenerateSignal(
            string symbol,
            KlineInterval interval,
            IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            _logger.LogInformation("\n[DEBUG][{Symbol}][{TF}] STRATEGY START", symbol, interval);

            // Жёсткая защита от null / пустых данных
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
                _logger.LogWarning(
                    "[DEBUG][{Symbol}][{TF}] TOO FEW BARS: {Count} < 30 → SKIP",
                    symbol, interval, klines.Count);
                return null;
            }

            // 1) Корреляционный фильтр с BTCUSDT (как было)
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

            // 2) Smart Regime
            var smart = _smartRegimeService.Evaluate(symbol, interval, klines);
            var regime = smart.BaseRegime;

            _logger.LogInformation(
                "[DEBUG][{Symbol}][{TF}] REGIME={Regime} smart={Smart} slope={Slope:P2} vol={Vol:P2} conf={Conf:P0}",
                symbol,
                interval,
                regime,
                smart.SmartType,
                smart.TrendSlopePercent,
                smart.VolatilityPercent,
                smart.Confidence);

            // Если уверенность в режиме низкая — не входим
            if (smart.Confidence < 0.55m)
            {
                _logger.LogInformation(
                    "[DEBUG][{Symbol}][{TF}] SmartRegime confidence {Conf:P0} < 55% → SKIP",
                    symbol, interval, smart.Confidence);
                return null;
            }

            // При опасной «рубке» рынка — просто не лезем
            if (smart.IsDangerChopZone && smart.Confidence >= 0.4m)
            {
                _logger.LogInformation(
                    "[DEBUG][{Symbol}][{TF}] SmartRegime=ChopZone (danger) → SKIP",
                    symbol, interval);
                return null;
            }

            TradeSignal? baseSignal = null;

            // 3) Логика входа с учётом SmartRegime
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
                // В диапазоне первым делом ищем захват ликвидности
                baseSignal = TryLiquidityGrab(symbol, interval, klines)
                             ?? TryPullbackEma21(symbol, interval, klines);
            }
            else if (isStrongTrendLike)
            {
                // Сильный тренд — Pullback EMA21
                baseSignal = TryPullbackEma21(symbol, interval, klines);
            }
            else
            {
                // Остальные режимы — дефолтная логика по EMA21
                baseSignal = TryPullbackEma21(symbol, interval, klines);
            }

            if (baseSignal == null)
            {
                _logger.LogInformation("[DEBUG][{Symbol}][{TF}] GEN → No signal", symbol, interval);
                return null;
            }

            // 4) Pattern Filter — безопасный, без NRE
            var pattern = _patternEngineService.Analyze(symbol, interval, klines);

            if (pattern != null)
            {
                if (pattern.Score >= 0.30m)
                {
                    if (pattern.Direction == 1 && baseSignal.Side == SignalSide.Buy)
                    {
                        // паттерн за LONG — ок
                    }
                    else if (pattern.Direction == -1 && baseSignal.Side == SignalSide.Sell)
                    {
                        // паттерн за SHORT — ок
                    }
                    else if (pattern.Score >= 0.60m)
                    {
                        // сильный паттерн против сигнала — баним
                        _logger.LogInformation(
                            "[DEBUG][{Symbol}][{TF}] Pattern block: dir={Dir} score={Score:F2}",
                            symbol, interval, pattern.Direction, pattern.Score);
                        return null;
                    }
                }
            }

            // 5) Liquidity Cluster Filter & Adjustments
            baseSignal = _liquidityClusterService.FilterAndAdjust(baseSignal);
            if (baseSignal == null)
                return null;

            // 6) AI Dynamic Risk Tag
            var riskW = _aiLearning.GetDynamicRiskWeight(symbol, regime);
            baseSignal.Reason += $"|AIrisk={riskW:F2}";

            _logger.LogInformation(
                "[DEBUG][{Symbol}][{TF}] FINAL SIGNAL side={Side} entry={Entry:F2} sl={SL:F2} reason={Reason}",
                symbol, interval, baseSignal.Side, baseSignal.EntryPrice, baseSignal.StopLoss, baseSignal.Reason);

            return baseSignal;
        }
    }
}
