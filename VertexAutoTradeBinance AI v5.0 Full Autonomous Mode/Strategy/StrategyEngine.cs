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
using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.DecisionTrace;

namespace VertexAutoTradeBinance8.Strategy
{
    public sealed record FastFailResult(
    bool Allow,
    string Gate,
    string Reason)
    {
        public static FastFailResult Ok() =>
            new(true, "OK", "OK");

        public static FastFailResult Fail(string gate, string reason) =>
            new(false, gate, reason);
    }
    public class StrategyEngine
    {
        private readonly ILogger<StrategyEngine> _logger;
        private readonly AiCorrelationService _correlationService;
        private readonly AiLiquidityClusterService _liquidityClusterService;
        
        private readonly AiPatternEngineService _patternEngineService;
        private readonly AiSelfLearningService _aiLearning;
        private readonly SmartRegimeService _smartRegimeService;
         
        private readonly LiquidityGuardService _liquidityGuardService;
        private static readonly ConcurrentDictionary<(string symbol, SignalSide side), DateTime> _lastStopTime = new();

        private readonly EngineStateSnapshotService _stateSvc;

        public event Action<TradeSignal>? OnSignalGenerated;

        private readonly SignalConfidenceSettings _confidenceCfg;

        //fot UI
        public string CurrentMode { get; private set; } = "Detecting";
        public bool LastSoftEntry { get; private set; }
        public bool LastBlockedByLiquidity { get; private set; }

        private MarketDataFacade? _marketData;
        private readonly IDecisionTraceService _decisionTrace;

        // какие TF реагируют мгновенно
        private static readonly KlineInterval[] ReactiveTf =
        {
            KlineInterval.OneMinute,
            KlineInterval.FiveMinutes
        };

        // анти-дубль: symbol|tf -> last close
        private readonly ConcurrentDictionary<string, DateTime> _lastReactiveRun = new();

        private readonly TradingOptions _opt;
        private readonly TestModeOptions _test;

        public decimal? Confidence { get; set; } // 0..1


        public StrategyEngine(
            ILogger<StrategyEngine> logger,
            AiCorrelationService correlationService,
            AiLiquidityClusterService liquidityClusterService,
            
            AiPatternEngineService patternEngineService,
            AiSelfLearningService aiLearning,
            SmartRegimeService smartRegimeService,
            TradingOptions opt,
            TestModeOptions test,
            EngineStateSnapshotService stateSvc, IDecisionTraceService decisionTrace, LiquidityGuardService liquidityGuardService
            , SignalConfidenceSettings confidenceCfg)
        {
            _logger = logger;
            _correlationService = correlationService;
            _liquidityClusterService = liquidityClusterService;
            
            _patternEngineService = patternEngineService;
            _aiLearning = aiLearning;
            _smartRegimeService = smartRegimeService;
            _opt = opt;
            _test = test;
            _stateSvc = stateSvc;
            _decisionTrace = decisionTrace;
            _liquidityGuardService = liquidityGuardService;
            _confidenceCfg = confidenceCfg;

            _logger.LogWarning(
    "[CONFIG][STRATEGY] Trading TF={tf} | TestMode={enabled} Level={level}",
    _opt.TimeframeMinutes,
    _test.Enabled,
    _test.Level);
        }
        private EngineState _engineState => _stateSvc.State;

        public void BindReactive(MarketDataFacade marketData)
        {
            _marketData = marketData;

            // WS warm-up trigger
            marketData.OnWarm += (symbol, tf) =>
            {
                _logger.LogInformation(
                    "[STRAT][WARM] market warm confirmed {symbol} {tf}",
                    symbol, tf);
            };

            // Closed candle trigger
            marketData.WsClosedKline += (symbol, tf, candle) =>
            {
                if (ReactiveTf.Contains(tf))
                    RunReactive(symbol, tf, "CLOSE");
            };

            _logger.LogInformation("[STRAT][PUSH] Reactive entry-point bound");
        }

        private void RunReactive(string symbol, KlineInterval interval, string reason)
        {
            if (_marketData == null)
                return;

            var key = $"{symbol}:{interval}";
            var now = DateTime.UtcNow;

            // 🔥 SNAPSHOT OVERRIDE
            if (_marketData.HasSnapshotState)
            {
                _logger.LogInformation(
       "[STRAT][SNAPSHOT] warmup bypassed → snapshot state active");
                // разрешаем работу даже если WS ещё не warm
            }

            if (!_marketData.HasSnapshotState &&
         _marketData.IsInWarmup(symbol, interval))
            {
                _logger.LogDebug(
                    "[STRAT][PUSH][{symbol}][{tf}] skip — market warmup",
                    symbol, interval);
                return;
            }
             
            // ⏱ АНТИ-СПАМ:
            // CLOSE — всегда разрешаем
            if (reason != "CLOSE")
            {
                if (_lastReactiveRun.TryGetValue(key, out var last) &&
                    (now - last).TotalMilliseconds < 300)
                {
                    return;
                }
            }

            // фиксируем время запуска (и для CLOSE тоже)
            _lastReactiveRun[key] = now;

            try
            {
                var klines = _marketData
                    .GetKlinesAsync(symbol, interval, need: 120)
                    .GetAwaiter()
                    .GetResult();

                if (klines == null || klines.Count < 30)
                    return;

                _logger.LogDebug(
                    "[STRAT][PUSH][{symbol}][{tf}] run reason={reason} bars={bars}",
                    symbol, interval, reason, klines.Count);


                // 🔎 FAST-FAIL + TRACE DECISION (NEW LAYER)
                var decision = EvaluateSignal(symbol, interval, klines);
                // 🔥 TRACE — ВСЕГДА
                _decisionTrace.Record(new DecisionTraceSnapshot
                {
                    Symbol = symbol,
                    Timeframe = interval.ToString(),
                    Allow = decision.Allow,
                    FailedGate = decision.FailedGate?.Gate,
                    Reason = decision.FailedGate?.Reason,
                    Time = DateTime.UtcNow
                });


                if (!decision.Allow)
                {
                    var fail = decision.FailedGate;
                    if (fail != null)
                    {
                        _logger.LogInformation(
                            "[DECISION][{symbol}][{tf}] BLOCK gate={gate} reason={reason}",
                            symbol, interval, fail.Gate, fail.Reason);
                    }
                    return;
                }

                var signal = decision.Signal;

                if (signal != null)
                {
                    _logger.LogInformation(
                        "[STRAT][PUSH][{symbol}][{tf}] SIGNAL GENERATED → DISPATCH",
                        symbol, interval);

                    try
                    {
                        OnSignalGenerated?.Invoke(signal);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "[STRAT][PUSH][{symbol}][{tf}] OnSignalGenerated handler failed",
                            symbol, interval);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[STRAT][PUSH][{symbol}][{tf}] reactive error",
                    symbol, interval);
            }
        }

        // -------------------------------------------------------------------------------------
        // ATR/TP/SL настройки по таймфрейму
        // -------------------------------------------------------------------------------------
        private static (decimal slMult, decimal tp1Mult, decimal tp2Mult, decimal tp3Mult)
            GetAtrConfig(KlineInterval interval)
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
                    },

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

            //var side = upTrend ? SignalSide.Buy : SignalSide.Sell;
            SignalSide side;

            if (upTrend && !downTrend)
                side = SignalSide.Buy;
            else if (downTrend && !upTrend)
                side = SignalSide.Sell;
            else
                return null; // конфликт → NO SOFT SIGNAL

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

            // safety corridor
            minRr = Math.Clamp(minRr, 1.4m, 2.6m);
            _logger.LogDebug(
                $"[STRAT][{symbol}][{interval}] Dynamic RR итог: minRR={minRr:F2}, regime={regime}, smart={smartType}, slope={slope:P2}, vol={vol:P2}, atr%={atrPct:P2}");


           

            // short-bias ТОЛЬКО в downtrend
            if (signal.Side == SignalSide.Sell &&
                (smart.BaseRegime == MarketRegime.StrongDownTrend ||
                 smart.SmartType == SmartRegimeType.SmartStrongTrend))
            {
                minRr *= 0.9m;
            }

            //if (signal.Side == SignalSide.Sell &&
            //    (smart.BaseRegime == MarketRegime.StrongDownTrend ||
            //     smart.SmartType == SmartRegimeType.SmartStrongTrend))
            //{
            //    minRr *= 0.9m; // разрешаем быстрее фиксировать профит в даунтренде
            //}


            //if (signal.Side == SignalSide.Sell)
            //{
            //    minRr *= 0.9m; // −10% требование к RR для SHORT
            //}
            //        _logger.LogWarning(
            //"[DEBUG][SIDE-STATS] {symbol} side={side} regime={regime} slope={slope:P2}",
            //symbol, signal.Side, smart.BaseRegime, smart.TrendSlopePercent);


            //var w = _aiLearning.GetGateWeight(smart.BaseRegime, "RR");
            //minRr *= w;

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
            bool testMode = _test.Enabled;
            string level = _test.Level;

            bool allowSoftEntryAlways = testMode && _test.AllowSoftEntryAlways;
            bool relaxRr = testMode && _test.RelaxRR;
            bool relaxPatternBlock = testMode && _test.RelaxPatternBlock;
            bool relaxLiquidity = testMode && _test.RelaxLiquidity;
            bool ignoreCorrelation = testMode && _test.IgnoreCorrelation;
            bool lowerRegimeThreshold = testMode && _test.LowerRegimeThreshold;

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

            //// MICRO_SIGNAL — логируется для любого режима, если baseSignal появился
            //if (baseSignal != null)
            //{
            //    _aiLearning.RecordMarketStateTriggered(
            //        reason: "MICRO_SIGNAL",
            //        symbol: symbol,
            //        timeframe: interval.ToString(),
            //        regime: smart.BaseRegime,
            //        slope: smart.TrendSlopePercent,
            //        volatility: smart.VolatilityPercent,
            //        atr: baseSignal.Atr ?? 0,
            //        confidence: smart.Confidence
            //    );
            //}


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
                    _engineState.LastEntryDecision = "SOFT_ENTRY";
                    _engineState.SoftEntry = true;

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

            // =====================================================
            // ⏳ SIDE-AWARE COOLDOWN (PRO)
            // =====================================================
            if (_lastStopTime.TryGetValue((symbol, baseSignal.Side), out var lastStop))
            {
                var diff = DateTime.UtcNow - lastStop;

                // ⛔ Блокируем ТОЛЬКО тот же side
                if (diff < TimeSpan.FromMinutes(10))
                {
                    _logger.LogInformation(
                        $"⏳ COOLDOWN SAME-SIDE: {symbol} {baseSignal.Side} blocked ({diff.TotalMinutes:F1}m)");
                    return null;
                }
            }


            if (!relaxRr && baseSignal.TakeProfits != null && baseSignal.TakeProfits.Count > 0)
            {
                var slDist = Math.Abs(baseSignal.EntryPrice - baseSignal.StopLoss);
                var tpDist = Math.Abs(baseSignal.TakeProfits[0] - baseSignal.EntryPrice);

                if (slDist > 0)
                {
                    var rr = tpDist / slDist;
                    var minRr = GetDynamicMinRr(symbol, interval, smart, baseSignal);

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
                            $"🚫 RR_BLOCK: rr={rr:F2} < minRR={minRr:F2}");

                        return null;
                    }
                }
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

                    var w = _aiLearning.GetGateMultiplier(symbol, smart.BaseRegime, "PATTERN");
                    decimal blockScore = (relaxPatternBlock ? 0.85m : 0.60m) * w;
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
                var w = _aiLearning.GetGateWeight(smart.BaseRegime, "LIQ");

                if (!relaxLiquidity && beforeLiq != null && baseSignal == null && w >= 1.0m)
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

            if (baseSignal == null)
            {
                _aiLearning.RecordMarketStateTriggered(
                    reason: "NO_BASE_SIGNAL",
                    symbol: symbol,
                    timeframe: interval.ToString(),
                    regime: smart.BaseRegime,
                    slope: smart.TrendSlopePercent,
                    volatility: smart.VolatilityPercent,
                    atr: 0m,
                    confidence: smart.Confidence
                );

                _logger.LogInformation(
                    "[STRAT][{symbol}][{interval}] NO BASE SIGNAL — market neutral",
                    symbol, interval);

                LastSoftEntry = false;
                LastBlockedByLiquidity = false;
                CurrentMode = "Detecting";

                return null;
            }
            var riskW = 1.00m;

            try
            {
                riskW = _aiLearning.GetAiRiskAdjustment(symbol, regime);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[STRAT][{symbol}][{interval}] AI risk fallback",
                    symbol, interval);
            }

            baseSignal.Reason ??= string.Empty;
            baseSignal.Reason += $"|AIrisk={riskW:F2}";

            // 8) DYNAMIC RR FILTER (StrongUpTrend FIX)
            if (baseSignal.TakeProfits != null && baseSignal.TakeProfits.Count > 0)
            {
                bool isStrongTrend =
                    smart.BaseRegime == MarketRegime.StrongUpTrend ||
                    smart.BaseRegime == MarketRegime.StrongDownTrend;

                if (isStrongTrend)
                {
                    // ❗ В сильном тренде НЕ фиксируем TP
                    // Только первый TP как trigger для runner
                    baseSignal.TakeProfits = new List<decimal>
        {
            baseSignal.TakeProfits[0]
        };

                    baseSignal.Reason += "|STRONG_TREND_RUNNER";
                }
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

            //// =====================================================
            //// 🔐 EXPOSURE CONTROL (FINAL GATE BEFORE RETURN)
            //// =====================================================
            //try
            //{
            //    var exposure = CanIncreaseExposure(
            //       state: _engineState,              // существующий EngineState
            //        symbol: symbol,
            //        symbolNotionalUsd: 0m,             // ❗ StrategyEngine не знает — ставим 0
            //        equityUsd: 0m,                     // ❗ НЕ используется тут
            //        usedMarginUsd: 0m,                 // ❗ НЕ используется тут
            //        aiEdgeScore: smart.Confidence,     // ✔ корректный proxy
            //        isSpecialSetup:
            //            baseSignal.IsSuperSignal ||
            //            baseSignal.Reason.Contains("LIQUIDITY_GRAB") ||
            //            baseSignal.Reason.Contains("PULLBACK_EMA21"),
            //        isHighVolatility:
            //            smart.VolatilityPercent >= 0.015m,
            //        isLowEquityMode: false             // ❗ решается НИЖЕ по стеку
            //    );

            //    if (!exposure.AllowAdd)
            //    {
            //        _aiLearning.RecordMarketStateTriggered(
            //            reason: "EXPOSURE_BLOCK",
            //            symbol: symbol,
            //            timeframe: interval.ToString(),
            //            regime: smart.BaseRegime,
            //            slope: smart.TrendSlopePercent,
            //            volatility: smart.VolatilityPercent,
            //            atr: baseSignal.Atr ?? 0,
            //            confidence: smart.Confidence
            //        );

            //        _logger.LogWarning(
            //            $"⛔ EXPOSURE BLOCK: {exposure.Reason}");

            //        _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            //        return null;
            //    }


            //}
            //catch (Exception ex)
            //{
            //    _logger.LogError(ex, "[STRAT][EXPOSURE] Fatal error → signal blocked for safety");
            //    return null;
            //}


            return baseSignal;
        }


        private ExposureDecision CanIncreaseExposure(
    EngineState state,
    string symbol,
    decimal symbolNotionalUsd,   // текущий notional по символу (сумма long+short или чистая — как решишь)
    decimal equityUsd,
    decimal usedMarginUsd,
    decimal aiEdgeScore,         // 0..1
    bool isSpecialSetup,         // твой MomentumTradingPro: liquidity grab + retest + confirm
    bool isHighVolatility,       // по ATR/vol
    bool isLowEquityMode         // equity ниже порога -> “крутимся”
)
        {
            if (equityUsd <= 0m)
            {
                return new ExposureDecision
                {
                    AllowAdd = true,
                    UseProfitBucket = false,
                    AllowedAddUsd = 0m,
                    Reason = "ALLOW: exposure-skip (equity handled downstream)",
                    SymbolCapPct = 0m
                };
            }


            var sKey = EngineState.Key(symbol);
            var st = state.Symbols.GetOrAdd(sKey, _ => new SymbolState());

            // === daily bucket reset ===
            if (st.BucketDayUtc != DateTime.UtcNow.Date)
            {
                st.BucketDayUtc = DateTime.UtcNow.Date;
                st.RealizedPnlBucketUsd = 0m;
                st.HarvestsToday = 0;
                st.AddsToday = 0;
            }

            // === dynamic symbol cap ===
            // База: чем меньше капитал — тем шире разрешаем (крутиться), но не безумно.
            // Чем выше vol — тем меньше cap (риск).
            decimal baseCap = st.DefaultSymbolCapPct;

            if (isLowEquityMode)
                baseCap = Math.Min(0.35m, baseCap + 0.10m); // +10% cap для малого капитала, максимум 35%

            if (isHighVolatility)
                baseCap = Math.Max(0.10m, baseCap - 0.06m); // режем cap на высоком вол

            // временный буст от AI (если ты где-то выставишь st.CurrentSymbolCapPct и CapBoostUntilUtc)
            decimal cap = baseCap;
            if (st.CapBoostUntilUtc > DateTime.UtcNow)
                cap = Math.Max(cap, st.CurrentSymbolCapPct);

            // === global utilization cap (динамический) ===
            // тоже не жёстко: при малом капитале можно чуть выше, но не бесконечно.
            decimal maxUsedMarginPct = isLowEquityMode ? 0.70m : 0.55m;
            if (isHighVolatility) maxUsedMarginPct -= 0.08m;

            decimal usedPct = equityUsd <= 0 ? 1m : usedMarginUsd / equityUsd;
            if (usedPct >= maxUsedMarginPct)
            {
                return new ExposureDecision
                {
                    AllowAdd = false,
                    UseProfitBucket = false,
                    AllowedAddUsd = 0m,
                    Reason = $"BLOCK: usedMarginPct={usedPct:P0} >= {maxUsedMarginPct:P0}",
                    SymbolCapPct = cap
                };
            }

            // === symbol cap check ===
            decimal symbolPct = equityUsd <= 0 ? 1m : symbolNotionalUsd / equityUsd;
            bool capHit = symbolPct >= cap;

            // === "важно усреднить без профита" ===
            // Разрешаем ТОЛЬКО если special-setup И aiEdgeScore высокий.
            bool allowNoProfitAveraging = isSpecialSetup && aiEdgeScore >= 0.78m;

            // === profit bucket route ===
            // если cap не пробит — можно добавлять из bucket (и чуть-чуть без bucket только по special)
            decimal bucket = st.RealizedPnlBucketUsd;
            decimal reinvestRate = 0.55m; // 55% прибыли можно реинвестировать
            decimal fromBucketUsd = Math.Max(0m, bucket * reinvestRate);

            // === amount sizing (консервативно) ===
            // при capHit: добавлять можно только если allowNoProfitAveraging и то ограниченно
            decimal maxAddUsd;
            if (capHit)
            {
                if (!allowNoProfitAveraging)
                {
                    return new ExposureDecision
                    {
                        AllowAdd = false,
                        UseProfitBucket = false,
                        AllowedAddUsd = 0m,
                        Reason = $"BLOCK: symbolCapHit {symbolPct:P0} >= {cap:P0}",
                        SymbolCapPct = cap
                    };
                }

                // special add разрешаем маленьким шотом, чтобы “дожать” вход, а не залить депозит
                maxAddUsd = isHighVolatility ? equityUsd * 0.015m : equityUsd * 0.025m; // 1.5–2.5% equity
                return new ExposureDecision
                {
                    AllowAdd = true,
                    UseProfitBucket = false,
                    AllowedAddUsd = Math.Max(0m, maxAddUsd),
                    Reason = $"ALLOW: special-setup no-profit add (capHit) edge={aiEdgeScore:F2}",
                    SymbolCapPct = cap
                };
            }

            // cap не пробит
            // 1) если есть bucket — добавляем из bucket
            if (fromBucketUsd >= 5m)
            {
                maxAddUsd = Math.Min(fromBucketUsd, equityUsd * (isHighVolatility ? 0.02m : 0.04m));
                return new ExposureDecision
                {
                    AllowAdd = true,
                    UseProfitBucket = true,
                    AllowedAddUsd = Math.Max(0m, maxAddUsd),
                    Reason = $"ALLOW: add from profit bucket={bucket:F2} edge={aiEdgeScore:F2}",
                    SymbolCapPct = cap
                };
            }

            // 2) bucket нет — только special-setup
            if (allowNoProfitAveraging)
            {
                maxAddUsd = equityUsd * (isHighVolatility ? 0.015m : 0.03m);
                return new ExposureDecision
                {
                    AllowAdd = true,
                    UseProfitBucket = false,
                    AllowedAddUsd = Math.Max(0m, maxAddUsd),
                    Reason = $"ALLOW: special-setup no-profit add edge={aiEdgeScore:F2}",
                    SymbolCapPct = cap
                };
            }

            return new ExposureDecision
            {
                AllowAdd = false,
                UseProfitBucket = false,
                AllowedAddUsd = 0m,
                Reason = "BLOCK: no bucket and not special-setup",
                SymbolCapPct = cap
            };
        }

        private FastFailResult Gate0_Data(
        string symbol,
        KlineInterval tf,
        IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines == null)
                return FastFailResult.Fail("DATA", "klines=null");

            if (klines.Count < 30)
                return FastFailResult.Fail("DATA", $"bars={klines.Count}<30");

            return FastFailResult.Ok();
        }

        private FastFailResult Gate1_SmartRegime(
        string symbol,
        KlineInterval tf,
        IReadOnlyList<BinanceFuturesUsdtKline> klines,
        out SmartRegimeInfo smart)
        {
            smart = null!;

            try
            {
                smart = _smartRegimeService.Evaluate(symbol, tf, klines);
                return FastFailResult.Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FAST][SMART] Evaluate failed");
                return FastFailResult.Fail("SMART", "Evaluate error");
            }
        }
        private FastFailResult Gate2_Confidence(
      SmartRegimeInfo smart,
      bool lowerRegimeThreshold)
        {
            if (smart.IsDangerChopZone)
                return FastFailResult.Fail("CONF", "DangerChopZone");

            // === ADAPTIVE PART (ОСТАЁТСЯ)
            int adaptiveThreshold = GetAdaptiveThreshold(
                smart.BaseRegime,
                smart.SmartType,
                smart.VolatilityPercent,
                smart.TrendSlopePercent);

            decimal thrFrac = adaptiveThreshold / 100m;
            decimal safetyBuffer = 0.10m;

            if (lowerRegimeThreshold)
            {
                adaptiveThreshold = Math.Max(20, (int)(adaptiveThreshold * 0.8));
                thrFrac = adaptiveThreshold / 100m;
                safetyBuffer = 0.20m;
            }

            bool fastTrendOverride = IsFastTrendOverride(smart);

            // === ENGINE STATE (UI / TRACE)
            _engineState.LastEntryDecision = "CONF_CHECK";
            _engineState.ConfidenceRaw = smart.Confidence;
            _engineState.ConfidencePercent = (int)(smart.Confidence * 100);

            _engineState.ConfidenceLevel =
                smart.Confidence >= _confidenceCfg.Bands.HighFrom ? "HIGH" :
                smart.Confidence >= _confidenceCfg.MinEntry ? "MEDIUM" :
                                                                    "LOW";

            // ======================================================
            // 🔒 HARD FLOOR (PRODUCTION RULE)
            // ======================================================
            // НИКАКИХ входов < 46% — даже если adaptive разрешает
            if (smart.Confidence < _confidenceCfg.MinEntry)
            {
                return FastFailResult.Fail(
                    "CONF",
                    $"confidence={smart.Confidence:P0}<min={_confidenceCfg.MinEntry:P0}");
            }

            // ======================================================
            // ADAPTIVE CHECK (ТОЛЬКО ЕСЛИ ПРОШЛИ HARD FLOOR)
            // ======================================================
            if (!fastTrendOverride && smart.Confidence < thrFrac - safetyBuffer)
            {
                return FastFailResult.Fail(
                    "CONF",
                    $"confidence={smart.Confidence:P0}<thr={adaptiveThreshold}% (buf={safetyBuffer:P0})");
            }

            return FastFailResult.Ok();
        }




        private FastFailResult Gate3_BaseSignal(
        string symbol,
        KlineInterval tf,
        IReadOnlyList<BinanceFuturesUsdtKline> klines,
        SmartRegimeInfo smart,
        out TradeSignal? baseSignal)
        {
            baseSignal = null;

            bool rangeLike =
                smart.BaseRegime == MarketRegime.Range ||
                smart.SmartType == SmartRegimeType.SmartRange ||
                smart.SmartType == SmartRegimeType.SmartSqueeze;

            if (rangeLike)
                baseSignal = TryLiquidityGrab(symbol, tf, klines)
                          ?? TryPullbackEma21(symbol, tf, klines);
            else
                baseSignal = TryPullbackEma21(symbol, tf, klines);

            _engineState.LastEntryDecision = "WAITING_PULLBACK";

            if (baseSignal == null)
                return FastFailResult.Fail("BASE", "no base pattern");

            if (baseSignal != null)
            {
                _aiLearning.RecordMarketStateTriggered(
                    reason: "MICRO_SIGNAL",
                    symbol: symbol,
                    timeframe: tf.ToString(),
                    regime: smart.BaseRegime,
                    slope: smart.TrendSlopePercent,
                    volatility: smart.VolatilityPercent,
                    atr: baseSignal.Atr ?? 0,
                    confidence: smart.Confidence
                );
            }

            return FastFailResult.Ok();
        }

        private FastFailResult Gate4_RR(
        string symbol,
        KlineInterval tf,
        TradeSignal signal,
        SmartRegimeInfo smart,
        bool relaxRr)
        {
            if (relaxRr)
                return FastFailResult.Ok();

            if (signal.TakeProfits == null || signal.TakeProfits.Count == 0)
                return FastFailResult.Ok();

            var slDist = Math.Abs(signal.EntryPrice - signal.StopLoss);
            var tpDist = Math.Abs(signal.TakeProfits[0] - signal.EntryPrice);

            if (slDist <= 0)
                return FastFailResult.Fail("RR", "slDist<=0");

            var rr = tpDist / slDist;
            var minRr = GetDynamicMinRr(symbol, tf, smart, signal);

            // 🔥 AI Gate Weight (DecisionTrace)
            var w = _aiLearning.GetGateMultiplier(symbol, smart.BaseRegime, "RR");
            minRr *= w;

            // weight < 1 → gate слишком строгий → ослабляем
            // weight > 1 → gate слабый → усиливаем
            //  minRr *= rrGateWeight;

            if (rr < minRr)
                return FastFailResult.Fail(
                    "RR",
                    $"rr={rr:F2}<min={minRr:F2} (w={w:F2})");

            return FastFailResult.Ok();
        }

        private FastFailResult Gate5_Pattern(
        string symbol,
        KlineInterval tf,
        IReadOnlyList<BinanceFuturesUsdtKline> klines,
        TradeSignal signal,
        bool relaxPatternBlock)
        {
            try
            {
                var pattern = _patternEngineService.Analyze(symbol, tf, klines);
                if (pattern == null) return FastFailResult.Ok();

                bool sameDir =
                    (pattern.Direction == 1 && signal.Side == SignalSide.Buy) ||
                    (pattern.Direction == -1 && signal.Side == SignalSide.Sell);

                decimal thr = relaxPatternBlock ? 0.85m : 0.60m;

                if (!sameDir && pattern.Score >= thr)
                    return FastFailResult.Fail(
                        "PATTERN",
                        $"dir={pattern.Direction} score={pattern.Score:F2}");

                return FastFailResult.Ok();
            }
            catch
            {
                return FastFailResult.Ok(); // паттерны не критичны
            }
        }
       
        private FastFailResult Gate6_Liquidity(
        TradeSignal signal,
        SmartRegimeInfo smart,
        IReadOnlyList<BinanceFuturesUsdtKline> klines,
        KlineInterval tf,
        bool relaxLiquidity)
        {
            var w = _aiLearning.GetGateMultiplier(
    signal.Symbol,
    smart.BaseRegime,
    "LIQ");
            var lg = _liquidityGuardService.Analyze(
               symbol: signal.Symbol,
               interval: tf,
               klines: klines,
               side: signal.Side,
               superSignal: signal.IsSuperSignal);

            if (lg.Block && !relaxLiquidity)
            {
                _engineState.LastEntryDecision = "BLOCKED_LIQUIDITY";
                _engineState.BlockedByLiquidity = true;
                _engineState.LiquidityReason = lg.Reason.ToString();
                return FastFailResult.Fail("LIQ_GUARD", lg.Reason.ToString());
            }


            var after = _liquidityClusterService.FilterAndAdjust(signal);
            if (after != null)
                signal = after;


            if (after == null)
            {
                if (relaxLiquidity)
                    return FastFailResult.Ok();

                // gate-weight влияет ТОЛЬКО на пороги, не на случайность
                if (w >= 1.0m)
                    return FastFailResult.Fail("LIQ", "Liquidity block");

                return FastFailResult.Ok(); // ослабленный режим
            }
            return FastFailResult.Ok();

        }


        private FastFailResult Gate7_Exposure(
       string symbol,
       KlineInterval tf,
       TradeSignal signal,
       SmartRegimeInfo smart)
        {
            var w = _aiLearning.GetGateMultiplier(
                symbol,
                smart.BaseRegime,
                "EXPO");

            var es = _engineState;

            if (es == null || es.EquityUsd <= 0)
            {
                return FastFailResult.Ok(); // честный SKIP (нет аккаунт-состояния)
            }

            // ❗ symbolNotionalUsd здесь ОСОЗНАННО = 0
            // фактический notional проверяется ниже уровнем (Supervisor / Executor)

            var res = CanIncreaseExposure(
                state: es,
                symbol: symbol,
                symbolNotionalUsd: 0m,
                equityUsd: es.EquityUsd,
                usedMarginUsd: es.UsedMarginUsd,
                aiEdgeScore: smart.Confidence * w,
                isSpecialSetup: signal.IsSuperSignal,
                isHighVolatility: smart.VolatilityPercent >= 0.015m,
                isLowEquityMode: es.EquityUsd < 500m
            );

            if (!res.AllowAdd)
            {
                _aiLearning.RecordMarketStateTriggered(
                    reason: "EXPOSURE_BLOCK",
                    symbol: symbol,
                    timeframe: tf.ToString(),
                    regime: smart.BaseRegime,
                    slope: smart.TrendSlopePercent,
                    volatility: smart.VolatilityPercent,
                    atr: signal.Atr ?? 0,
                    confidence: smart.Confidence
                );

                return FastFailResult.Fail("EXPO", res.Reason);
            }

            return FastFailResult.Ok();
        } 

        internal sealed class SignalDecisionTrace
        {
            public bool Allow { get; set; }
            public TradeSignal? Signal { get; set; }

            public List<FastFailResult> Gates { get; } = new();

            public FastFailResult? FailedGate =>
                Gates.FirstOrDefault(g => !g.Allow);
        }

        internal SignalDecisionTrace EvaluateSignal(
     string symbol,
     KlineInterval interval,
     IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            var trace = new SignalDecisionTrace();


            // === ENGINE LIVE STATE RESET (PER DECISION) ===
            _engineState.LastDecisionTime = DateTime.UtcNow;
            _engineState.LastEntryDecision = "EVALUATING";
            _engineState.BlockedByLiquidity = false;
            _engineState.SoftEntry = false;


            // === CONFIG FLAGS (ЕДИНЫЙ ИСТОЧНИК — TestModeOptions)
            bool testMode = _test.Enabled;

            bool relaxRr = testMode && _test.RelaxRR;
            bool relaxPatternBlock = testMode && _test.RelaxPatternBlock;
            bool relaxLiquidity = testMode && _test.RelaxLiquidity;
            bool lowerRegimeThreshold = testMode && _test.LowerRegimeThreshold;

            // --- Gate 0: Data
            var r0 = Gate0_Data(symbol, interval, klines);
            trace.Gates.Add(r0);

            // --- Gate 1: SmartRegime (ВСЕГДА)
            SmartRegimeInfo smart;
            var r1 = Gate1_SmartRegime(symbol, interval, klines, out smart);
            trace.Gates.Add(r1);

            // --- Gate 2: Confidence
            var r2 = Gate2_Confidence(smart, lowerRegimeThreshold);
            trace.Gates.Add(r2);

            // --- Gate 3: Base Signal
            TradeSignal? baseSignal;
            var r3 = Gate3_BaseSignal(symbol, interval, klines, smart, out baseSignal);
            trace.Gates.Add(r3);

           
            if (baseSignal != null)
            {  // 🔥 CRITICAL: фиксируем confidence НА ВХОДЕ
                baseSignal.Confidence = smart.Confidence;
                trace.Gates.Add(Gate4_RR(symbol, interval, baseSignal, smart, relaxRr));
                trace.Gates.Add(Gate5_Pattern(symbol, interval, klines, baseSignal, relaxPatternBlock));
                trace.Gates.Add(Gate6_Liquidity(baseSignal, smart, klines, interval, relaxLiquidity));
                trace.Gates.Add(Gate7_Exposure(symbol, interval, baseSignal, smart));
            }

            // --- FINAL DECISION
            var failed = trace.FailedGate;
            trace.Allow = failed == null;
            trace.Signal = trace.Allow ? baseSignal : null;

            // 🔥 CRITICAL: DecisionTrace должен соответствовать РЕАЛЬНОМУ решению
            _aiLearning.RecordDecisionTrace(symbol, smart.BaseRegime, trace.Gates);

            if (trace.Allow)
            {
                _engineState.LastEntryDecision = "ENTER_ALLOWED";
                _engineState.ConfidenceRaw = smart.Confidence;
                _engineState.ConfidencePercent = (int)(smart.Confidence * 100);
                _engineState.ConfidenceLevel =
                    smart.Confidence >= 0.65m ? "HIGH" :
                    smart.Confidence >= 0.45m ? "MEDIUM" : "LOW";
            }

            return trace;
        }

        public static void RegisterStop(string symbol, SignalSide side)
        {
            _lastStopTime[(symbol, side)] = DateTime.UtcNow;

            // лёгкая очистка, чтобы dictionary не рос бесконечно (TTL 6 часов)
            var cutoff = DateTime.UtcNow.AddHours(-6);
            foreach (var kv in _lastStopTime)
            {
                if (kv.Value < cutoff)
                    _lastStopTime.TryRemove(kv.Key, out _);
            }
        }
    }
}
