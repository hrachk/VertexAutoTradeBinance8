//  -----------------------------------------------------------------------------
//   STRATEGY ENGINE v5.1
//   - Dynamic RR filter (ATR + volatility + regime)
//   - Soft entry + liquidity + AI risk as before
//   - Имена и сигнатуры полностью совместимы с VertexAutoTradeBinance8
//  -----------------------------------------------------------------------------

using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
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
                start = 1;

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
            return tr >= atr * 2.2m;
        }

        private static bool IsTooSmallBody(BinanceFuturesUsdtKline current, decimal atr)
        {
            if (atr <= 0)
                return true;

            var body = Math.Abs(current.ClosePrice - current.OpenPrice);
            return body < atr * 0.08m;
        }

        // -------------------------------------------------------------------------------------
        // Нормализация entry/SL — чтобы не было entry == SL и QTY=0
        // -------------------------------------------------------------------------------------
        private static void NormalizeEntryAndSl(TradeSignal s)
        {
            if (s.EntryPrice <= 0 || s.StopLoss <= 0)
                return;

            decimal dist = Math.Abs(s.EntryPrice - s.StopLoss);
            decimal minDist;

            if (s.Atr.HasValue && s.Atr.Value > 0)
            {
                // минимум 0.3 ATR между entry и SL
                minDist = s.Atr.Value * 0.30m;
            }
            else
            {
                // fallback — 0.1% от цены
                minDist = s.EntryPrice * 0.001m;
            }

            if (dist >= minDist)
                return;

            if (s.Side == SignalSide.Buy)
            {
                s.StopLoss = s.EntryPrice - minDist;
            }
            else if (s.Side == SignalSide.Sell)
            {
                s.StopLoss = s.EntryPrice + minDist;
            }
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

                var s = new TradeSignal
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

                NormalizeEntryAndSl(s);
                return s;
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

                var s = new TradeSignal
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

                NormalizeEntryAndSl(s);
                return s;
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

                var s = new TradeSignal
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

                NormalizeEntryAndSl(s);
                return s;
            }

            bool bear = c.ClosePrice < c.OpenPrice &&
                        c.HighPrice >= ema &&
                        c.ClosePrice < ema;

            if (bear)
            {
                decimal entry = ema;
                decimal sl = c.HighPrice + atr * slMult;

                var s = new TradeSignal
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

                NormalizeEntryAndSl(s);
                return s;
            }

            return null;
        }

        // -------------------------------------------------------------------------------------
        // SOFT SAFE MODE: мягкий тестовый вход по тренду
        // -------------------------------------------------------------------------------------
        private TradeSignal? CreateSoftSafeSignal(
            string symbol,
            KlineInterval interval,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            SmartRegimeInfo smart)
        {
            if (klines == null || klines.Count < 20)
                return null;

            int last = klines.Count - 1;
            if (last < 1)
                return null;

            var c = klines[last];
            var prev = klines[last - 1];

            decimal atr = Atr(klines, 14, last);
            if (atr <= 0)
                return null;

            if (IsTooBigImpulseBar(c, prev, atr))
                return null;

            if (IsTooSmallBody(c, atr))
                return null;

            bool upTrend =
                smart.BaseRegime == MarketRegime.StrongUpTrend ||
                smart.SmartType == SmartRegimeType.SmartTrend ||
                smart.SmartType == SmartRegimeType.SmartStrongTrend ||
                smart.TrendSlopePercent > 0;

            bool downTrend =
                smart.BaseRegime == MarketRegime.StrongDownTrend ||
                smart.SmartType == SmartRegimeType.SmartTrend ||
                smart.SmartType == SmartRegimeType.SmartStrongTrend ||
                smart.TrendSlopePercent < 0;

            if (!upTrend && !downTrend)
                return null;

            var side = upTrend ? SignalSide.Buy : SignalSide.Sell;

            decimal entry = c.ClosePrice;
            decimal sl = upTrend
                ? entry - atr * 0.6m
                : entry + atr * 0.6m;

            decimal tp1 = upTrend
                ? entry + atr * 1.0m
                : entry - atr * 1.0m;

            decimal tp2 = upTrend
                ? entry + atr * 1.6m
                : entry - atr * 1.6m;

            var s = new TradeSignal
            {
                Symbol = symbol,
                Side = side,
                EntryPrice = entry,
                StopLoss = sl,
                Atr = atr,
                Time = c.CloseTime,
                Timeframe = interval.ToString(),
                Reason = "SOFT_TREND_PROBE",
                TakeProfits = new List<decimal> { tp1, tp2 },
                IsSuperSignal = false
            };

            NormalizeEntryAndSl(s);
            return s;
        }

        // -------------------------------------------------------------------------------------
        // ADAPTIVE SMART REGIME THRESHOLD (FIX 4.4)
        // -------------------------------------------------------------------------------------
        private static int GetAdaptiveThreshold(
            MarketRegime baseRegime,
            SmartRegimeType smartType,
            decimal volatility,
            decimal slope)
        {
            int threshold;

            bool isRangeLike =
                baseRegime == MarketRegime.Range ||
                smartType == SmartRegimeType.SmartRange ||
                smartType == SmartRegimeType.SmartSqueeze;

            bool isStrongTrendLike =
                baseRegime == MarketRegime.StrongUpTrend ||
                baseRegime == MarketRegime.StrongDownTrend ||
                smartType == SmartRegimeType.SmartStrongTrend;

            bool isTrendLike =
                baseRegime == MarketRegime.StrongUpTrend ||
                baseRegime == MarketRegime.StrongDownTrend ||
                smartType == SmartRegimeType.SmartTrend;

            if (isRangeLike)
                threshold = 35;
            else if (isStrongTrendLike)
                threshold = 60;
            else if (isTrendLike)
                threshold = 45;
            else
                threshold = 45;

            if (volatility < 0.10m)
                threshold -= 10;
            else if (volatility > 0.30m)
                threshold += 10;

            if (Math.Abs(slope) > 0.7m)
                threshold += 5;

            if (threshold < 25) threshold = 25;
            if (threshold > 80) threshold = 80;

            return threshold;
        }

        // -------------------------------------------------------------------------------------
        // FAST TREND OVERRIDE
        // -------------------------------------------------------------------------------------
        private static bool IsFastTrendOverride(SmartRegimeInfo smart)
        {
            bool strongTrend =
                smart.BaseRegime == MarketRegime.StrongUpTrend ||
                smart.BaseRegime == MarketRegime.StrongDownTrend;

            bool smartStrong =
                smart.SmartType == SmartRegimeType.SmartStrongTrend;

            bool slopeOk = Math.Abs(smart.TrendSlopePercent) >= 0.018m;
            bool volOk = smart.VolatilityPercent > 0m && smart.VolatilityPercent <= 0.010m;
            bool confOk = smart.Confidence >= 0.35m;

            return strongTrend && smartStrong && slopeOk && volOk && confOk;
        }

        // -------------------------------------------------------------------------------------
        // DYNAMIC RR FILTER (ATR + volatility + regime)
        // -------------------------------------------------------------------------------------
        private decimal GetDynamicMinRr(
            string symbol,
            KlineInterval interval,
            SmartRegimeInfo smart,
            TradeSignal signal)
        {
            // ATR в процентах от цены
            decimal atrPct = 0m;
            if (signal.Atr.HasValue && signal.Atr.Value > 0 && signal.EntryPrice > 0)
                atrPct = signal.Atr.Value / signal.EntryPrice;

            var regime = smart.BaseRegime;
            var smartType = smart.SmartType;
            var vol = smart.VolatilityPercent;
            var slope = smart.TrendSlopePercent;

            bool isSqueeze =
                regime == MarketRegime.Range ||
                smartType == SmartRegimeType.SmartSqueeze;

            bool isStrongTrendLike =
                regime == MarketRegime.StrongUpTrend ||
                regime == MarketRegime.StrongDownTrend ||
                smartType == SmartRegimeType.SmartStrongTrend;

            bool strongSlope = Math.Abs(slope) >= 0.02m;   // ≥ 2% наклон на TF
            bool highVol = vol >= 0.015m || atrPct >= 0.015m;   // > 1.5%
            bool lowVol = vol <= 0.005m || atrPct <= 0.005m;    // < 0.5%

            decimal minRr = 2.0m; // базовый

            if (isSqueeze)
            {
                // рынок в капкане → требуем максимальный запас по RR
                minRr = 2.5m;
            }
            else if (isStrongTrendLike && strongSlope)
            {
                // сильный тренд + норм/высокая волатильность → можно ослабить RR
                if (highVol)
                    minRr = 1.7m; // хай-вола: swing-амплитуда большая
                else
                    minRr = 1.8m;
            }
            else
            {
                // слабый / обычный тренд
                if (lowVol)
                    minRr = 2.2m; // рынок вязкий → требуем больше RR
                else
                    minRr = 2.0m;
            }

            // safety-коридор
            if (minRr < 1.4m) minRr = 1.4m;
            if (minRr > 2.6m) minRr = 2.6m;

            _logger.LogDebug(
                "[{Symbol}][{TF}] Dynamic RR: minRR={MinRR:F2}, regime={Regime}, smart={Smart}, slope={Slope:P2}, vol={Vol:P2}, atr%={AtrPct:P2}",
                symbol, interval, minRr, regime, smartType, slope, vol, atrPct);

            return minRr;
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

            // 1) Корреляция с BTC — с защитой
            if (!string.Equals(symbol, "BTCUSDT", StringComparison.OrdinalIgnoreCase))
            {
                decimal? corr = null;
                try
                {
                    corr = _correlationService.GetCorrelation("BTCUSDT", symbol);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "[DEBUG][{Symbol}][{TF}] CorrFilter ERROR → пропускаем фильтр корреляции",
                        symbol, interval);
                }

                if (corr.HasValue && Math.Abs(corr.Value) < 0.10m)
                {
                    _logger.LogInformation(
                        "[DEBUG][{Symbol}][{TF}] CorrFilter: {Corr:F2} < 0.10 → SKIP",
                        symbol, interval, corr.Value);
                    return null;
                }
            }

            // 2) Smart Regime — с защитой
            SmartRegimeInfo smart;
            try
            {
                smart = _smartRegimeService.Evaluate(symbol, interval, klines);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[DEBUG][{Symbol}][{TF}] SmartRegimeService.Evaluate ERROR → SKIP",
                    symbol, interval);
                return null;
            }

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

            int adaptiveThreshold = GetAdaptiveThreshold(
                smart.BaseRegime,
                smart.SmartType,
                smart.VolatilityPercent,
                smart.TrendSlopePercent);

            decimal adaptiveThresholdFrac = adaptiveThreshold / 100m;
            decimal safetyBuffer = 0.10m;

            bool fastTrendOverride = IsFastTrendOverride(smart);

            if (!fastTrendOverride && smart.Confidence < adaptiveThresholdFrac - safetyBuffer)
            {
                _logger.LogDebug(
                    "[{Symbol}][{TF}] AdaptiveRegime: confidence={Conf:P0} < threshold={Thr}% → SKIP",
                    symbol, interval, smart.Confidence, adaptiveThreshold);
                return null;
            }

            if (smart.IsDangerChopZone)
            {
                _logger.LogInformation(
                    "[DEBUG][{Symbol}][{TF}] SmartRegime=ChopZone (danger) → SKIP",
                    symbol, interval);
                return null;
            }

            bool softModeAllowed =
                (regime == MarketRegime.StrongUpTrend ||
                 regime == MarketRegime.StrongDownTrend ||
                 smart.SmartType == SmartRegimeType.SmartTrend ||
                 smart.SmartType == SmartRegimeType.SmartStrongTrend)
                && (smart.Confidence >= 0.40m || fastTrendOverride)
                && Math.Abs(smart.TrendSlopePercent) >= 0.0045m
                && smart.VolatilityPercent <= 0.40m
                && smart.TrendSlopePercent != 0;

            TradeSignal? baseSignal = null;

            bool isRangeLikeRegime =
                regime == MarketRegime.Range ||
                smart.SmartType == SmartRegimeType.SmartRange ||
                smart.SmartType == SmartRegimeType.SmartSqueeze;

            bool isStrongTrendLikeRegime =
                regime == MarketRegime.StrongUpTrend ||
                regime == MarketRegime.StrongDownTrend ||
                smart.SmartType == SmartRegimeType.SmartStrongTrend;

            // 3) Базовый сигнал по текущему режиму
            if (isRangeLikeRegime)
            {
                baseSignal = TryLiquidityGrab(symbol, interval, klines)
                             ?? TryPullbackEma21(symbol, interval, klines);
            }
            else if (isStrongTrendLikeRegime)
            {
                baseSignal = TryPullbackEma21(symbol, interval, klines);
            }
            else
            {
                baseSignal = TryPullbackEma21(symbol, interval, klines);
            }

            // 3.1) SOFT safe mode, если нет жёсткого сигнала
            if (baseSignal == null && softModeAllowed)
            {
                var soft = CreateSoftSafeSignal(symbol, interval, klines, smart);
                if (soft != null)
                {
                    _logger.LogInformation(
                        "[DEBUG][{Symbol}][{TF}] SOFT entry activated: side={Side} entry={Entry:F4} sl={SL:F4}",
                        symbol, interval, soft.Side, soft.EntryPrice, soft.StopLoss);
                    baseSignal = soft;
                }
            }

            if (baseSignal == null)
            {
                _logger.LogInformation("[DEBUG][{Symbol}][{TF}] GEN → No signal", symbol, interval);
                return null;
            }

            // 4) Pattern Filter — с защитой
            try
            {
                var pattern = _patternEngineService.Analyze(symbol, interval, klines);

                if (pattern != null && pattern.Score >= 0.30m)
                {
                    bool sameDir =
                        (pattern.Direction == 1 && baseSignal.Side == SignalSide.Buy) ||
                        (pattern.Direction == -1 && baseSignal.Side == SignalSide.Sell);

                    if (!sameDir && pattern.Score >= 0.60m)
                    {
                        _logger.LogInformation(
                            "[DEBUG][{Symbol}][{TF}] Pattern block: dir={Dir} score={Score:F2}",
                            symbol, interval, pattern.Direction, pattern.Score);
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[DEBUG][{Symbol}][{TF}] PatternEngine ERROR → игнорируем паттерны",
                    symbol, interval);
            }

            // 5) Liquidity Cluster Filter — с защитой
            try
            {
                baseSignal = _liquidityClusterService.FilterAndAdjust(baseSignal);
                if (baseSignal == null)
                    return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[DEBUG][{Symbol}][{TF}] LiquidityClusterService ERROR → используем базовый сигнал без корректировок",
                    symbol, interval);
                // оставляем baseSignal как есть
            }

            // 6) AI Dynamic Risk Tag — с защитой
            try
            {
                 
                var riskW = _aiLearning.GetAiRiskAdjustment(symbol, regime);
                baseSignal.Reason += $"|AIrisk={riskW:F2}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[DEBUG][{Symbol}][{TF}] AiSelfLearningService.GetDynamicRiskWeight ERROR → AIrisk=1.00",
                    symbol, interval);
                baseSignal.Reason += "|AIrisk=1.00";
            }

            // 7) DYNAMIC RR FILTER
            if (baseSignal.TakeProfits != null && baseSignal.TakeProfits.Count > 0)
            {
                decimal tp1 = baseSignal.TakeProfits[0];
                decimal slDist = Math.Abs(baseSignal.EntryPrice - baseSignal.StopLoss);
                decimal tpDist = Math.Abs(tp1 - baseSignal.EntryPrice);

                if (slDist <= 0 || tpDist <= 0)
                {
                    _logger.LogInformation(
                        "[DEBUG][{Symbol}][{TF}] RR filter: invalid distances slDist={SlDist:F6}, tpDist={TpDist:F6} → SKIP",
                        symbol, interval, slDist, tpDist);
                    return null;
                }

                decimal rr = tpDist / slDist;
                decimal minRr = GetDynamicMinRr(symbol, interval, smart, baseSignal);

                if (rr < minRr)
                {
                    _logger.LogInformation(
                        "[DEBUG][{Symbol}][{TF}] RR filter: RR {RR:F2} < minRR {MinRR:F2}: entry={Entry:F4}, sl={SL:F4}, tp1={TP1:F4} → SKIP",
                        symbol, interval, rr, minRr, baseSignal.EntryPrice, baseSignal.StopLoss, tp1);
                    return null;
                }
            }

            _logger.LogInformation(
                "[DEBUG][{Symbol}][{TF}] FINAL SIGNAL side={Side} entry={Entry:F2} sl={SL:F2} reason={Reason}",
                symbol, interval, baseSignal.Side, baseSignal.EntryPrice, baseSignal.StopLoss, baseSignal.Reason);

            return baseSignal;
        }

    }
}
