//  -----------------------------------------------------------------------------
//   STRATEGY ENGINE v6.4
//   (QUANT-REALTIME MAX + HYBRID LOGGER + CONFIG TEST MODE + HUMAN LOG FORMAT)
//   - Dynamic RR filter (ATR + volatility + regime + AI TrendPredict)
//   - Soft entry + liquidity + AI risk как было
//   - HYBRID MarketState logging для AiSelfLearningService
//   - TestMode / Relax режимы читаются из TradingOptions (config.json)
//   - Новый формат логов: блоки, эмодзи, человекочитаемый вывод
//   - Имена и сигнатуры полностью совместимы с VertexAutoTradeBinance8
//  -----------------------------------------------------------------------------

using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Configuration;
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
        private readonly TradingOptions _opt;


        //fot UI
        public string CurrentMode { get; private set; } = "Detecting";
        public bool LastSoftEntry { get; private set; }
        public bool LastBlockedByLiquidity { get; private set; }




        public StrategyEngine(
            ILogger<StrategyEngine> logger,
            AiCorrelationService correlationService,
            AiLiquidityClusterService liquidityClusterService,
            AiMarketRegimeService marketRegimeService,
            AiPatternEngineService patternEngineService,
            AiSelfLearningService aiLearning,
            SmartRegimeService smartRegimeService,
            TradingOptions opt)
        {
            _logger = logger;
            _correlationService = correlationService;
            _liquidityClusterService = liquidityClusterService;
            _marketRegimeService = marketRegimeService;
            _patternEngineService = patternEngineService;
            _aiLearning = aiLearning;
            _smartRegimeService = smartRegimeService;
            _opt = opt;
        }

        // -------------------------------------------------------------------------------------
        // ATR/TP/SL настройки по таймфрейму
        // -------------------------------------------------------------------------------------
        private static (decimal slMult, decimal tp1Mult, decimal tp2Mult, decimal tp3Mult)
            GetAtrConfig(KlineInterval interval)
        {
            // RISK FIX: SL вне шума + TP согласован с RR-фильтром (TP1/SL ≥ ~2.0R)
            // Иначе после расширения SL все сигналы умирали на RR_BLOCK.
            return interval switch
            {
                // SL 1.3 → TP1 2.7 (RR≈2.08), TP2 3.5, TP3 4.5
                KlineInterval.OneMinute or KlineInterval.FiveMinutes
                    => (1.3m, 2.7m, 3.5m, 4.5m),
                // SL 1.5 → TP1 3.0 (RR=2.0)
                KlineInterval.FifteenMinutes
                    => (1.5m, 3.0m, 4.0m, 5.2m),
                // SL 2.0 → TP1 4.0 (RR=2.0)
                KlineInterval.OneHour or KlineInterval.FourHour
                    => (2.0m, 4.0m, 5.5m, 7.0m),
                KlineInterval.OneDay
                    => (2.5m, 5.0m, 7.0m, 9.0m),
                _ => (1.3m, 2.7m, 3.5m, 4.5m)
            };
        }

        // -------------------------------------------------------------------------------------
        // EMA helper
        // -------------------------------------------------------------------------------------
        private static decimal Ema(
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            int period,
            int index)
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
        private static decimal Atr(
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            int period,
            int lastIndex)
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
        private static decimal GetTrueRange(
            BinanceFuturesUsdtKline current,
            BinanceFuturesUsdtKline prev)
        {
            var tr1 = current.HighPrice - current.LowPrice;
            var tr2 = Math.Abs(current.HighPrice - prev.ClosePrice);
            var tr3 = Math.Abs(current.LowPrice - prev.ClosePrice);
            return Math.Max(tr1, Math.Max(tr2, tr3));
        }

        private static bool IsTooBigImpulseBar(
            BinanceFuturesUsdtKline current,
            BinanceFuturesUsdtKline prev,
            decimal atr)
        {
            if (atr <= 0)
                return false;

            var tr = GetTrueRange(current, prev);
            return tr >= atr * 2.2m;
        }

        private static bool IsTooSmallBody(
            BinanceFuturesUsdtKline current,
            decimal atr)
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
                // RISK FIX: минимум 0.8 ATR между entry и SL (было 0.3 — микро-стопы)
                minDist = s.Atr.Value * 0.80m;
            }
            else
            {
                // fallback — 0.25% от цены (было 0.1%)
                minDist = s.EntryPrice * 0.0025m;
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
            // Soft: SL 1.2 ATR (вне шума), TP1 2.5 ATR → RR ≈ 2.08 (проходит minRr)
            decimal sl = upTrend
                ? entry - atr * 1.2m
                : entry + atr * 1.2m;

            decimal tp1 = upTrend
                ? entry + atr * 2.5m
                : entry - atr * 2.5m;

            decimal tp2 = upTrend
                ? entry + atr * 3.5m
                : entry - atr * 3.5m;

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
        // DYNAMIC RR FILTER (ATR + volatility + regime + AI TrendPredict)
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

            bool strongSlope = Math.Abs(slope) >= 0.02m;          // ≥ 2% наклон на TF
            bool highVol = vol >= 0.015m || atrPct >= 0.015m;     // > 1.5%
            bool lowVol = vol <= 0.005m || atrPct <= 0.005m;      // < 0.5%

            // Согласовано с ATR-конфигом (цель ~2.0R на TP1). Не завышаем minRr впустую.
            decimal minRr = 1.85m; // базовый (было 2.0)

            if (isSqueeze)
            {
                minRr = 2.2m; // было 2.5 — squeeze всё ещё строже
            }
            else if (isStrongTrendLike && strongSlope)
            {
                if (highVol)
                    minRr = 1.60m; // было 1.7
                else
                    minRr = 1.70m; // было 1.8
            }
            else
            {
                if (lowVol)
                    minRr = 2.0m;  // было 2.2
                else
                    minRr = 1.85m;
            }

            // --- AI TREND PREDICTOR (QUANT-REALTIME MAX) --------------------
            AiSelfLearningService.AiTrendPrediction? trend = null;
            try
            {
                trend = _aiLearning.PredictTrend(symbol, regime, slope, vol);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    $"[STRAT][{symbol}][{interval}] PredictTrend ERROR → используем базовый dynamic RR.");
            }

            if (trend != null && trend.Direction != 0)
            {
                bool sameDir =
                    (trend.Direction > 0 && signal.Side == SignalSide.Buy) ||
                    (trend.Direction < 0 && signal.Side == SignalSide.Sell);

                if (sameDir)
                {
                    // тренд и сигнал совпадают → чуть смягчаем RR
                    minRr *= trend.RrBias;           // RrBias < 1.0
                }
                else
                {
                    // сигнал против AI-тренда → ужесточаем RR
                    var extra = (1.0m - trend.RrBias);
                    if (extra < 0) extra = 0;
                    minRr *= 1.0m + extra;           // +0…0.25
                }

                _logger.LogDebug(
                    $"[STRAT][{symbol}][{interval}] TrendPredict: dir={trend.Direction}, conf={trend.Confidence:P0}, rrBias={trend.RrBias:F2} → adjMinRR={minRr:F2}");
            }

            // safety-коридор
            if (minRr < 1.50m) minRr = 1.50m;
            if (minRr > 2.40m) minRr = 2.40m;

            _logger.LogDebug(
                $"[STRAT][{symbol}][{interval}] Dynamic RR итог: minRR={minRr:F2}, regime={regime}, smart={smartType}, slope={slope:P2}, vol={vol:P2}, atr%={atrPct:P2}");

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
            // Шапка блока
            _logger.LogInformation(
$@"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
📌 {symbol} [{interval}] — STRATEGY ENGINE
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

            // ==== CONFIG / TEST MODE FLAGS ==================================================
            bool testMode = _opt.Enabled;
            string level = _opt.Level ?? "off";

            bool allowSoftEntryAlways = testMode && _opt.AllowSoftEntryAlways;
            bool relaxRr = testMode && _opt.RelaxRR;
            bool relaxPatternBlock = testMode && _opt.RelaxPatternBlock;
            bool relaxLiquidity = testMode && _opt.RelaxLiquidity;
            bool ignoreCorrelation = testMode && _opt.IgnoreCorrelation;
            bool lowerRegimeThreshold = testMode && _opt.LowerRegimeThreshold;

            if (testMode)
            {
                _logger.LogInformation(
$@"🧪 TestMode включён (Level = {level})
   • AllowSoftEntryAlways : {allowSoftEntryAlways}
   • RelaxRR              : {relaxRr}
   • RelaxPatternBlock    : {relaxPatternBlock}
   • RelaxLiquidity       : {relaxLiquidity}
   • IgnoreCorrelation    : {ignoreCorrelation}
   • LowerRegimeThreshold : {lowerRegimeThreshold}");
            }

            // 0) Базовые проверки по данным
            if (klines == null)
            {
                _logger.LogError("❌ Ошибка: klines == null → пропускаем символ.");
                return null;
            }

            if (klines.Count == 0)
            {
                _logger.LogError("❌ Ошибка: klines.Count == 0 → пропускаем символ.");
                return null;
            }

            if (klines.Count < 30)
            {
                _logger.LogWarning($"⚠ Недостаточно баров: {klines.Count} < 30 → сигнал не ищем.");
                return null;
            }

            // 1) Корреляция с BTC — (можно отключить через config IgnoreCorrelation)
            if (!string.Equals(symbol, "BTCUSDT", StringComparison.OrdinalIgnoreCase))
            {
                decimal? corr = null;
                try
                {
                    corr = _correlationService.GetCorrelation("BTCUSDT", symbol);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        $"[STRAT][{symbol}][{interval}] Ошибка CorrFilter → фильтр корреляции временно пропускаем.");
                }

                if (!ignoreCorrelation && corr.HasValue && Math.Abs(corr.Value) < 0.10m)
                {
                    _logger.LogInformation(
                        $"🔒 CorrFilter: |corrBTC|={corr.Value:F2} < 0.10 → символ пропущен.");
                    _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                    return null;
                }

                if (ignoreCorrelation && corr.HasValue)
                {
                    _logger.LogInformation(
                        $"🧪 TestMode: CorrFilter отключён (corrBTC={corr.Value:F2}) → продолжаем.");
                }
            }

            // 2) Smart Regime — с защитой + базовый RecordMarketState (ВСЕГДА)
            SmartRegimeInfo smart;
            try
            {
                smart = _smartRegimeService.Evaluate(symbol, interval, klines);

                try
                {
                    var atr14 = Atr(klines, 14, klines.Count - 1);
                    _aiLearning.RecordMarketState(
                        symbol: symbol,
                        timeframe: interval.ToString(),
                        regime: smart.BaseRegime,
                        trendSlopePercent: smart.TrendSlopePercent,
                        volatilityPercent: smart.VolatilityPercent,
                        atr: atr14,
                        confidence: smart.Confidence
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        $"[STRAT][{symbol}][{interval}] BASE RecordMarketState ERROR.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    $"❌ SmartRegimeService.Evaluate ERROR → символ пропущен.");
                _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                return null;
            }

            var regime = smart.BaseRegime;

            // 2.1) HYBRID periodic snapshot (каждые ~60 сек) — независим от сделок
            try
            {
                var atr14 = Atr(klines, 14, klines.Count - 1);
                _aiLearning.TryHybridPeriodicSnapshot(
                    symbol: symbol,
                    timeframe: interval.ToString(),
                    regime: smart.BaseRegime,
                    slope: smart.TrendSlopePercent,
                    volatility: smart.VolatilityPercent,
                    atr: atr14,
                    confidence: smart.Confidence);
            }
            catch
            {
                // HYBRID snapshot errors не критичны
            }

            _logger.LogInformation(
$@"📊 Режим рынка:
   • Base Regime : {regime}
   • Smart Regime: {smart.SmartType}
   • Наклон      : {smart.TrendSlopePercent:P2}
   • Волатильн.  : {smart.VolatilityPercent:P2}
   • Доверие     : {smart.Confidence:P0}");

            // 2.2) Adaptive regime threshold + возможность ослабить его через config
            int adaptiveThreshold = GetAdaptiveThreshold(
                smart.BaseRegime,
                smart.SmartType,
                smart.VolatilityPercent,
                smart.TrendSlopePercent);

            decimal adaptiveThresholdFrac = adaptiveThreshold / 100m;
            decimal safetyBuffer = 0.10m;

            if (lowerRegimeThreshold)
            {
                // В тест-режиме даём системе больше шансов торговать
                adaptiveThreshold = Math.Max(20, (int)(adaptiveThreshold * 0.8));
                adaptiveThresholdFrac = adaptiveThreshold / 100m;
                safetyBuffer = 0.20m;

                _logger.LogInformation(
                    $"🧪 TestMode: порог confidence снижен → Thr={adaptiveThreshold}%  Buffer≈{safetyBuffer:P0}");
            }

            bool fastTrendOverride = IsFastTrendOverride(smart);

            if (!fastTrendOverride && smart.Confidence < adaptiveThresholdFrac - safetyBuffer)
            {
                _logger.LogInformation(
                    $"🚫 Confidence слишком низкий: {smart.Confidence:P0} < {adaptiveThreshold}% (после буфера) → сигнал не ищем.");
                _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                return null;
            }

            if (smart.IsDangerChopZone)
            {
                _logger.LogInformation("🚫 Зона хаоса (ChopZone/Danger) → сигнал отключён.");
                _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                return null;
            }

            // 3) SoftModeAllowed: можно насильно разрешить через config AllowSoftEntryAlways
            bool softModeAllowed =
                (regime == MarketRegime.StrongUpTrend ||
                 regime == MarketRegime.StrongDownTrend ||
                 smart.SmartType == SmartRegimeType.SmartTrend ||
                 smart.SmartType == SmartRegimeType.SmartStrongTrend)
                && (smart.Confidence >= 0.40m || fastTrendOverride)
                && Math.Abs(smart.TrendSlopePercent) >= 0.0045m
                && smart.VolatilityPercent <= 0.40m
                && smart.TrendSlopePercent != 0;

            if (allowSoftEntryAlways)
            {
                softModeAllowed = true;
                _logger.LogInformation(
                    "🧪 TestMode: AllowSoftEntryAlways=TRUE → мягкие входы по тренду разрешены всегда.");
            }

            TradeSignal? baseSignal = null;

            bool isRangeLikeRegime =
                regime == MarketRegime.Range ||
                smart.SmartType == SmartRegimeType.SmartRange ||
                smart.SmartType == SmartRegimeType.SmartSqueeze;

            bool isStrongTrendLikeRegime =
                regime == MarketRegime.StrongUpTrend ||
                regime == MarketRegime.StrongDownTrend ||
                smart.SmartType == SmartRegimeType.SmartStrongTrend;

            // 4) Базовый сигнал по текущему режиму
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

            // MICRO_SIGNAL — логируется для любого режима, если baseSignal появился
            if (baseSignal != null)
            {
                _aiLearning.RecordMarketStateTriggered(
                    reason: "MICRO_SIGNAL",
                    symbol: symbol,
                    timeframe: interval.ToString(),
                    regime: smart.BaseRegime,
                    slope: smart.TrendSlopePercent,
                    volatility: smart.VolatilityPercent,
                    atr: baseSignal.Atr ?? 0,
                    confidence: smart.Confidence
                );
            }


            // 4.1) SOFT safe mode, если нет жёсткого сигнала
            if (baseSignal == null && softModeAllowed)
            {
                var soft = CreateSoftSafeSignal(symbol, interval, klines, smart);
                if (soft != null)
                {
                    _aiLearning.RecordMarketStateTriggered(
                        reason: "SOFT_ENTRY",
                        symbol: symbol,
                        timeframe: interval.ToString(),
                        regime: smart.BaseRegime,
                        slope: smart.TrendSlopePercent,
                        volatility: smart.VolatilityPercent,
                        atr: soft.Atr ?? 0,
                        confidence: smart.Confidence
                    );



                    _logger.LogInformation(
                        $"🟡 SOFT-вход по тренду: side={soft.Side}, entry={soft.EntryPrice:F4}, SL={soft.StopLoss:F4}");
                    LastSoftEntry = true;
                    LastBlockedByLiquidity = false;
                    CurrentMode = "SoftTrend";
                    baseSignal = soft;
                }
            }

            if (baseSignal == null)
            {
                _logger.LogInformation("🔴 Итог: сигнала НЕТ (ни базового, ни soft).");
                _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

                LastSoftEntry = false;
                LastBlockedByLiquidity = false;
                CurrentMode = "Detecting";


                return null;
            }

            // 5) Pattern Filter — с защитой + RelaxPatternBlock вариант
            try
            {
                var pattern = _patternEngineService.Analyze(symbol, interval, klines);

                if (pattern != null && pattern.Score >= 0.30m)
                {
                    bool sameDir =
                        (pattern.Direction == 1 && baseSignal.Side == SignalSide.Buy) ||
                        (pattern.Direction == -1 && baseSignal.Side == SignalSide.Sell);

                    decimal blockScore = 0.60m;
                    if (relaxPatternBlock)
                    {
                        blockScore = 0.85m; // в тест-режиме блокируем только супер-сильные контр-сигналы
                    }

                    if (!sameDir && pattern.Score >= blockScore)
                    {
                        _aiLearning.RecordMarketStateTriggered(
                            reason: "AI_PATTERN_BLOCK",
                            symbol: symbol,
                            timeframe: interval.ToString(),
                            regime: smart.BaseRegime,
                            slope: smart.TrendSlopePercent,
                            volatility: smart.VolatilityPercent,
                            atr: baseSignal?.Atr ?? 0,
                            confidence: smart.Confidence
                        );

                        _logger.LogInformation(
                            $"🚫 Блок по AI-паттерну: dir={pattern.Direction}, score={pattern.Score:F2}, thr={blockScore:F2}.");
                        _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                        return null;
                    }

                    if (relaxPatternBlock && !sameDir && pattern.Score >= 0.60m && pattern.Score < blockScore)
                    {
                        _logger.LogInformation(
                            $"🧪 TestMode: паттерн против сигнала, но RelaxPatternBlock=TRUE → не блокируем (score={pattern.Score:F2}).");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    $"[STRAT][{symbol}][{interval}] PatternEngine ERROR → паттерны игнорируем.");
            }

            // 6) Liquidity Cluster Filter — с защитой + RelaxLiquidity
            try
            {
                var beforeLiq = baseSignal;
                baseSignal = _liquidityClusterService.FilterAndAdjust(beforeLiq);
                if (baseSignal == null)
                {
                    _aiLearning.RecordMarketStateTriggered(
                        reason: "LIQUIDITY_DANGER",
                        symbol: symbol,
                        timeframe: interval.ToString(),
                        regime: smart.BaseRegime,
                        slope: smart.TrendSlopePercent,
                        volatility: smart.VolatilityPercent,
                        atr: beforeLiq?.Atr ?? 0,
                        confidence: smart.Confidence
                    );

                    if (relaxLiquidity && beforeLiq != null)
                    {
                        _logger.LogInformation(
                            "🧪 TestMode: RelaxLiquidity=TRUE → игнорируем блок по ликвидности, берём базовый сигнал.");
                        baseSignal = beforeLiq;
                    }
                    else
                    {
                        _logger.LogInformation(
                            "🚫 Сигнал заблокирован по ликвидности (опасная стена/дисбаланс).");
                        _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                        LastSoftEntry = false;
                        LastBlockedByLiquidity = true;
                        CurrentMode = "LiquidityBlocked";

                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    $"[STRAT][{symbol}][{interval}] LiquidityClusterService ERROR → используем базовый сигнал без корректировок.");
                // оставляем baseSignal как есть
            }

            // 7) AI Dynamic Risk Tag — с защитой
            try
            {
                var riskW = _aiLearning.GetAiRiskAdjustment(symbol, regime);
                baseSignal.Reason += $"|AIrisk={riskW:F2}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    $"[STRAT][{symbol}][{interval}] AiSelfLearningService.GetAiRiskAdjustment ERROR → AIrisk=1.00.");
                baseSignal.Reason += "|AIrisk=1.00";
            }

            // 8) DYNAMIC RR FILTER + RelaxRR
            if (baseSignal.TakeProfits != null && baseSignal.TakeProfits.Count > 0)
            {
                decimal tp1 = baseSignal.TakeProfits[0];
                decimal slDist = Math.Abs(baseSignal.EntryPrice - baseSignal.StopLoss);
                decimal tpDist = Math.Abs(tp1 - baseSignal.EntryPrice);

                if (slDist <= 0 || tpDist <= 0)
                {
                    _logger.LogInformation(
                        $"🚫 RR filter: некорректные расстояния slDist={slDist:F6}, tpDist={tpDist:F6} → сигнал отброшен.");
                    _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                    return null;
                }

                decimal rr = tpDist / slDist;
                decimal minRr = GetDynamicMinRr(symbol, interval, smart, baseSignal);

                if (relaxRr)
                {
                    // Чуть опускаем минимальный RR в тест-режиме
                    var original = minRr;
                    minRr *= 0.80m;
                    if (minRr < 1.2m) minRr = 1.2m;

                    _logger.LogInformation(
                        $"🧪 TestMode: RelaxRR=TRUE → minRR {original:F2} → {minRr:F2}.");
                }

                if (rr < minRr)
                {
                    _aiLearning.RecordMarketStateTriggered(
                        reason: "RR_BLOCK",
                        symbol: symbol,
                        timeframe: interval.ToString(),
                        regime: smart.BaseRegime,
                        slope: smart.TrendSlopePercent,
                        volatility: smart.VolatilityPercent,
                        atr: baseSignal.Atr ?? 0,
                        confidence: smart.Confidence
                    );

                    _logger.LogInformation(
                        $"🚫 RR filter: RR={rr:F2} < minRR={minRr:F2} (entry={baseSignal.EntryPrice:F4}, SL={baseSignal.StopLoss:F4}, TP1={tp1:F4}) → сигнал отброшен.");
                    _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                    return null;
                }

                _logger.LogInformation(
                    $"✅ RR OK: RR={rr:F2} ≥ minRR={minRr:F2}.");
            }

            // Финальный красивый блок
            decimal? tp1F = baseSignal.TakeProfits != null && baseSignal.TakeProfits.Count > 0
                ? baseSignal.TakeProfits[0]
                : null;
            decimal? tp2F = baseSignal.TakeProfits != null && baseSignal.TakeProfits.Count > 1
                ? baseSignal.TakeProfits[1]
                : null;
            decimal? tp3F = baseSignal.TakeProfits != null && baseSignal.TakeProfits.Count > 2
                ? baseSignal.TakeProfits[2]
                : null;

            string dirEmoji = baseSignal.Side == SignalSide.Buy ? "🟢 LONG" : "🔴 SHORT";

            _logger.LogInformation(
$@"📌 Итоговый сигнал:
   • Направление : {dirEmoji}
   • Entry       : {baseSignal.EntryPrice:F4}
   • Stop Loss   : {baseSignal.StopLoss:F4}
   • TP1         : {(tp1F.HasValue ? tp1F.Value.ToString("F4") : "-")}
   • TP2         : {(tp2F.HasValue ? tp2F.Value.ToString("F4") : "-")}
   • TP3         : {(tp3F.HasValue ? tp3F.Value.ToString("F4") : "-")}
   • Reason      : {baseSignal.Reason}");

            _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

            return baseSignal;
        }
    }
}
