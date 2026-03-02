//  -----------------------------------------------------------------------------
// Движок стратегий v6.5 ФИНАЛЬНАЯ (ПРОИЗВОДСТВЕННАЯ)
// - Реактивный асинхронный конвейер (БЕЗ .GetResult / БЕЗ взаимоблокировок)
// - Однократная обработка каждого ключа с очисткой TTL
// - Детерминированный конвейер Gate + согласованность DecisionTrace
// - Совместимые сигнатуры/события для VertexAutoTradeBinance8//  -----------------------------------------------------------------------------

using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using System.Collections.Concurrent;
using System.Diagnostics;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Models.DTO.Debug;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.DecisionTrace;
using VertexAutoTradeBinance8.Services.DTO.Debug;
using VertexAutoTradeBinance8.Strategy.Confidence;

namespace VertexAutoTradeBinance8.Strategy
{
    public sealed record FastFailResult(bool Allow, string Gate, string Reason)
    {
        public static FastFailResult Ok() => new(true, "OK", "OK");
        public static FastFailResult Fail(string gate, string reason) => new(false, gate, reason);
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
        private readonly EngineStateSnapshotService _stateSvc;
        private readonly IDecisionTraceService _decisionTrace;
        private readonly TradingOptions _opt;
        private readonly TestModeOptions _test;
        private readonly ConfidenceResolver _confidenceCfg;

        // UI flags
        public string CurrentMode { get; private set; } = "Detecting";
        public bool LastSoftEntry { get; private set; }
        public bool LastBlockedByLiquidity { get; private set; }
        public decimal? Confidence { get; set; } // 0..1

        public event Action<TradeSignal>? OnSignalGenerated;

        private EngineState _engineState => _stateSvc.State;

        private MarketDataFacade? _marketData;
        private readonly DecisionMarkerSink _decisionMarkers;

        // какие TF реагируют мгновенно
        private static readonly KlineInterval[] ReactiveTf =
        {
            KlineInterval.OneMinute,
            KlineInterval.FiveMinutes,
            KlineInterval.FifteenMinutes
        };

        // анти-спам: symbol|tf -> last run time
        private readonly ConcurrentDictionary<string, DateTime> _lastReactiveRun = new();

        // side-aware cooldown after stop
        private static readonly ConcurrentDictionary<(string symbol, SignalSide side), DateTime> _lastStopTime = new();

        // per-key singleflight locks + TTL cleanup
        private readonly ConcurrentDictionary<string, LockEntry> _reactiveLocks = new();
        private long _lockCleanupTick; // interlocked tick

        private sealed class LockEntry
        {
            public readonly SemaphoreSlim Gate = new(1, 1);
            public DateTime LastUsedUtc;
            public bool Pending;
        }

        private readonly SignalConfidenceAggregator _confidenceAgg;
        private readonly ConcurrentDictionary<string, DateTime> _lastRealtimeEval = new();

        private readonly ConcurrentDictionary<string, DateTime> _lastSignalUtc = new();
        private static readonly TimeSpan SignalCooldown = TimeSpan.FromSeconds(10);


        public StrategyEngine(
            ILogger<StrategyEngine> logger,
            AiCorrelationService correlationService,
            AiLiquidityClusterService liquidityClusterService,
            AiPatternEngineService patternEngineService,
            AiSelfLearningService aiLearning,
            SmartRegimeService smartRegimeService,
            TradingOptions opt,
            TestModeOptions test,
            EngineStateSnapshotService stateSvc,
            IDecisionTraceService decisionTrace,
            LiquidityGuardService liquidityGuardService,
            ConfidenceResolver confidenceCfg, DecisionMarkerSink decisionMarkers)
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
            _decisionMarkers = decisionMarkers;

            _logger.LogWarning(
                "[CONFIG][STRATEGY] Trading TF={tf} | TestMode={enabled} Level={level}",
                _opt.TimeframeMinutes,
                _test.Enabled,
                _test.Level);

            _confidenceAgg = new SignalConfidenceAggregator(_smartRegimeService);

        }
        private bool ShouldRunRealtime(string symbol)
        {
            var now = DateTime.UtcNow;

            if (_lastRealtimeEval.TryGetValue(symbol, out var last))
            {
                if ((now - last).TotalMilliseconds < 250)
                    return false;
            }

            _lastRealtimeEval[symbol] = now;
            return true;
        }

        public enum TrendPhase
        {
            Unknown = 0,
            Impulse,
            Continuation,
            Exhaustion,
            Distribution,
            Decay
        }
        private static TrendPhase DetectTrendPhase(
        IReadOnlyList<BinanceFuturesUsdtKline> klines,
        decimal atr,
        decimal slope)
        {
            int last = klines.Count - 1;
            if (last < 60 || atr <= 0) return TrendPhase.Unknown;

            // ---- 1) Был ли импульс недавно? ----
            bool hadImpulse = false;
            int impStart = Math.Max(1, last - 18);
            for (int i = impStart; i < last - 3; i++)
            {
                var body = Math.Abs(klines[i].ClosePrice - klines[i].OpenPrice);
                if (body >= atr * 1.1m)
                {
                    hadImpulse = true;
                    break;
                }
            }
            if (!hadImpulse) return TrendPhase.Unknown;

            // ---- 2) Есть ли "climax" свеча? (широкий range + high volume) ----
            var c = klines[last];
            var p = klines[last - 1];
            var rangeNow = c.HighPrice - c.LowPrice;

            decimal avgRange10 = 0m;
            decimal avgVol10 = 0m;
            int r0 = Math.Max(1, last - 10);
            int n = 0;
            for (int i = r0; i < last; i++)
            {
                avgRange10 += (klines[i].HighPrice - klines[i].LowPrice);
                avgVol10 += klines[i].Volume;
                n++;
            }
            avgRange10 = n > 0 ? avgRange10 / n : rangeNow;
            avgVol10 = n > 0 ? avgVol10 / n : c.Volume;

            bool climax = rangeNow >= avgRange10 * 1.8m && c.Volume >= avgVol10 * 1.8m;

            // ---- 3) Компрессия после импульса? ----
            decimal recentRange5 = 0m;
            for (int i = last - 5; i < last; i++)
                recentRange5 += (klines[i].HighPrice - klines[i].LowPrice);
            recentRange5 /= 5m;

            bool compression = recentRange5 <= atr * 0.65m;

            // ---- 4) Потеря продолжения: нет новых экстремумов ----
            decimal hi6 = klines[last - 6].HighPrice;
            decimal lo6 = klines[last - 6].LowPrice;
            for (int i = last - 6; i <= last; i++)
            {
                hi6 = Math.Max(hi6, klines[i].HighPrice);
                lo6 = Math.Min(lo6, klines[i].LowPrice);
            }
            bool noContinuationUp = c.HighPrice < hi6 && c.ClosePrice < hi6;
            bool noContinuationDown = c.LowPrice > lo6 && c.ClosePrice > lo6;

            // ---- 5) Слишком далеко от EMA21 (только как усилитель, НЕ как причина) ----
            decimal ema21 = Ema(klines, 21, last);
            decimal distEmaAtr = Math.Abs(c.ClosePrice - ema21) / atr;

            // ---- Решение ----
            // Distribution = импульс был + (компрессия или climax) + потеря продолжения
            if ((compression || climax) && distEmaAtr >= 1.0m)
            {
                if (slope > 0m && noContinuationUp) return TrendPhase.Distribution;
                if (slope < 0m && noContinuationDown) return TrendPhase.Distribution;
            }

            // Exhaustion = импульс был + slope почти умер + далеко от EMA
            if (Math.Abs(slope) < 0.0038m && distEmaAtr >= 1.1m)
                return TrendPhase.Exhaustion;

            // Continuation = slope живой и нет признаков distribution
            if (Math.Abs(slope) >= 0.0040m)
                return TrendPhase.Continuation;

            return TrendPhase.Unknown;
        }


        private static bool IsConfirmedReversal(
       IReadOnlyList<BinanceFuturesUsdtKline> klines,
       int last,
       decimal atr,
       SmartRegimeInfo smart,
       SignalSide side)
        {
            if (klines == null || last < 30 || atr <= 0m) return false;

            var c = klines[last];
            var p = klines[last - 1];

            // 0) anti-spike
            if (IsTooBigImpulseBar(c, p, atr)) return false;

            // 1) candle must show intent
            decimal body = Math.Abs(c.ClosePrice - c.OpenPrice);
            if (body < atr * 0.55m) return false;

            bool bull = c.ClosePrice > c.OpenPrice;
            bool bear = c.ClosePrice < c.OpenPrice;

            if (side == SignalSide.Buy && !bull) return false;
            if (side == SignalSide.Sell && !bear) return false;

            // 2) EMA21 cross confirmation (close on the other side)
            decimal ema21 = Ema(klines, 21, last);

            if (side == SignalSide.Buy)
            {
                if (!(c.ClosePrice > ema21 && p.ClosePrice <= ema21))
                    return false;
            }
            else
            {
                if (!(c.ClosePrice < ema21 && p.ClosePrice >= ema21))
                    return false;
            }

            // 3) structure break vs recent swing (BOS)
            int lb = 10;
            int s = Math.Max(1, last - lb);

            decimal swingHi = klines[s].HighPrice;
            decimal swingLo = klines[s].LowPrice;

            for (int i = s; i < last; i++)
            {
                swingHi = Math.Max(swingHi, klines[i].HighPrice);
                swingLo = Math.Min(swingLo, klines[i].LowPrice);
            }

            if (side == SignalSide.Buy)
            {
                // break above recent swing high by small buffer
                if (c.ClosePrice < swingHi + atr * 0.10m) return false;
            }
            else
            {
                // break below recent swing low by small buffer
                if (c.ClosePrice > swingLo - atr * 0.10m) return false;
            }

            // 4) optional: regime confidence must not be trash
            // (не делаем жестко, только фильтр от мусора)
            if (smart.Confidence < 0.35m) return false;

            return true;
        }
        public void BindReactive(MarketDataFacade marketData)
        {
            _marketData = marketData;

            marketData.OnWarm += (symbol, tf) =>
            {
                _logger.LogInformation(
                    "[STRAT][WARM] market warm confirmed {symbol} {tf}",
                    symbol, tf);
            };

            marketData.WsClosedKline += (symbol, tf, candle) =>
            {
                if (ReactiveTf.Contains(tf))
                    _ = RunReactive(symbol, tf, "CLOSE");
            };

            // 🔥 REALTIME trigger
            marketData.RealtimePrice += (symbol, price) =>
            {
                if (!ShouldRunRealtime(symbol))
                    return;

                _ = RunReactive(symbol, KlineInterval.FiveMinutes, "REALTIME");
            };

            _logger.LogInformation(
                "[STRAT][PUSH] Reactive entry-point bound (REALTIME ENABLED)");
        }

        private static readonly HashSet<string> _htfSymbols =
    new(StringComparer.OrdinalIgnoreCase)
    {
        "BTCUSDT",
        "ETHUSDT",
        "SOLUSDT",
        "XRPUSDT",
        "BNBUSDT"
    };


        private readonly ConcurrentDictionary<string, DateTime> _lastSignalCandle = new();
        private async Task RunReactive(string symbol, KlineInterval interval, string reason)
        {
            var md = _marketData;
            if (md == null) return;


            // 🔥 Decision timeframe всегда FiveMinutes
            KlineInterval decisionTf = KlineInterval.FiveMinutes;

            if (_htfSymbols.Contains(symbol))
                decisionTf = KlineInterval.FifteenMinutes;

            var key = $"{symbol}:{decisionTf}";
            var now = DateTime.UtcNow;

            // Warmup guard
            if (!md.HasSnapshotState && md.IsInWarmup(symbol, decisionTf))
                return;

            // Anti-spam only for realtime
            if (reason == "REALTIME")
            {
                if (_lastReactiveRun.TryGetValue(key, out var last) &&
                    (now - last).TotalMilliseconds < 250)
                    return;
            }

            _lastReactiveRun[key] = now;

            var le = _reactiveLocks.GetOrAdd(key, _ => new LockEntry());
            le.LastUsedUtc = now;

            _ = Task.Run(async () =>
            {
                if (!await le.Gate.WaitAsync(0))
                {
                    le.Pending = true;
                    return;
                }

                try
                {
                    // ✅ CORRECT: load klines from MarketDataFacade
                    var klines = await md.GetKlinesAsync(
                        symbol,
                        decisionTf,
                        need: 200,
                        CancellationToken.None);

                    if (klines == null || klines.Count < 50)
                        return;

                    IReadOnlyList<BinanceFuturesUsdtKline> working = klines;

                    // ============================================
                    // 🔥 REALTIME INJECTION INTO LAST CANDLE
                    // ============================================

                    if (reason == "REALTIME")
                    {
                        var realtimePrice = md.GetLastPrice(symbol);

                        if (realtimePrice > 0)
                        {
                            var last = klines[^1];

                            var modified = new BinanceFuturesUsdtKline
                            {
                                OpenTime = last.OpenTime,
                                CloseTime = last.CloseTime,

                                OpenPrice = last.OpenPrice,

                                HighPrice = Math.Max(last.HighPrice, realtimePrice),
                                LowPrice = Math.Min(last.LowPrice, realtimePrice),

                                ClosePrice = realtimePrice,

                                Volume = last.Volume,
                                QuoteVolume = last.QuoteVolume,
                                TradeCount = last.TradeCount,

                                TakerBuyBaseVolume = last.TakerBuyBaseVolume,
                                TakerBuyQuoteVolume = last.TakerBuyQuoteVolume
                            };

                            var temp = klines.ToList();
                            temp[^1] = modified;

                            working = temp;
                        }
                    }

                    // ============================================
                    // 🔥 REAL SIGNAL EVALUATION
                    // ============================================

                    var decision = await EvaluateSignalAsync(
                        symbol,
                        decisionTf,
                        working,
                        CancellationToken.None);

                    SafeRecordDecisionTrace(symbol, decisionTf, decision);

                    if (!decision.Allow)
                        return;

                    var signal = decision.Signal;
                    if (signal == null)
                        return;


                    // CRITICAL: finalize required fields
                    signal.Symbol = symbol;
                    signal.Timeframe = decisionTf.ToString();
                    signal.EntryPrice = working[^1].ClosePrice;

                    signal.Time = DateTime.UtcNow;

                    // ensure TP list exists
                    //   signal.TakeProfit ??= new List<decimal>();

                    // ensure StopLoss valid
                    if (signal.StopLoss <= 0)
                        signal.StopLoss = working[^1].ClosePrice;

                    // ensure Side valid
                    if (!Enum.IsDefined(typeof(SignalSide), signal.Side))
                        return;

                    var sigKey = $"{symbol}:{signal.Side}:{decisionTf}";
                    var candleKey = $"{symbol}:{signal.Side}:{decisionTf}";
                    var candleTime = working[^1].OpenTime;
                    var nowUtc = DateTime.UtcNow;

                    if (_lastSignalUtc.TryGetValue(sigKey, out var lastUtc))
                    {
                        if (nowUtc - lastUtc < SignalCooldown)
                            return;
                    }

                    if (_lastSignalCandle.TryGetValue(candleKey, out var lastCandle))
                    {
                        if (lastCandle == candleTime)
                            return;
                    }

                    _lastSignalUtc[sigKey] = nowUtc;
                    _lastSignalCandle[candleKey] = candleTime;

                    var sw = Stopwatch.StartNew();
                    _logger.LogInformation(
    "[LATENCY] {symbol} {reason} eval {ms}ms",
    symbol,
    reason,
    sw.ElapsedMilliseconds);
                    OnSignalGenerated?.Invoke(signal);





                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "[STRAT][REALTIME][{symbol}] reactive error",
                        symbol);
                }
                finally
                {

                    le.Gate.Release();

                    if (le.Pending)
                    {
                        le.Pending = false;
                        _ = RunReactive(symbol, interval, "PENDING");
                    }
                }
            });
        }

        private void SafeRecordDecisionTrace(string symbol, KlineInterval tf, SignalDecisionTrace decision)
        {
            if (decision == null) return;

            try
            {
                // Проверяем Signal и Confidence
                decimal confidence = 0m;
                if (decision.Signal?.Confidence != null)
                    confidence = decision.Signal.Confidence.Value;

                _decisionTrace?.Record(new DecisionTraceSnapshot
                {
                    Symbol = symbol,
                    Timeframe = tf.ToString(),
                    Allow = decision.Allow,
                    FailedGate = decision.FailedGate?.Gate,
                    Reason = decision.FailedGate?.Reason,
                    Time = DateTime.UtcNow,
                    Confidence = confidence
                });
            }
            catch (Exception ex)
            {
                // Логируем, но не ломаем цепочку
                _logger.LogError(ex, "[DECISION][{symbol}][{tf}] DecisionTrace.Record failed (Safe)", symbol, tf);
            }
        }

        // ----------------------------- CORE HELPERS -----------------------------
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

        private static decimal Ema(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period, int index)
        {
            int start = Math.Max(0, index - period + 1);
            decimal k = 2m / (period + 1);

            decimal ema = klines[start].ClosePrice;
            for (int i = start + 1; i <= index; i++)
                ema = klines[i].ClosePrice * k + ema * (1 - k);

            return ema;
        }

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
            return bars <= 0 ? 0 : sumTr / bars;
        }

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
            var tr = GetTrueRange(current, prev);
            return tr >= atr * 2.2m;
        }

        private static bool IsTooSmallBody(BinanceFuturesUsdtKline current, decimal atr)
        {
            if (atr <= 0) return true;
            var body = Math.Abs(current.ClosePrice - current.OpenPrice);
            return body < atr * 0.08m;
        }



        //private TradeSignal? TryLiquidityGrab(string symbol, KlineInterval interval, IReadOnlyList<BinanceFuturesUsdtKline> klines)
        //{
        //    if (klines == null || klines.Count < 30) return null;

        //    int last = klines.Count - 1;
        //    if (last < 1) return null;

        //    var c = klines[last];
        //    var prev = klines[last - 1];

        //    // --- HARD SLOPE LOCK (ANTI-REVERSAL) ---
        //    decimal emaFast = Ema(klines, 21, last);
        //    decimal emaPrev = Ema(klines, 21, last - 5);
        //    decimal emaSlope = (emaFast - emaPrev) / emaPrev;

        //    // если наклон выраженный — НЕ разрешаем контртрендовый grab
        //    if (emaSlope > 0.0020m && c.ClosePrice < c.OpenPrice)
        //        return null; // вверх тренд → не ловим SHORT grab

        //    if (emaSlope < -0.0020m && c.ClosePrice > c.OpenPrice)
        //        return null; // вниз тренд → не ловим LONG grab

        //    decimal atr = Atr(klines, 14, last);
        //    if (atr <= 0) return null;

        //    if (IsTooBigImpulseBar(c, prev, atr)) return null;
        //    if (IsTooSmallBody(c, atr)) return null;

        //    var (slMult, tp1Mult, tp2Mult, tp3Mult) = GetAtrConfig(interval);

        //    bool longGrab =
        //        c.LowPrice < prev.LowPrice &&
        //        c.ClosePrice > prev.LowPrice &&
        //        (prev.LowPrice - c.LowPrice) > atr * 0.4m &&
        //        c.ClosePrice > c.OpenPrice;

        //    if (longGrab)
        //    {
        //        decimal entry = prev.LowPrice + atr * 0.2m;
        //        decimal sl = c.LowPrice - atr * slMult;

        //        var s = new TradeSignal
        //        {
        //            Symbol = symbol,
        //            Side = SignalSide.Buy,
        //            EntryPrice = entry,
        //            StopLoss = sl,
        //            Atr = atr,
        //            TakeProfits = new List<decimal>
        //            {
        //                entry + atr * tp1Mult,
        //                entry + atr * tp2Mult,
        //                entry + atr * tp3Mult
        //            },
        //            Time = c.CloseTime,
        //            Timeframe = interval.ToString(),
        //            Reason = "LIQUIDITY_GRAB_LONG",
        //            IsSuperSignal = true
        //        };

        //        NormalizeEntryAndSl(s);
        //        return s;
        //    }

        //    bool shortGrab =
        //        c.HighPrice > prev.HighPrice &&
        //        c.ClosePrice < prev.HighPrice &&
        //        (c.HighPrice - prev.HighPrice) > atr * 0.4m &&
        //        c.ClosePrice < c.OpenPrice;

        //    if (shortGrab)
        //    {
        //        decimal entry = prev.HighPrice - atr * 0.2m;
        //        decimal sl = c.HighPrice + atr * slMult;

        //        var s = new TradeSignal
        //        {
        //            Symbol = symbol,
        //            Side = SignalSide.Sell,
        //            EntryPrice = entry,
        //            StopLoss = sl,
        //            Atr = atr,
        //            TakeProfits = new List<decimal>
        //            {
        //                entry - atr * tp1Mult,
        //                entry - atr * tp2Mult,
        //                entry - atr * tp3Mult
        //            },
        //            Time = c.CloseTime,
        //            Timeframe = interval.ToString(),
        //            Reason = "LIQUIDITY_GRAB_SHORT",
        //            IsSuperSignal = true
        //        };

        //        NormalizeEntryAndSl(s);
        //        return s;
        //    }

        //    return null;
        //}


        //private static void NormalizeEntryAndSl(TradeSignal s)
        //{
        //    if (s.EntryPrice <= 0 || s.StopLoss <= 0)
        //        return;

        //    decimal dist = Math.Abs(s.EntryPrice - s.StopLoss);
        //    decimal minDist =
        //s.Atr.HasValue && s.Atr.Value > 0
        //? s.Atr.Value * 0.80m   // 🔥 FIX: было 0.30
        //: s.EntryPrice * 0.0025m;

        //    if (dist >= minDist)
        //        return;

        //    if (s.Side == SignalSide.Buy) s.StopLoss = s.EntryPrice - minDist;
        //    else if (s.Side == SignalSide.Sell) s.StopLoss = s.EntryPrice + minDist;
        //}

        private static void NormalizeEntryAndSl(TradeSignal s)
        {
            if (s == null)
                return;

            if (s.EntryPrice <= 0 || s.StopLoss <= 0)
                return;

            decimal dist = Math.Abs(s.EntryPrice - s.StopLoss);

            // ----------------------------------------------------------
            // Determine minimum stop distance
            // ----------------------------------------------------------

            decimal minDist;

            if (s.Atr.HasValue && s.Atr.Value > 0)
            {
                decimal atr = s.Atr.Value;

                // adaptive multiplier based on signal type
                decimal atrMult = s.Reason switch
                {
                    // pullbacks require tighter stops
                    "PULLBACK_EMA21_LONG" => 0.55m,
                    "PULLBACK_EMA21_SHORT" => 0.55m,

                    // liquidity grabs moderate
                    "LIQUIDITY_GRAB_CONTINUATION_LONG" => 0.65m,
                    "LIQUIDITY_GRAB_CONTINUATION_SHORT" => 0.65m,

                    // volatility expansion needs wider stop
                    "VOLATILITY_EXPANSION_BREAKOUT_LONG" => 0.85m,
                    "VOLATILITY_EXPANSION_BREAKOUT_SHORT" => 0.85m,

                    // default safe baseline
                    _ => 0.60m
                };

                minDist = atr * atrMult;

                // absolute floor protection (exchange precision, micro-ATR assets)
                decimal hardFloor = s.EntryPrice * 0.0015m;

                if (minDist < hardFloor)
                    minDist = hardFloor;
            }
            else
            {
                // fallback when ATR unavailable
                minDist = s.EntryPrice * 0.0025m;
            }

            // ----------------------------------------------------------
            // Apply clamp only if needed
            // ----------------------------------------------------------

            if (dist >= minDist)
                return;

            if (s.Side == SignalSide.Buy)
                s.StopLoss = s.EntryPrice - minDist;
            else
                s.StopLoss = s.EntryPrice + minDist;
        }

        /// // ----------------------------- SIGNAL PATTERNS -----------------------------

        bool HasImpulseBefore(
        IReadOnlyList<BinanceFuturesUsdtKline> klines,
        int last,
        decimal atr)
            {
                // ищем импульс в 3–6 свечах ДО
                for (int i = last - 6; i < last - 1; i++)
                {
                    if (i < 1) continue;

                    var k = klines[i];
                    var body = Math.Abs(k.ClosePrice - k.OpenPrice);

                    if (body >= atr * 0.8m)
                        return true;
                }

                return false;
            }

        private TradeSignal? TryLiquidityGrab(
            string symbol,
            KlineInterval interval,
            IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines == null || klines.Count < 40)
                return null;

            int last = klines.Count - 1;
            if (last < 10)
                return null;

            var c = klines[last];
            var prev = klines[last - 1];

            // ============================================================
            // ATR
            // ============================================================
            decimal atr = Atr(klines, 14, last);
            if (atr <= 0m)
                return null;

            // Reject abnormal candles
            if (IsTooBigImpulseBar(c, prev, atr))
                return null;

            if (IsTooSmallBody(c, atr))
                return null;

            // ============================================================
            // EMA TREND (normalized by ATR)
            // ============================================================
            decimal emaNow = Ema(klines, 21, last);
            decimal emaPrev = Ema(klines, 21, last - 8);

            if (emaPrev == 0m)
                return null;

            decimal emaDelta = emaNow - emaPrev;

            // slope normalized by ATR (volatility aware)
            decimal normalizedSlope = emaDelta / atr;

            const decimal slopeThreshold = 0.35m;   // calibrated normalized slope
            const decimal weakThreshold = 0.20m;

            bool strongUp = normalizedSlope > slopeThreshold;
            bool strongDown = normalizedSlope < -slopeThreshold;

            bool weakUp = normalizedSlope > weakThreshold;
            bool weakDown = normalizedSlope < -weakThreshold;

            if (!(strongUp || strongDown || weakUp || weakDown))
                return null;

            var (slMult, tp1Mult, tp2Mult, tp3Mult) = GetAtrConfig(interval);

            // ============================================================
            // LONG — continuation grab
            // ============================================================
            if (strongUp || weakUp)
            {
                decimal grabSize = prev.LowPrice - c.LowPrice;

                bool validGrab =
                    c.LowPrice < prev.LowPrice &&
                    c.ClosePrice > prev.LowPrice &&
                    grabSize >= atr * 0.35m &&
                    c.ClosePrice > c.OpenPrice;

                if (validGrab)
                {
                    decimal entry = prev.LowPrice + grabSize * 0.5m; // midpoint reclaim
                    decimal sl = c.LowPrice - atr * slMult;

                    decimal risk = entry - sl;
                    if (risk <= 0)
                        return null;

                    decimal tp1 = entry + atr * tp1Mult;
                    decimal rr = (tp1 - entry) / risk;

                    if (rr < 1.1m)
                        return null;

                    var signal = new TradeSignal
                    {
                        Symbol = symbol,
                        Side = SignalSide.Buy,
                        EntryPrice = entry,
                        StopLoss = sl,
                        Atr = atr,
                        Time = c.CloseTime,
                        Timeframe = interval.ToString(),
                        Reason = "LIQUIDITY_GRAB_CONTINUATION_LONG_V2",
                        IsSuperSignal = strongUp,
                        TakeProfits = new List<decimal>
                {
                    entry + atr * tp1Mult,
                    entry + atr * tp2Mult,
                    entry + atr * tp3Mult
                }
                    };

                    NormalizeEntryAndSl(signal);
                    return signal;
                }
            }

            // ============================================================
            // SHORT — continuation grab
            // ============================================================
            if (strongDown || weakDown)
            {
                decimal grabSize = c.HighPrice - prev.HighPrice;

                bool validGrab =
                    c.HighPrice > prev.HighPrice &&
                    c.ClosePrice < prev.HighPrice &&
                    grabSize >= atr * 0.35m &&
                    c.ClosePrice < c.OpenPrice;

                if (validGrab)
                {
                    decimal entry = prev.HighPrice - grabSize * 0.5m;
                    decimal sl = c.HighPrice + atr * slMult;

                    decimal risk = sl - entry;
                    if (risk <= 0)
                        return null;

                    decimal tp1 = entry - atr * tp1Mult;
                    decimal rr = (entry - tp1) / risk;

                    if (rr < 1.1m)
                        return null;

                    var signal = new TradeSignal
                    {
                        Symbol = symbol,
                        Side = SignalSide.Sell,
                        EntryPrice = entry,
                        StopLoss = sl,
                        Atr = atr,
                        Time = c.CloseTime,
                        Timeframe = interval.ToString(),
                        Reason = "LIQUIDITY_GRAB_CONTINUATION_SHORT_V2",
                        IsSuperSignal = strongDown,
                        TakeProfits = new List<decimal>
                {
                    entry - atr * tp1Mult,
                    entry - atr * tp2Mult,
                    entry - atr * tp3Mult
                }
                    };

                    NormalizeEntryAndSl(signal);
                    return signal;
                }
            }

            return null;
        }
        TradeSignal? impulseContinuation = null;
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

            if (!HasImpulseBefore(klines, last, atr))
                return null;

            decimal emaPrev = Ema(klines, 21, last - 5);
            decimal emaSlope = (ema - emaPrev) / emaPrev;
            if (Math.Abs(emaSlope) < 0.0015m)
                return null;

            decimal maxDistanceFromEma = 0m;
            int start = Math.Max(21, last - 10);

            for (int i = start; i <= last; i++)
            {
                decimal e = Ema(klines, 21, i);
                decimal dist = Math.Abs(klines[i].ClosePrice - e);
                if (dist > maxDistanceFromEma)
                    maxDistanceFromEma = dist;
            }

            if (maxDistanceFromEma < atr * 1.2m)
                return null;

            if (IsTooBigImpulseBar(c, prev, atr)) return null;
            if (IsTooSmallBody(c, atr)) return null;

            var (slMult, tp1Mult, tp2Mult, tp3Mult) = GetAtrConfig(interval);

            bool bull = c.ClosePrice > c.OpenPrice && c.LowPrice <= ema && c.ClosePrice > ema;
            if (bull)
            {
                decimal entry = ema + atr * 0.4m;
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

            bool bear = c.ClosePrice < c.OpenPrice && c.HighPrice >= ema && c.ClosePrice < ema;
            if (bear)
            {

                decimal entry = ema - atr * 0.4m;
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

        private TradeSignal? TryVolatilityExpansionEntry(
        string symbol,
        KlineInterval interval,
        IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines == null || klines.Count < 60)
                return null;

            int last = klines.Count - 1;
            var c = klines[last];

            decimal atrShort = Atr(klines, 14, last);
            decimal atrLong = Atr(klines, 40, last);

            if (atrShort <= 0m || atrLong <= 0m)
                return null;

            // ============================================================
            // 1️⃣ VOLATILITY SQUEEZE (short ATR contracted vs long ATR)
            // ============================================================
            bool isSqueeze = atrShort < atrLong * 0.75m;
            if (!isSqueeze)
                return null;

            // ============================================================
            // 2️⃣ BREAKOUT STRUCTURE (20 bar range break)
            // ============================================================
            decimal highest20 = decimal.MinValue;
            decimal lowest20 = decimal.MaxValue;

            for (int i = last - 20; i < last; i++)
            {
                highest20 = Math.Max(highest20, klines[i].HighPrice);
                lowest20 = Math.Min(lowest20, klines[i].LowPrice);
            }

            bool breakoutUp = c.ClosePrice > highest20;
            bool breakoutDown = c.ClosePrice < lowest20;

            if (!breakoutUp && !breakoutDown)
                return null;

            // ============================================================
            // 3️⃣ VOLUME EXPANSION (dynamic, not fixed 2.5x)
            // ============================================================
            decimal avgVol = 0m;
            for (int i = last - 20; i < last; i++)
                avgVol += klines[i].Volume;

            avgVol /= 20m;

            if (avgVol <= 0m)
                return null;

            bool volumeExpansion = c.Volume > avgVol * 1.8m;
            if (!volumeExpansion)
                return null;


            // запрещаем если объём уже падает
            if (klines[last].Volume < klines[last - 1].Volume)
                return null;
            // ============================================================
            // 4️⃣ IMPULSE QUALITY
            // ============================================================
            decimal body = Math.Abs(c.ClosePrice - c.OpenPrice);
            decimal range = c.HighPrice - c.LowPrice;

            if (range <= 0m)
                return null;

            if (body < atrShort * 1.1m)
                return null;

            bool strongCloseUp =
                breakoutUp &&
                (c.HighPrice - c.ClosePrice) <= range * 0.20m;

            bool strongCloseDown =
                breakoutDown &&
                (c.ClosePrice - c.LowPrice) <= range * 0.20m;

            if (!strongCloseUp && !strongCloseDown)
                return null;

            var (slMult, tp1Mult, tp2Mult, tp3Mult) = GetAtrConfig(interval);

            // ============================================================
            // LONG
            // ============================================================
            if (strongCloseUp)
            {
                decimal entry = c.ClosePrice;
                decimal sl = c.LowPrice - atrShort * slMult;

                decimal risk = entry - sl;
                if (risk <= 0m)
                    return null;

                decimal tp1 = entry + atrShort * tp1Mult;
                decimal rr = (tp1 - entry) / risk;

                if (rr < 1.2m)
                    return null;

                var signal = new TradeSignal
                {
                    Symbol = symbol,
                    Side = SignalSide.Buy,
                    EntryPrice = entry,
                    StopLoss = sl,
                    Atr = atrShort,
                    Timeframe = interval.ToString(),
                    Time = c.CloseTime,
                    Reason = "VOLATILITY_EXPANSION_BREAKOUT_LONG_V2",
                    IsSuperSignal = true,
                    TakeProfits = new List<decimal>
            {
                entry + atrShort * tp1Mult,
                entry + atrShort * tp2Mult,
                entry + atrShort * tp3Mult
            }
                };

                NormalizeEntryAndSl(signal);
                return signal;
            }

            // ============================================================
            // SHORT
            // ============================================================
            if (strongCloseDown)
            {
                decimal entry = c.ClosePrice;
                decimal sl = c.HighPrice + atrShort * slMult;

                decimal risk = sl - entry;
                if (risk <= 0m)
                    return null;

                decimal tp1 = entry - atrShort * tp1Mult;
                decimal rr = (entry - tp1) / risk;

                if (rr < 1.2m)
                    return null;

                var signal = new TradeSignal
                {
                    Symbol = symbol,
                    Side = SignalSide.Sell,
                    EntryPrice = entry,
                    StopLoss = sl,
                    Atr = atrShort,
                    Timeframe = interval.ToString(),
                    Time = c.CloseTime,
                    Reason = "VOLATILITY_EXPANSION_BREAKOUT_SHORT_V2",
                    IsSuperSignal = true,
                    TakeProfits = new List<decimal>
            {
                entry - atrShort * tp1Mult,
                entry - atrShort * tp2Mult,
                entry - atrShort * tp3Mult
            }
                };

                NormalizeEntryAndSl(signal);
                return signal;
            }

            return null;
        }
        private bool IsVolatilityExpansionAllowed(SmartRegimeInfo smart)
        {
            // ❌ запрещаем в грязи
            if (smart.IsDangerChopZone)
                return false;

            // ✅ разрешаем в Range / Squeeze / Transition
            if (smart.BaseRegime == MarketRegime.Range ||
                smart.SmartType == SmartRegimeType.SmartSqueeze ||
                smart.SmartType == SmartRegimeType.SmartRange)
                return true;

            // ⚠️ в сильном тренде — редко, но можно
            if (smart.SmartType == SmartRegimeType.SmartStrongTrend &&
                Math.Abs(smart.TrendSlopePercent) < 0.03m)
                return true;



            return false;
        }
        private static bool TooManyImpulseBars(IReadOnlyList<BinanceFuturesUsdtKline> klines, int last, decimal atr)
        {
            int count = 0;

            for (int i = last; i > last - 8 && i > 0; i--)
            {
                var body = Math.Abs(klines[i].ClosePrice - klines[i].OpenPrice);
                if (body > atr * 0.9m)
                    count++;
                else
                    break;
            }

            return count >= 4;
        }
        // ----------------------------- REGIME/CONF HELPERS -----------------------------
        //private static int GetAdaptiveThreshold(MarketRegime baseRegime, SmartRegimeType smartType, decimal volatility, decimal slope)
        //{
        //    int threshold;

        //    bool isRangeLike =
        //        baseRegime == MarketRegime.Range ||
        //        smartType == SmartRegimeType.SmartRange ||
        //        smartType == SmartRegimeType.SmartSqueeze;

        //    bool isStrongTrendLike =
        //        baseRegime == MarketRegime.StrongUpTrend ||
        //        baseRegime == MarketRegime.StrongDownTrend ||
        //        smartType == SmartRegimeType.SmartStrongTrend;

        //    bool isTrendLike =
        //        baseRegime == MarketRegime.StrongUpTrend ||
        //        baseRegime == MarketRegime.StrongDownTrend ||
        //        smartType == SmartRegimeType.SmartTrend;

        //    if (isRangeLike) threshold = 35;
        //    else if (isStrongTrendLike) threshold = 60;
        //    else if (isTrendLike) threshold = 45;
        //    else threshold = 45;

        //    if (volatility < 0.10m) threshold -= 10;
        //    else if (volatility > 0.30m) threshold += 10;

        //    if (Math.Abs(slope) > 0.7m) threshold += 5;

        //    if (threshold < 25) threshold = 25;
        //    if (threshold > 80) threshold = 80;

        //    return threshold;
        //}

        private static int GetAdaptiveThreshold(
    MarketRegime baseRegime,
    SmartRegimeType smartType,
    decimal volatility,      // 0.02 = 2%
    decimal slope)           // 0.01 = 1%
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
                smartType == SmartRegimeType.SmartTrend;

            // --- 1️⃣ Base ---
            if (isRangeLike) threshold = 35;
            else if (isStrongTrendLike) threshold = 60;
            else if (isTrendLike) threshold = 45;
            else threshold = 45;

            // --- 2️⃣ Volatility adjustment ---
            // реальный intraday диапазон 0.005 – 0.05
            if (volatility < 0.015m)         // <1.5% — спокойный рынок
                threshold -= 5;
            else if (volatility > 0.05m)     // >5% — хаос
                threshold += 10;

            // --- 3️⃣ Slope adjustment ---
            // нормальный тренд 0.005 – 0.03
            if (Math.Abs(slope) > 0.02m)     // >2% slope — ускорение
                threshold += 5;

            // --- 4️⃣ Clamp ---
            return Math.Clamp(threshold, 25, 80);
        }

        private static bool IsFastTrendOverride(SmartRegimeInfo smart)
        {
            bool strongTrend =
                smart.BaseRegime == MarketRegime.StrongUpTrend ||
                smart.BaseRegime == MarketRegime.StrongDownTrend;

            bool smartStrong = smart.SmartType == SmartRegimeType.SmartStrongTrend;

            bool slopeOk = Math.Abs(smart.TrendSlopePercent) >= 0.018m;
            bool volOk = smart.VolatilityPercent > 0m && smart.VolatilityPercent <= 0.010m;
            bool confOk = smart.Confidence >= 0.35m;

            return strongTrend && smartStrong && slopeOk && volOk && confOk;
        }

        private decimal GetDynamicMinRr(string symbol, KlineInterval interval, SmartRegimeInfo smart, TradeSignal signal)
        {
            decimal atrPct = 0m;
            if (signal.Atr.HasValue && signal.Atr.Value > 0 && signal.EntryPrice > 0)
                atrPct = signal.Atr.Value / signal.EntryPrice;

            // =========================
            // TF SCALE (post-1m cleanup)
            // =========================
            decimal tfRrScale = interval switch
            {
                KlineInterval.FiveMinutes => 0.85m, // 🔥 ключевое
                KlineInterval.FifteenMinutes => 1.00m,
                _ => 1.00m
            };

            var regime = smart.BaseRegime;
            var smartType = smart.SmartType;
            var vol = smart.VolatilityPercent;
            var slope = smart.TrendSlopePercent;

            bool isSqueeze = regime == MarketRegime.Range || smartType == SmartRegimeType.SmartSqueeze;

            bool isStrongTrendLike =
                regime == MarketRegime.StrongUpTrend ||
                regime == MarketRegime.StrongDownTrend ||
                smartType == SmartRegimeType.SmartStrongTrend;

            bool strongSlope = Math.Abs(slope) >= 0.02m;
            bool highVol = vol >= 0.015m || atrPct >= 0.015m;
            bool lowVol = vol <= 0.005m || atrPct <= 0.005m;

            decimal minRr = 2.0m;

            if (isSqueeze)
                minRr = 2.5m;
            else if (isStrongTrendLike && strongSlope)
                minRr = highVol ? 1.7m : 1.8m;
            else
                minRr = lowVol ? 2.2m : 2.0m;

            AiSelfLearningService.AiTrendPrediction? trend = null;
            try
            {
                trend = _aiLearning.PredictTrend(symbol, regime, slope, vol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[STRAT][{symbol}][{interval}] PredictTrend ERROR → fallback dynamic RR", symbol, interval);
            }

            if (trend != null && trend.Direction != 0)
            {
                bool sameDir =
                    (trend.Direction > 0 && signal.Side == SignalSide.Buy) ||
                    (trend.Direction < 0 && signal.Side == SignalSide.Sell);

                if (sameDir) minRr *= trend.RrBias;
                else
                {
                    var extra = (1.0m - trend.RrBias);
                    if (extra < 0) extra = 0;
                    minRr *= 1.0m + extra;
                }
            }

            minRr = Math.Clamp(minRr, 1.4m, 2.6m);

            // Apply TF scale
            minRr *= tfRrScale;

            // Final clamp after TF adaptation
            minRr = Math.Clamp(minRr, 1.3m, 2.4m);


            if (signal.Side == SignalSide.Sell &&
                (smart.BaseRegime == MarketRegime.StrongDownTrend ||
                 smart.SmartType == SmartRegimeType.SmartStrongTrend))
            {
                minRr *= 0.9m;
            }

            return minRr;
        }

        // ----------------------------- DECISION TRACE MODEL -----------------------------
        internal sealed class SignalDecisionTrace
        {
            public bool Allow { get; set; }
            public TradeSignal? Signal { get; set; }
            public List<FastFailResult> Gates { get; } = new();

            public FastFailResult? FailedGate => Gates.FirstOrDefault(g => !g.Allow);

            public void Add(FastFailResult r)
            {
                Gates.Add(r);
                if (!r.Allow)
                    Allow = false;
            }
        }


        private TradeSignal? TryImpulseContinuation(
        string symbol,
        KlineInterval tf,
        IReadOnlyList<BinanceFuturesUsdtKline> klines,
        SmartRegimeInfo smart)
        {
            if (klines == null || klines.Count < 120)
                return null;

            // --- only strong trend ---
            bool strongTrend =
                smart.BaseRegime == MarketRegime.StrongUpTrend ||
                smart.BaseRegime == MarketRegime.StrongDownTrend ||
                smart.SmartType == SmartRegimeType.SmartStrongTrend;

            if (!strongTrend)
                return null;

            int i = klines.Count - 1;
            var c0 = klines[i];
            var c1 = klines[i - 1];

            decimal atr = Atr(klines, 14, i);
            if (atr <= 0m)
                return null;

            decimal ema21 = EmaClose(klines, 21, i);

            // --- distance filter (CORE) ---
            decimal dist = Math.Abs(c0.ClosePrice - ema21);
            decimal minDist = atr * 1.2m;
            decimal maxDist = atr * 3.0m;

            if (dist < minDist || dist > maxDist)
                return null;

            // --- impulse check (no climax) ---
            decimal body0 = Math.Abs(c0.ClosePrice - c0.OpenPrice);
            decimal body1 = Math.Abs(c1.ClosePrice - c1.OpenPrice);

            bool impulseOk =
                body0 >= atr * 0.4m &&
                (body0 + body1) <= atr * 2.2m; // anti-climax

            if (!impulseOk)
                return null;

            // ===== ANTI OVER-EXTENSION FILTER =====
            if (TooManyImpulseBars(klines, i, atr))
                return null;

            // --- direction ---
            bool slopeUp = smart.TrendSlopePercent > 0m;
            bool slopeDown = smart.TrendSlopePercent < 0m;

            // --- LONG continuation ---
            if (slopeUp && c0.ClosePrice > ema21)
            {
                var s = new TradeSignal
                {
                    Symbol = symbol,
                    Side = SignalSide.Buy,
                    Reason = "IMPULSE_CONTINUATION",
                    Atr = atr,
                    Confidence = smart.Confidence * 0.85m, // penalty
                    SizeMultiplier = 0.35m,
                    ForceFullExit = true,
                    TimeStopBars = 4
                };
                NormalizeEntryAndSl(s);
                return s;
            }

            // --- SHORT continuation ---
            if (slopeDown && c0.ClosePrice < ema21)
            {
                var s = new TradeSignal
                {
                    Symbol = symbol,
                    Side = SignalSide.Sell,
                    Reason = "IMPULSE_CONTINUATION",
                    Atr = atr,
                    Confidence = smart.Confidence * 0.85m,
                    SizeMultiplier = 0.35m,
                    ForceFullExit = true,
                    TimeStopBars = 4
                };
                NormalizeEntryAndSl(s);
                return s;
            }

            return null;
        }

        /// <summary>
        /// EarlyTrendJoin — универсальный вход на старте импульса/пробоя, чтобы не пропускать фазу "поехали".
        /// Встроен анти-FOMO: если цена уже слишком далеко от EMA21 (late), сигнал не даём.
        /// </summary>
        private TradeSignal? TryEarlyTrendJoin(
            string symbol,
            KlineInterval tf,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            SmartRegimeInfo smart)
        {
            if (klines == null || klines.Count < 120) return null;

            int i = klines.Count - 1;
            var c0 = klines[i];
            var c1 = klines[i - 1];
            var c2 = klines[i - 2];

            decimal atr = Atr(klines, 14, i);
            if (atr <= 0m) return null;

            // EMA расчёты (используй свои готовые методы/кэш если есть)
            decimal ema21 = EmaClose(klines, 21, i);
            decimal ema55 = EmaClose(klines, 55, i);
            decimal ema99 = EmaClose(klines, 99, i);

            // 1) Базовая структурная валидность: цена над EMA21 для long / под EMA21 для short
            bool priceAbove21 = c0.ClosePrice > ema21;
            bool priceBelow21 = c0.ClosePrice < ema21;

            // 2) Анти-FOMO / анти-late: цена не должна быть слишком далеко от EMA21
            //    (это именно то, что убивает входы на вершинах)
            decimal dist = Math.Abs(c0.ClosePrice - ema21);
            decimal maxDist = atr * 1.10m; // универсально, не слишком жёстко
            if (dist > maxDist) return null;

            // 3) Импульсность: последние 1-2 свечи должны быть "смысловые"
            //decimal body0 = Math.Abs(c0.ClosePrice - c0.ClosePrice);
            //decimal body1 = Math.Abs(c1.ClosePrice - c1.ClosePrice);
            decimal body0 = Math.Abs(c0.ClosePrice - c0.OpenPrice);
            decimal body1 = Math.Abs(c1.ClosePrice - c1.OpenPrice);

            bool impulseOk =
                body0 >= atr * 0.55m ||
                (body0 + body1) >= atr * 0.85m;

            if (!impulseOk) return null;

            // 4) Поддержка тренда: EMA21 должна быть не "мертвая"
            //    (минимальный наклон/драйв, но без overfit)
            decimal slopeAbs = Math.Abs(smart.TrendSlopePercent);
            bool slopeOk = slopeAbs >= 0.0015m || smart.VolatilityPercent >= 0.03m;

            if (!slopeOk) return null;

            // 5) Контекст: если явный strong downtrend — не лезем в long early join (и наоборот)
            //    (это защитит от "ловли ножей")
            bool strongUp = smart.BaseRegime == MarketRegime.StrongUpTrend || smart.SmartType == SmartRegimeType.SmartStrongTrend;
            bool strongDown = smart.BaseRegime == MarketRegime.StrongDownTrend || smart.SmartType == SmartRegimeType.SmartStrongTrend;

            // direction inference from slope sign
            bool slopeUp = smart.TrendSlopePercent > 0m;
            bool slopeDown = smart.TrendSlopePercent < 0m;

            // --- LONG early trend join ---
            if (priceAbove21 && slopeUp)
            {
                // минимальная "ступенька": EMA21 выше EMA55 или цена удерживает EMA21 после пробоя
                bool structureOk = ema21 >= ema55 * 0.999m || (c1.ClosePrice > ema21 && c0.ClosePrice > ema21);

                if (!structureOk) return null;

                // в сильном даун-тренде early-long запрещаем
                if (smart.BaseRegime == MarketRegime.StrongDownTrend) return null;

                var s = new TradeSignal
                {
                    Symbol = symbol,
                    Side = SignalSide.Buy,
                    Reason = "EARLY_TREND_JOIN",
                    Atr = atr,
                    Confidence = smart.Confidence
                };
                NormalizeEntryAndSl(s);
                return s;
            }

            // --- SHORT early trend join ---
            if (priceBelow21 && slopeDown)
            {
                bool structureOk = ema21 <= ema55 * 1.001m || (c1.ClosePrice < ema21 && c0.ClosePrice < ema21);

                if (!structureOk) return null;

                // в сильном ап-тренде early-short запрещаем
                if (smart.BaseRegime == MarketRegime.StrongUpTrend) return null;

                var s = new TradeSignal
                {
                    Symbol = symbol,
                    Side = SignalSide.Sell,
                    Reason = "EARLY_TREND_JOIN",
                    Atr = atr,
                    Confidence = smart.Confidence
                };
                NormalizeEntryAndSl(s);
                return s;
            }

            return null;
        }

        private static decimal EmaClose(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period, int idx)
        {
            // простой EMA без кэша — если у тебя уже есть EMA-метод, замени на него
            // берём окно 4*period (достаточно стабильно)
            int start = Math.Max(0, idx - period * 4);
            decimal k = 2m / (period + 1m);

            decimal ema = klines[start].ClosePrice;
            for (int i = start + 1; i <= idx; i++)
                ema = klines[i].ClosePrice * k + ema * (1m - k);

            return ema;
        }

        // ----------------------------- GATES 0..7 (PRODUCTION) -----------------------------
        private FastFailResult Gate0_Data(string symbol, KlineInterval tf, IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines == null) return FastFailResult.Fail("DATA", "klines=null");
            if (klines.Count < 30) return FastFailResult.Fail("DATA", $"bars={klines.Count}<30");
            return FastFailResult.Ok();
        }

        private FastFailResult Gate1_SmartRegime(
           string symbol,
           KlineInterval tf,
           IReadOnlyList<BinanceFuturesUsdtKline> klines,
           out SmartRegimeInfo smart)
        {
            // default safe fallback (never null)
            smart = new SmartRegimeInfo
            {
                Symbol = symbol,
                Interval = tf,
                BaseRegime = MarketRegime.Range,
                SmartType = SmartRegimeType.Unknown,
                TrendSlopePercent = 0m,
                VolatilityPercent = 0m,
                Confidence = 0m
            };

            _engineState.LastEntryDecision = "REGIME_EVALUATE";

            try
            {
                var evaluated = _smartRegimeService.Evaluate(symbol, tf, klines);
                if (evaluated == null)
                    return FastFailResult.Fail("SMART", "Evaluate returned null");

                // ------------------------------------------------------------
                // Sanity checks (defensive)
                // ------------------------------------------------------------
                if (evaluated.Confidence < 0m || evaluated.Confidence > 1.0m)
                {
                    _logger.LogWarning(
                        "[SMART] Confidence out of range {value} for {symbol} {tf}. Clamped.",
                        evaluated.Confidence,
                        symbol,
                        tf);

                    evaluated.Confidence = Math.Clamp(evaluated.Confidence, 0m, 1m);
                }

                if (Math.Abs(evaluated.TrendSlopePercent) > 5.0m)   // >500% slope — абсурд
                    return FastFailResult.Fail("SMART", "Invalid slope");

                if (evaluated.VolatilityPercent < 0m || evaluated.VolatilityPercent > 1.0m)
                    return FastFailResult.Fail("SMART", "Invalid volatility");

                smart = evaluated;

                // ------------------------------------------------------------
                // Shared ATR (once)
                // ------------------------------------------------------------
                decimal atr14 = 0m;
                try
                {
                    atr14 = Atr(klines, 14, klines.Count - 1);
                }
                catch { /* non-critical */ }

                try
                {
                    _aiLearning.RecordMarketStateTriggered(
                        reason: "BASE_REGIME",
                        symbol: symbol,
                        timeframe: tf.ToString(),
                        regime: smart.BaseRegime,
                        slope: smart.TrendSlopePercent,
                        volatility: smart.VolatilityPercent,
                        atr: atr14,
                        confidence: smart.Confidence
                    );
                }
                catch { /* non-critical */ }

                // ------------------------------------------------------------
                // HYBRID periodic snapshot (fail-safe)
                // ------------------------------------------------------------
                try
                {
                    _aiLearning.TryHybridPeriodicSnapshot(
                        symbol: symbol,
                        timeframe: tf.ToString(),
                        regime: smart.BaseRegime,
                        slope: smart.TrendSlopePercent,
                        volatility: smart.VolatilityPercent,
                        atr: atr14,
                        confidence: smart.Confidence
                    );
                }
                catch { /* non-critical */ }

                _engineState.LastEntryDecision = "REGIME_OK";
                return FastFailResult.Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[FAST][SMART] Evaluate failed {symbol} {tf}",
                    symbol,
                    tf
                );

                _engineState.LastEntryDecision = "REGIME_ERROR";
                return FastFailResult.Fail("SMART", "Evaluate error");
            }
        }

        private FastFailResult Gate2_Confidence(SmartRegimeInfo smart, bool lowerRegimeThreshold, string symbol)
        {
            // HARD BLOCK: chop is forbidden
            if (smart.IsDangerChopZone)
                return FastFailResult.Fail("CONF", "DangerChopZone");

            int adaptiveThreshold = GetAdaptiveThreshold(
                smart.BaseRegime,
                smart.SmartType,
                smart.VolatilityPercent,
                smart.TrendSlopePercent);

            decimal thrFrac = adaptiveThreshold / 100m;
            decimal safetyBuffer = 0.10m;

            // Soft-entry mode (test / relax)
            if (lowerRegimeThreshold)
            {
                adaptiveThreshold = Math.Max(20, (int)(adaptiveThreshold * 0.8));
                thrFrac = adaptiveThreshold / 100m;
                safetyBuffer = 0.20m;

                LastSoftEntry = true;
                _engineState.SoftEntry = true;
            }

            bool fastTrendOverride = IsFastTrendOverride(smart);
            var cfg = _confidenceCfg.Resolve(symbol);

            // UI / EngineState snapshot
            _engineState.LastEntryDecision = "CONF_CHECK";
            _engineState.ConfidenceRaw = smart.Confidence;
            _engineState.ConfidencePercent = (int)(smart.Confidence * 100);
            _engineState.ConfidenceLevel =
                smart.Confidence >= cfg.Bands.HighFrom ? "HIGH" :
                smart.Confidence >= cfg.MinEntry ? "MEDIUM" : "LOW";

            // Expose confidence to UI consumers
            Confidence = smart.Confidence;

            // ABSOLUTE HARD FLOOR (never bypassed)
            if (smart.Confidence < cfg.MinEntry)
                return FastFailResult.Fail(
                    "CONF",
                    $"confidence={smart.Confidence:P0}<min={cfg.MinEntry:P0}"
                );


            // Adaptive threshold with safety buffer
            var effectiveThreshold = thrFrac - safetyBuffer;

            // Clamp to sane bounds (never zero, never extreme)
            effectiveThreshold = Math.Clamp(effectiveThreshold, 0.10m, 0.80m);

            // Fast trend override: soften, not disable
            if (fastTrendOverride)
                effectiveThreshold *= 0.85m;

            if (smart.Confidence < effectiveThreshold && !IsFastTrendOverride(smart))
                return FastFailResult.Fail(
                    "CONF",
                    $"confidence={smart.Confidence:P0}<thr={effectiveThreshold:P0} (fastTrend={fastTrendOverride})"
                );



            return FastFailResult.Ok();
        }

        private FastFailResult Gate2_ConfidenceHybrid(
        decimal finalConfidence,
        SmartRegimeInfo smart,
        bool lowerRegimeThreshold,
        string symbol)
            {
                var cfg = _confidenceCfg.Resolve(symbol);

                // ============================================================
                // 0️⃣ HARD BLOCK
                // ============================================================

                if (smart.IsDangerChopZone)
                    return FastFailResult.Fail("CONF", "DangerChopZone");

                // ============================================================
                // 1️⃣ Adaptive Threshold
                // ============================================================

                int adaptiveThreshold = GetAdaptiveThreshold(
                    smart.BaseRegime,
                    smart.SmartType,
                    smart.VolatilityPercent,
                    smart.TrendSlopePercent);

                if (lowerRegimeThreshold)
                {
                    adaptiveThreshold = (int)(adaptiveThreshold * 0.85m);
                    LastSoftEntry = true;
                    _engineState.SoftEntry = true;
                }

                decimal adaptiveFloor = adaptiveThreshold / 100m;

                // Fast trend override
                if (IsFastTrendOverride(smart))
                    adaptiveFloor *= 0.85m;

                // ============================================================
                // 2️⃣ Absolute floor (минимальный порог стратегии)
                // ============================================================

                decimal absoluteFloor = cfg.MinEntry;

                // ============================================================
                // 3️⃣ Final effective floor
                // ============================================================

                decimal finalFloor = Math.Max(absoluteFloor, adaptiveFloor);

                finalFloor = Math.Clamp(finalFloor, 0.10m, 0.85m);

                if (finalConfidence < finalFloor)
                {
                    return FastFailResult.Fail(
                        "CONF",
                        $"conf={finalConfidence:P0}<floor={finalFloor:P0}"
                    );
                }

                // ============================================================
                // 4️⃣ State update
                // ============================================================

                _engineState.LastEntryDecision = "CONF_OK";
                _engineState.ConfidenceRaw = finalConfidence;
                _engineState.ConfidencePercent = (int)(finalConfidence * 100);

                _engineState.ConfidenceLevel =
                    finalConfidence >= cfg.Bands.HighFrom ? "HIGH" :
                    finalConfidence >= cfg.MinEntry ? "MEDIUM" :
                    "LOW";

                Confidence = finalConfidence;

                return FastFailResult.Ok();
            }

        private FastFailResult Gate2_5_TrendPhaseLock(
        IReadOnlyList<BinanceFuturesUsdtKline> klines,
        SmartRegimeInfo smart,
        TradeSignal? candidate)
        {
            if (candidate == null) return FastFailResult.Ok();

            decimal atr = Atr(klines, 14, klines.Count - 1);
            var phase = DetectTrendPhase(klines, atr, smart.TrendSlopePercent);

            if (phase == TrendPhase.Distribution || phase == TrendPhase.Exhaustion)
            {
                if (candidate.Side == SignalSide.Buy && smart.TrendSlopePercent > 0)
                    return FastFailResult.Fail("PHASE", "BLOCK LONG — trend end");

                if (candidate.Side == SignalSide.Sell && smart.TrendSlopePercent < 0)
                    return FastFailResult.Fail("PHASE", "BLOCK SHORT — trend end");
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

            // --- regime flags ---
            bool rangeLike =
                smart.BaseRegime == MarketRegime.Range ||
                smart.SmartType == SmartRegimeType.SmartRange;

            bool squeezeLike = smart.SmartType == SmartRegimeType.SmartSqueeze;

            bool trendLike =
                smart.BaseRegime == MarketRegime.UpTrend ||
                smart.BaseRegime == MarketRegime.DownTrend ||
                smart.BaseRegime == MarketRegime.StrongUpTrend ||
                smart.BaseRegime == MarketRegime.StrongDownTrend ||
                smart.SmartType == SmartRegimeType.SmartTrend ||
                smart.SmartType == SmartRegimeType.SmartStrongTrend;

            // --- patterns ---
            TradeSignal? pullback = null;
            TradeSignal? liquidity = null;
            TradeSignal? earlyTrend = null;

            // ============================================================
            // 1) Pattern priority (UNIVERSAL)
            //    - Squeeze: EarlyTrendJoin (breakout start) > Pullback > Liquidity
            //    - Range:   Liquidity (mean-revert) > Pullback > EarlyTrendJoin (rare)
            //    - Trend:   Pullback (best) > EarlyTrendJoin (start-of-trend) > Liquidity (only if aligned)
            // ============================================================
            TradeSignal? continuation = null;
            if (squeezeLike)
            {
                // В squeeze чаще всего нужен вход "в старт импульса"
                earlyTrend = TryEarlyTrendJoin(symbol, tf, klines, smart);
                if (earlyTrend == null)
                    pullback = TryPullbackEma21(symbol, tf, klines);
                if (pullback == null)
                    liquidity = TryLiquidityGrab(symbol, tf, klines);
            }
            else if (rangeLike)
            {
                // В range приоритет mean-revert/liq-grab
                liquidity = TryLiquidityGrab(symbol, tf, klines);
                if (liquidity == null)
                    pullback = TryPullbackEma21(symbol, tf, klines);
                if (pullback == null)
                    earlyTrend = TryEarlyTrendJoin(symbol, tf, klines, smart); // допускаем, но редко
            }
            else // trend-like / other
            {
                // В тренде: pullback лучший, но если его нет — early join, чтобы не пропускать старт тренда
                pullback = TryPullbackEma21(symbol, tf, klines);
                if (pullback == null)
                    earlyTrend = TryEarlyTrendJoin(symbol, tf, klines, smart);


                if (pullback == null && earlyTrend == null)
                    continuation = TryImpulseContinuation(symbol, tf, klines, smart);




                // LiquidityGrab в тренде — только как fallback (и дальше будет slope-lock)
                if (pullback == null && earlyTrend == null && continuation == null)
                    liquidity = TryLiquidityGrab(symbol, tf, klines);


            }

            // pick base
            baseSignal = pullback ?? earlyTrend ?? continuation ?? liquidity;
            if (baseSignal == null)
            {
                _engineState.LastEntryDecision = "NO_BASE_PATTERN";
                return FastFailResult.Fail("BASE", "no base pattern");
            }

            // ============================================================
            // 2) HARD SMART-SLOPE LOCK (final authority) — только против тренда
            //    (оставляем твою логику, но расширяем на liquidity в trendLike)
            // ============================================================

            var slopeLock = 0.008m + smart.VolatilityPercent * 1.2m;
            slopeLock = Math.Clamp(slopeLock, 0.006m, 0.025m);

            bool slopeUp = smart.TrendSlopePercent > slopeLock;
            bool slopeDown = smart.TrendSlopePercent < -slopeLock;

            bool useSlopeLock =
                smart.BaseRegime == MarketRegime.StrongUpTrend ||
                smart.BaseRegime == MarketRegime.StrongDownTrend ||
                smart.SmartType == SmartRegimeType.SmartStrongTrend;

            // LiquidityGrab против сильного slope — запрещаем, или заменяем на pullback
            if (useSlopeLock && liquidity != null && baseSignal == liquidity)
            {
                bool againstSlope =
                    (slopeUp && liquidity.Side == SignalSide.Sell) ||
                    (slopeDown && liquidity.Side == SignalSide.Buy);

                if (againstSlope)
                {
                    var alt = TryPullbackEma21(symbol, tf, klines);
                    if (alt != null)
                    {
                        baseSignal = alt;
                    }
                    else
                    {
                        _engineState.LastEntryDecision = "BLOCKED_LIQ_AGAINST_SLOPE";
                        return FastFailResult.Fail(
                            "DIR",
                            $"LiquidityGrabAgainstSlope slope={smart.TrendSlopePercent:F4}"
                        );
                    }
                }
            }

            // ============================================================
            // 3) Side-aware cooldown
            // ============================================================

            if (_lastStopTime.TryGetValue((symbol, baseSignal.Side), out var lastStop))
            {
                var diff = DateTime.UtcNow - lastStop;
                if (diff < TimeSpan.FromMinutes(10))
                {
                    _engineState.LastEntryDecision = "COOLDOWN";
                    return FastFailResult.Fail(
                        "COOLDOWN",
                        $"same-side cooldown {diff.TotalMinutes:F1}m"
                    );
                }
            }

            // ============================================================
            // 4) State + trace
            // ============================================================

            _engineState.LastEntryDecision = baseSignal == pullback ? "BASE_PULLBACK" :
                                             baseSignal == earlyTrend ? "BASE_EARLY_TREND" :
                                             "BASE_LIQUIDITY";

            try
            {
                _aiLearning.RecordMarketStateTriggered(
                    reason: baseSignal == pullback
                        ? "MICRO_SIGNAL_PULLBACK"
                        : baseSignal == earlyTrend
                            ? "MICRO_SIGNAL_EARLY_TREND"
                            : "MICRO_SIGNAL_LIQUIDITY",
                    symbol: symbol,
                    timeframe: tf.ToString(),
                    regime: smart.BaseRegime,
                    slope: smart.TrendSlopePercent,
                    volatility: smart.VolatilityPercent,
                    atr: baseSignal.Atr ?? 0m,
                    confidence: smart.Confidence
                );
            }
            catch { }

            return FastFailResult.Ok();
        }

        private FastFailResult Gate3_5_DirectionLock(
        string symbol,
        KlineInterval tf,
        IReadOnlyList<BinanceFuturesUsdtKline> klines,
        TradeSignal signal,
        SmartRegimeInfo smart,
        bool allowCounterTrendInRangeLike)
        {
            _logger.LogDebug(
                "[DIR][{sym}][{tf}] base={base} smart={smart} slope={slope:F4} side={side}",
                symbol, tf, smart.BaseRegime, smart.SmartType, smart.TrendSlopePercent, signal.Side
            );

            bool rangeLike =
                smart.BaseRegime == MarketRegime.Range ||
                smart.SmartType == SmartRegimeType.SmartRange;

            bool squeezeLike =
                smart.SmartType == SmartRegimeType.SmartSqueeze;

            // ------------------------------------------------------------------
            // 1) RANGE / SMART-RANGE → DirectionLock НЕ применяется
            // ------------------------------------------------------------------
            if (rangeLike && allowCounterTrendInRangeLike && !squeezeLike)
                return FastFailResult.Ok();

            // ------------------------------------------------------------------
            // 2) END-OF-TREND REVERSAL OVERRIDE
            // ------------------------------------------------------------------
     


            if (klines == null || klines.Count < 15)
            {
                _logger.LogWarning(
"[ATR CHECK] {symbol} tf={tf} klines={count} lastIndex={last}",
signal.Symbol,
tf,
klines?.Count ?? 0,
klines?.Count > 0 ? klines.Count - 1 : -1
);
                return FastFailResult.Fail("gATE_3_5_DirectionLock", "ATR_NOT_READY");
            }
                

            decimal atr = signal.Atr ?? Atr(klines, 14, klines.Count - 1);
            var phase = DetectTrendPhase(klines, atr, smart.TrendSlopePercent);

            bool endPhase =
                phase == TrendPhase.Distribution ||
                phase == TrendPhase.Exhaustion;

            if (endPhase)
            {
                // конец ап-тренда → разрешаем SHORT ТОЛЬКО при подтверждении
                if (smart.TrendSlopePercent > 0m && signal.Side == SignalSide.Sell)
                {
                    if (IsConfirmedReversal(klines, klines.Count - 1, atr, smart, SignalSide.Sell))
                        return FastFailResult.Ok();
                }

                // конец даун-тренда → разрешаем LONG ТОЛЬКО при подтверждении
                if (smart.TrendSlopePercent < 0m && signal.Side == SignalSide.Buy)
                {
                    if (IsConfirmedReversal(klines, klines.Count - 1, atr, smart, SignalSide.Buy))
                        return FastFailResult.Ok();
                }
            }

            // ------------------------------------------------------------------
            // 3) UNIVERSAL SLOPE LOCK (PRO SCALE)
            // ------------------------------------------------------------------
            var slopeLock = 0.008m + smart.VolatilityPercent * 1.2m;
            slopeLock = Math.Clamp(slopeLock, 0.006m, 0.025m);

            bool slopeUp = smart.TrendSlopePercent > slopeLock;
            bool slopeDown = smart.TrendSlopePercent < -slopeLock;

            bool trendLocked =
                smart.BaseRegime == MarketRegime.StrongUpTrend ||
                smart.BaseRegime == MarketRegime.StrongDownTrend ||
                smart.SmartType == SmartRegimeType.SmartStrongTrend ||
                squeezeLike;

            if (trendLocked)
            {
                if (slopeUp && signal.Side == SignalSide.Sell)
                    return FastFailResult.Fail(
                        "DIR",
                        $"BLOCK SHORT by slope slope={smart.TrendSlopePercent:F4}"
                    );

                if (slopeDown && signal.Side == SignalSide.Buy)
                    return FastFailResult.Fail(
                        "DIR",
                        $"BLOCK LONG by slope slope={smart.TrendSlopePercent:F4}"
                    );
            }

            return FastFailResult.Ok();
        }

        private FastFailResult Gate3_2_LateEntryFilter(
        string symbol,
        KlineInterval tf,
        IReadOnlyList<BinanceFuturesUsdtKline> klines,
        TradeSignal signal,
        SmartRegimeInfo smart)
            {
                if (klines == null || klines.Count < 40)
                    return FastFailResult.Ok();

                int last = klines.Count - 1;
                var c = klines[last];
                var prev = klines[last - 1];

                decimal atr = signal.Atr ?? Atr(klines, 14, last);
                if (atr <= 0m) return FastFailResult.Ok();

                decimal ema21 = Ema(klines, 21, last);
                decimal distFromEmaAtr = Math.Abs(c.ClosePrice - ema21) / atr;

                // =========================
                // EARLY TREND OVERRIDE (CRITICAL)
                // =========================
                bool earlyTrend =
                    smart.BaseRegime == MarketRegime.StrongUpTrend ||
                    smart.BaseRegime == MarketRegime.StrongDownTrend;

                if (earlyTrend)
                {
                    // slope ещё НЕ перегрет
                    decimal slopeLock = 0.012m + smart.VolatilityPercent * 1.2m;
                    slopeLock = Math.Clamp(slopeLock, 0.01m, 0.03m);

                    bool slopeOk =
                        (smart.TrendSlopePercent > 0 && smart.TrendSlopePercent < slopeLock) ||
                        (smart.TrendSlopePercent < 0 && smart.TrendSlopePercent > -slopeLock);

                    // цена не улетела от EMA
                    if (slopeOk && distFromEmaAtr <= 0.9m)
                        return FastFailResult.Ok();
                }

                // =========================
                // MICRO IMPULSE (ANTI-CHASE)
                // =========================
                int lookback = 6;
                int start = Math.Max(1, last - lookback + 1);

                decimal hi = klines[start].HighPrice;
                decimal lo = klines[start].LowPrice;
                for (int i = start; i <= last; i++)
                {
                    hi = Math.Max(hi, klines[i].HighPrice);
                    lo = Math.Min(lo, klines[i].LowPrice);
                }

                decimal moveAtr = (hi - lo) / atr;
                bool hugeBarNow = IsTooBigImpulseBar(c, prev, atr);

                int sameDirBars = 0;
                for (int i = Math.Max(1, last - 4); i <= last; i++)
                {
                    bool up = klines[i].ClosePrice > klines[i].OpenPrice;
                    if (signal.Side == SignalSide.Buy && up) sameDirBars++;
                    if (signal.Side == SignalSide.Sell && !up) sameDirBars++;
                }
                bool overheatByFlow = sameDirBars >= 4;

                bool rangeLike =
                    smart.BaseRegime == MarketRegime.Range ||
                    smart.SmartType == SmartRegimeType.SmartRange ||
                    smart.SmartType == SmartRegimeType.SmartSqueeze;

                bool strongTrendLike =
                    smart.BaseRegime == MarketRegime.StrongUpTrend ||
                    smart.BaseRegime == MarketRegime.StrongDownTrend ||
                    smart.SmartType == SmartRegimeType.SmartStrongTrend;

                decimal maxEmaDistAtr = rangeLike ? 0.75m : (strongTrendLike ? 1.15m : 1.05m);
                decimal impulseAtrThr = rangeLike ? 1.25m : (strongTrendLike ? 1.75m : 1.60m);

                // =========================
                // LATE TREND CYCLE (M5)
                // =========================
                int cycleLb = tf == KlineInterval.FiveMinutes ? 48 : 36;
                cycleLb = Math.Min(cycleLb, klines.Count - 1);
                int cStart = Math.Max(1, last - cycleLb + 1);

                decimal cycleHi = klines[cStart].HighPrice;
                decimal cycleLo = klines[cStart].LowPrice;
                for (int i = cStart; i <= last; i++)
                {
                    cycleHi = Math.Max(cycleHi, klines[i].HighPrice);
                    cycleLo = Math.Min(cycleLo, klines[i].LowPrice);
                }

                decimal cycleMoveAtr = (cycleHi - cycleLo) / atr;

                decimal maxCycleAtr =
                    rangeLike ? 1.6m :
                    strongTrendLike ? 3.0m :
                    2.3m;

                decimal maxEmaExt =
                    rangeLike ? 0.85m :
                    strongTrendLike ? 1.4m :
                    1.1m;

                if (cycleMoveAtr >= maxCycleAtr && distFromEmaAtr >= maxEmaExt)
                {
                    _engineState.LastEntryDecision = "BLOCKED_LATE_CYCLE";
                    CurrentMode = "Blocked:LATE_CYCLE";
                    return FastFailResult.Fail("LATE_CYCLE", "trend cycle exhausted");
                }

                bool lateChase =
                    moveAtr >= impulseAtrThr &&
                    distFromEmaAtr >= maxEmaDistAtr;

                if (hugeBarNow && distFromEmaAtr >= maxEmaDistAtr * 0.9m)
                    lateChase = true;

                if (overheatByFlow && distFromEmaAtr >= maxEmaDistAtr)
                    lateChase = true;

                if (!lateChase)
                    return FastFailResult.Ok();

                bool looksLikeRetest =
                    Math.Abs(signal.EntryPrice - ema21) <= atr * 0.25m;

                if (looksLikeRetest)
                    return FastFailResult.Ok();

                _engineState.LastEntryDecision = "BLOCKED_LATE_ENTRY";
                CurrentMode = "Blocked:LATE";

                return FastFailResult.Fail("LATE", "late chase");
            }

        private FastFailResult Gate4_RR(
          string symbol,
          KlineInterval tf,
          TradeSignal signal,
          SmartRegimeInfo smart,
          bool relaxRr)
            {
                // no TP → RR gate not applicable
                if (signal.TakeProfits == null || signal.TakeProfits.Count == 0)
                    return FastFailResult.Ok();

                // SL geometry must be valid ALWAYS
                var slDist = Math.Abs(signal.EntryPrice - signal.StopLoss);
                if (slDist <= 0)
                    return FastFailResult.Fail("RR", "slDist<=0");

                // use best TP distance (not first)
                var bestTp = signal.TakeProfits
                    .Select(tp => Math.Abs(tp - signal.EntryPrice))
                    .Where(d => d > 0)
                    .DefaultIfEmpty(0m)
                    .Max();

                if (bestTp <= 0)
                    return FastFailResult.Fail("RR", "tpDist<=0");

                var rr = bestTp / slDist;

                // sanity floor: even in relax mode geometry must make sense
                if (rr <= 0.5m)
                    return FastFailResult.Fail("RR", $"rr={rr:F2} too low");

                // relaxRR: skip strict RR check, but only AFTER geometry validation
                if (relaxRr)
                    return FastFailResult.Ok();

                var minRr = GetDynamicMinRr(symbol, tf, smart, signal);

                // AI gate multiplier (defensive)
                var w = 1.0m;
                try
                {
                    w = _aiLearning.GetGateMultiplier(symbol, smart.BaseRegime, "RR");
                }
                catch { /* non-critical */ }

                // clamp AI multiplier to avoid insanity
                w = Math.Clamp(w, 0.6m, 1.4m);
                minRr *= w;

                // absolute floor — never allow absurd RR requirements
                minRr = Math.Clamp(minRr, 1.2m, 3.0m);

                // fast trend override: soften RR but do not disable it
                bool fastTrendOverride = IsFastTrendOverride(smart);
                if (fastTrendOverride)
                    minRr *= 0.85m;

                if (rr < minRr)
                    return FastFailResult.Fail(
                        "RR",
                        $"rr={rr:F2}<min={minRr:F2} (w={w:F2}, fastTrend={fastTrendOverride})"
                    );

                return FastFailResult.Ok();
            }


        private FastFailResult Gate5_Pattern(string symbol, KlineInterval tf, IReadOnlyList<BinanceFuturesUsdtKline> klines, TradeSignal signal, bool relaxPatternBlock)
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
                    return FastFailResult.Fail("PATTERN", $"dir={pattern.Direction} score={pattern.Score:F2}");

                return FastFailResult.Ok();
            }
            catch
            {
                return FastFailResult.Ok(); // pattern errors are non-critical
            }
        }

        private async Task<FastFailResult> Gate6_LiquidityAsync(
          TradeSignal signal,
          SmartRegimeInfo smart,
          IReadOnlyList<BinanceFuturesUsdtKline> klines,
          KlineInterval tf,
          bool relaxLiquidity,
          CancellationToken ct)
        {
            // Pre-cancel guard (never treat cancel as hard fail in reactive path)
            if (ct.IsCancellationRequested)
                return FastFailResult.Fail("CANCELLED", "liquidity cancelled");

            // 1) LiquidityGuard — HARD structural block (top priority)
            var lg = _liquidityGuardService.Analyze(
                symbol: signal.Symbol,
                interval: tf,
                klines: klines,
                side: signal.Side,
                superSignal: signal.IsSuperSignal);

            // ALWAYS attach context (even if not blocking)
            signal.LiquidityScore = lg.Score;
            signal.LiquiditySoftWarning = lg.SoftWarning;
            signal.LiquidityDetails = lg.Details;

            // HARD block stays HARD
            if (lg.Block && !relaxLiquidity)
            {
                _engineState.LastEntryDecision = "BLOCKED_LIQ_GUARD";
                _engineState.BlockedByLiquidity = true;
                _engineState.LiquidityReason = lg.Reason.ToString();
                LastBlockedByLiquidity = true;

                return FastFailResult.Fail("LIQ_GUARD", lg.Reason.ToString());
            }

            // SOFT warning: DO NOT BLOCK — reduce size (executor will use it)
            if (lg.SoftWarning && !relaxLiquidity && !signal.IsSuperSignal)
            {
                // clamp 0.35..0.85 typical; tune as you like
                var m = 0.55m + 0.35m * Math.Clamp(lg.Score, 0m, 1m);
                signal.SizeMultiplier = Math.Clamp(m, 0.35m, 0.85m);

                // Optional: write marker "LIQ_SOFT" (not blocking)
                try
                {
                    var c = klines[^1];
                    _decisionMarkers.Add(new DecisionMarkerDto
                    {
                        Symbol = signal.Symbol,
                        Timeframe = tf.ToString(),
                        CandleTimeUtc = c.CloseTime,
                        Type = DecisionMarkerType.LowVolume,
                        Code = "LIQ_SOFT",
                        Details = $"soft {lg.Reason} score={lg.Score:F2} sizeMul={signal.SizeMultiplier:F2}",
                        Metrics = new Dictionary<string, decimal>
                        {
                            ["liqScore"] = lg.Score,
                            ["sizeMul"] = signal.SizeMultiplier
                        }
                    });
                }
                catch { }
            }
            // ------------------------------------------------------------------
            // 2) LiquidityCluster — async refinement / adjustment
            // ------------------------------------------------------------------
            TradeSignal? after;
            try
            {
                after = await _liquidityClusterService
                    .FilterAndAdjustAsync(signal, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // cancellation is NOT a trading decision
                return FastFailResult.Fail("CANCELLED", "liquidity cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[STRAT][{symbol}][{tf}] LiquidityCluster ERROR → soft-pass",
                    signal.Symbol,
                    tf);

                // fail-safe: never block on cluster exception
                LastBlockedByLiquidity = false;
                return FastFailResult.Ok();
            }

            // ------------------------------------------------------------------
            // 3) Cluster returned NULL → assess risk via AI strictness
            // ------------------------------------------------------------------
            if (after == null)
            {
                var w = 1.0m;
                try
                {
                    w = _aiLearning.GetGateMultiplier(signal.Symbol, smart.BaseRegime, "LIQ");
                }
                catch { /* non-critical */ }

                // Clamp AI multiplier to sane range
                w = Math.Clamp(w, 0.7m, 1.5m);

                // Block ONLY if:
                // - not relaxed
                // - not super-signal
                // - AI says "strict"
                if (!relaxLiquidity && !signal.IsSuperSignal && w >= 1.0m)
                {
                    _engineState.LastEntryDecision = "BLOCKED_LIQ_CLUSTER";
                    _engineState.BlockedByLiquidity = true;
                    _engineState.LiquidityReason = "ClusterDanger";
                    LastBlockedByLiquidity = true;

                    return FastFailResult.Fail("LIQ", "Liquidity cluster block");
                }

                // Otherwise allow (soft-pass)
                LastBlockedByLiquidity = false;
                return FastFailResult.Ok();
            }

            // ------------------------------------------------------------------
            // 4) Cluster adjusted signal → accept mutation
            // ------------------------------------------------------------------
            signal.CopyFrom(after);
            LastBlockedByLiquidity = false;

            return FastFailResult.Ok();
        }


        private FastFailResult Gate7_Exposure(
      string symbol,
      KlineInterval tf,
      TradeSignal signal,
      SmartRegimeInfo smart)
        {
            var es = _engineState;
            if (es == null || es.EquityUsd <= 0)
                return FastFailResult.Ok();

            // 🔹 AI multiplier
            var w = 1.0m;
            try { w = _aiLearning.GetGateMultiplier(symbol, smart.BaseRegime, "EXPO"); } catch { }
            w = Math.Clamp(w, 0.7m, 1.3m);

            // 🔹 Edge score
            var aiEdgeScore = Math.Clamp(smart.Confidence * w, 0.0m, 1.0m);

            // 🔹 Exposure decision
            var res = CanIncreaseExposure(
                state: es,
                symbol: symbol,
                symbolNotionalUsd: 0m,
                equityUsd: es.EquityUsd,
                usedMarginUsd: es.UsedMarginUsd,
                aiEdgeScore: aiEdgeScore,
                isSpecialSetup: signal.IsSuperSignal,
                isHighVolatility: smart.VolatilityPercent >= 0.015m,
                isLowEquityMode: es.EquityUsd < 500m
            );

            // 🔹 BLOCK handling
            if (!res.AllowAdd)
            {
                _engineState.LastEntryDecision = "BLOCKED_EXPOSURE";
                CurrentMode = "Blocked:EXPO";

                try
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
                }
                catch { }

                return FastFailResult.Fail("EXPO", res.Reason);
            }

            return FastFailResult.Ok();
        }
        //==============================================BTC HTF BLOCK


        public enum MarketProfileType
        {
            Default = 0,
            BtcMacro = 1
        }

        private static MarketProfileType GetProfile(string symbol)
        {
            if (string.Equals(symbol, "BTCUSDT", StringComparison.OrdinalIgnoreCase))
                return MarketProfileType.BtcMacro;

            if (string.Equals(symbol, "ETHUSDT", StringComparison.OrdinalIgnoreCase))
                return MarketProfileType.BtcMacro;

            return MarketProfileType.Default;
        }

        private sealed record BtcHtfState
        {
            public DateTime UpdatedUtc { get; init; }
            public KlineInterval Tf { get; init; }

            // direction / bias
            public decimal Ema50Slope { get; init; }        // normalized slope (ema50_now - ema50_prev)/ema50_prev
            public int BiasDir { get; init; }              // +1 long, -1 short, 0 neutral

            // squeeze / compression
            public decimal AtrPct { get; init; }           // atr14 / price
            public decimal AvgRange20 { get; init; }       // avg (high-low) 20 bars
            public decimal SqueezeScore { get; init; }     // 0..1 (higher = tighter)

            // risk flags
            public bool Valid { get; init; }
            public string Note { get; init; } = "";
        }
        private readonly ConcurrentDictionary<(string symbol, KlineInterval tf), BtcHtfState> _btcHtfCache = new();
        private readonly ConcurrentDictionary<(string symbol, KlineInterval tf), SemaphoreSlim> _btcHtfLocks = new();

        private static TimeSpan GetHtfTtl(KlineInterval tf) => tf switch
        {
            KlineInterval.FourHour => TimeSpan.FromMinutes(25),   // чаще, чем 4h close, но не спам
            KlineInterval.OneDay => TimeSpan.FromMinutes(90),
            _ => TimeSpan.FromMinutes(30)
        };
        private async Task<BtcHtfState> GetOrUpdateBtcHtfStateAsync(
    string symbol,
    KlineInterval tf,
    CancellationToken ct)
        {
            var key = (symbol, tf);
            var now = DateTime.UtcNow;

            if (_btcHtfCache.TryGetValue(key, out var cached))
            {
                var ttl = GetHtfTtl(tf);
                if ((now - cached.UpdatedUtc) <= ttl && cached.Valid)
                    return cached;
            }

            var gate = _btcHtfLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // re-check after lock
                if (_btcHtfCache.TryGetValue(key, out cached))
                {
                    var ttl = GetHtfTtl(tf);
                    if ((now - cached.UpdatedUtc) <= ttl && cached.Valid)
                        return cached;
                }

                var md = _marketData;
                if (md == null)
                {
                    var st0 = new BtcHtfState
                    {
                        UpdatedUtc = now,
                        Tf = tf,
                        Valid = false,
                        Note = "marketData=null"
                    };
                    _btcHtfCache[key] = st0;
                    return st0;
                }

                // HTF klines (централизованно через MarketDataFacade)
                var k = await md.GetKlinesAsync(symbol, tf, need: 220).ConfigureAwait(false);
                if (k == null || k.Count < 120)
                {
                    var st1 = new BtcHtfState
                    {
                        UpdatedUtc = now,
                        Tf = tf,
                        Valid = false,
                        Note = $"bars={k?.Count ?? 0}<120"
                    };
                    _btcHtfCache[key] = st1;
                    return st1;
                }

                int last = k.Count - 1;

                // price proxy
                var price = k[last].ClosePrice;
                if (price <= 0)
                {
                    var st2 = new BtcHtfState
                    {
                        UpdatedUtc = now,
                        Tf = tf,
                        Valid = false,
                        Note = "price<=0"
                    };
                    _btcHtfCache[key] = st2;
                    return st2;
                }

                // ATR% (как proxy волатильности HTF)
                var atr = Atr(k, 14, last);
                var atrPct = atr > 0 ? (atr / price) : 0m;

                // EMA50 slope (12 баров назад на HTF)
                var ema50 = Ema(k, 50, last);
                var ema50Prev = Ema(k, 50, Math.Max(0, last - 12));
                var slope = (ema50Prev != 0) ? (ema50 - ema50Prev) / ema50Prev : 0m;

                // avg range 20 bars
                decimal avgRange20 = 0m;
                int rStart = Math.Max(0, last - 19); // включаем last 20 свечей
                int rBars = 0;
                for (int i = rStart; i <= last; i++)
                {
                    avgRange20 += (k[i].HighPrice - k[i].LowPrice);
                    rBars++;
                }
                avgRange20 = rBars > 0 ? avgRange20 / rBars : 0m;

                // squeeze score: tighter => closer to 1
                // score based on (avgRange20 / atr) and atrPct
                decimal rOverAtr = (atr > 0) ? (avgRange20 / atr) : 99m;

                decimal atrOverRangeThreshold = 0.7m;
                decimal atrPctThreshold = 0.01m;

                decimal s1 = 1m - Math.Clamp((rOverAtr - atrOverRangeThreshold) / 0.60m, 0m, 1m);
                decimal s2 = 1m - Math.Clamp((atrPct - atrPctThreshold) / atrPctThreshold, 0m, 1m);
                decimal squeezeScore = Math.Clamp(0.6m * s1 + 0.4m * s2, 0m, 1m);

                // bias dir
                decimal emaSlopeThreshold = tf switch
                {
                    KlineInterval.OneDay => 0.0008m,
                    KlineInterval.FourHour => 0.0012m,
                    _ => 0.001m
                };

                int dir = slope >= emaSlopeThreshold ? +1 : slope <= -emaSlopeThreshold ? -1 : 0;

                var st = new BtcHtfState
                {
                    UpdatedUtc = now,
                    Tf = tf,
                    Ema50Slope = slope,
                    BiasDir = dir,
                    AtrPct = atrPct,
                    AvgRange20 = avgRange20,
                    SqueezeScore = squeezeScore,
                    Valid = true,
                    Note = "OK"
                };

                _btcHtfCache[key] = st;
                return st;
            }
            finally
            {
                gate.Release();
            }
        }
        private async Task<TradeSignal?> TryBtcRegimeExpansionAsync(
    string symbol,
    KlineInterval tf,
    IReadOnlyList<BinanceFuturesUsdtKline> klines,
    CancellationToken ct)
        {
            // BTC only
            if (!string.Equals(symbol, "BTCUSDT", StringComparison.OrdinalIgnoreCase))
                return null;

            // Entry TF allowed
          //  if (tf != KlineInterval.FifteenMinutes && tf != KlineInterval.OneHour)  //TODO:   
           //     return null;

            if (klines == null || klines.Count < 75)
                return null;

            int last = klines.Count - 1;
            if (last < 30) return null;

            var c = klines[last];
            var prev = klines[last - 1];

            // ATR
            decimal atr = Atr(klines, 14, last);
            if (atr <= 0) return null;

            decimal price = c.ClosePrice;
            if (price <= 0) return null;

            // ===================== HTF STATE (4H + 1D) =====================
            var h4 = await GetOrUpdateBtcHtfStateAsync(symbol, KlineInterval.FourHour, ct).ConfigureAwait(false);
            var d1 = await GetOrUpdateBtcHtfStateAsync(symbol, KlineInterval.OneDay, ct).ConfigureAwait(false);
            if (!h4.Valid || !d1.Valid) return null;

            // ---- HTF squeeze / eligibility ----
            // thresholds tuned for your screenshots regime
            bool htfSqueezeOk = h4.SqueezeScore >= 0.62m && h4.AtrPct <= 0.014m;
            bool d1StableOk = d1.AtrPct <= 0.022m;

            if (!htfSqueezeOk || !d1StableOk)
                return null;

            // ---- Direction bias (HTF) ----
            // Primary bias = H4, D1 acts as veto only if strongly opposite
            int bias = h4.BiasDir;
            if (bias == 0) return null;

            if (d1.BiasDir != 0 && Math.Sign(d1.BiasDir) != Math.Sign(bias))
            {
                // daily strongly opposite -> skip
                return null;
            }

            // ===================== EXPANSION BAR (LTF) =====================
            // 1) real impulse body
            decimal body = Math.Abs(c.ClosePrice - c.OpenPrice);
            decimal bodyThreshold = h4.SqueezeScore >= 0.75m
     ? atr * 1.20m     // если сильный squeeze — разрешаем раньше
     : atr * 1.30m;    // иначе почти как было

            if (body < bodyThreshold)
                return null;

            // 2) anti monster candle
            if (IsTooBigImpulseBar(c, prev, atr))
                return null;

            // 3) volume shock
            decimal avgVol20 = 0m;
            int vs = Math.Max(1, last - 20);
            int vb = 0;
            for (int i = vs; i < last; i++)
            {
                avgVol20 += klines[i].Volume;
                vb++;
            }
            avgVol20 = vb > 0 ? avgVol20 / vb : 0m;
            if (avgVol20 <= 0) return null;

            decimal volMultiplierStrongSqueeze = 1.85m;
            decimal volMultiplierNormal = 1.95m;
            decimal volMult = h4.SqueezeScore >= 0.75m ? volMultiplierStrongSqueeze : volMultiplierNormal;

            if (c.Volume < avgVol20 * volMult)
                return null;

            // 4) close near extreme
            decimal range = c.HighPrice - c.LowPrice;
            if (range <= 0) return null;

            bool closeNearHigh = (c.HighPrice - c.ClosePrice) <= range * 0.22m;
            bool closeNearLow = (c.ClosePrice - c.LowPrice) <= range * 0.22m;

            // 5) breakout confirmation vs last 20 range
            int from = Math.Max(0, last - 20);
            int count = last - from;   // исключаем текущую свечу

            if (count < 5) return null; // защита от мусора

            var period = klines.Skip(from).Take(count);

            int hiLoStart = Math.Max(0, last - 20);
            decimal hi20 = klines[hiLoStart].HighPrice;
            decimal lo20 = klines[hiLoStart].LowPrice;

            for (int i = hiLoStart + 1; i <= last - 1; i++)
            {
                hi20 = Math.Max(hi20, klines[i].HighPrice);
                lo20 = Math.Min(lo20, klines[i].LowPrice);
            }

            decimal breakoutBuffer = h4.SqueezeScore >= 0.75m
                ? atr * 0.08m
                : atr * 0.12m;

            bool longBreak = c.ClosePrice >= (hi20 + breakoutBuffer) && closeNearHigh;
            bool shortBreak = c.ClosePrice <= (lo20 - breakoutBuffer) && closeNearLow;

            // Directional gate: only trade in HTF bias direction
            if (bias > 0 && !longBreak) return null;
            if (bias < 0 && !shortBreak) return null;
            // ===================== OVEREXTENSION FILTER =====================
            // не входим если уже слишком далеко от диапазона

            decimal overextensionThreshold = tf switch
            {
                KlineInterval.FiveMinutes => 0.4m,
                KlineInterval.FifteenMinutes => 0.5m,
                KlineInterval.OneHour => 0.6m,
                KlineInterval.FourHour => 0.7m,
                _ => 0.6m
            };

            if (bias > 0 && c.ClosePrice - hi20 > atr * overextensionThreshold) return null;
            if (bias < 0 && lo20 - c.ClosePrice > atr * overextensionThreshold) return null;
            // ===================== BUILD SIGNAL (BTC profile) =====================
            // BTC stops are wider. Use tf-aware multipliers.
            var (slMult, tp1Mult, tp2Mult, tp3Mult) = tf switch
            {
                KlineInterval.FifteenMinutes => (1.6m, 1.4m, 2.2m, 3.2m),
                KlineInterval.OneHour => (2.0m, 1.6m, 2.6m, 3.8m),
                _ => (1.8m, 1.5m, 2.4m, 3.4m)
            };

            if (longBreak)
            {
                decimal entry = c.ClosePrice;                 // fast entry
                decimal sl = c.LowPrice - atr * slMult;       // wide btc stop

                var s = new TradeSignal
                {
                    Symbol = symbol,
                    Side = SignalSide.Buy,
                    EntryPrice = entry,
                    StopLoss = sl,
                    Atr = atr,
                    Timeframe = tf.ToString(),
                    Time = c.CloseTime,
                    Reason = "BTC_REGIME_EXPANSION_LONG",
                    IsSuperSignal = true,
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

            if (shortBreak)
            {
                decimal entry = c.ClosePrice;
                decimal sl = c.HighPrice + atr * slMult;

                var s = new TradeSignal
                {
                    Symbol = symbol,
                    Side = SignalSide.Sell,
                    EntryPrice = entry,
                    StopLoss = sl,
                    Atr = atr,
                    Timeframe = tf.ToString(),
                    Time = c.CloseTime,
                    Reason = "BTC_REGIME_EXPANSION_SHORT",
                    IsSuperSignal = true,
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
        private async Task<SignalDecisionTrace> AllowImmediatelyAsync(
     SignalDecisionTrace trace,
     TradeSignal signal,
     SmartRegimeInfo smart,
     string reason,
     string symbol,
     KlineInterval tf,
     IReadOnlyList<BinanceFuturesUsdtKline> klines,
     bool relaxRr,
     bool relaxLiquidity,
     CancellationToken ct)
        {
            var cfg = _confidenceCfg.Resolve(symbol);

            // 🔹 Привязываем confidence один раз
            signal.Confidence = Math.Max(smart.Confidence, cfg.MinEntry + 0.05m);

            _engineState.LastEntryDecision = reason;
            CurrentMode = reason;

            // --- RR gate
            trace.Add(Gate4_RR(symbol, tf, signal, smart, relaxRr));
            if (!trace.Allow) return Finalize(trace, smart);

            // --- Liquidity gate
            var g6 = await Gate6_LiquidityAsync(signal, smart, klines, tf, relaxLiquidity, ct).ConfigureAwait(false);
            trace.Add(g6);
            if (!trace.Allow) return Finalize(trace, smart);

            // --- Exposure gate
            trace.Add(Gate7_Exposure(symbol, tf, signal, smart));
            if (!trace.Allow) return Finalize(trace, smart);

            // ✅ Всё ок, разрешаем вход
            trace.Allow = true;
            trace.Signal = signal;

            return Finalize(trace, smart);
        }

        //===========================================================================END BTC HTF BLOCK


        // ----------------------------- MAIN EVALUATOR (ASYNC FINAL) -----------------------------
        internal async Task<SignalDecisionTrace> EvaluateSignalAsync(
            string symbol,
            KlineInterval tf,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            CancellationToken ct)
        {
            var trace = new SignalDecisionTrace { Allow = true };

            SmartRegimeInfo? smart = null;
            TradeSignal? baseSignal = null;

            // reset engine-state per decision (safe)
            _engineState.LastDecisionTime = DateTime.UtcNow;
            _engineState.LastEntryDecision = "EVALUATING";
            _engineState.BlockedByLiquidity = false;
            _engineState.SoftEntry = false;
            LastSoftEntry = false;
            LastBlockedByLiquidity = false;
            CurrentMode = "Detecting";

            // config flags
            bool testMode = _test.Enabled;
            bool relaxRr = testMode && _test.RelaxRR;
            bool relaxPatternBlock = testMode && _test.RelaxPatternBlock;
            bool relaxLiquidity = testMode && _test.RelaxLiquidity;
            bool lowerRegimeThreshold = testMode && _test.LowerRegimeThreshold;

            try
            {
                // Gate0
                var g0 = Gate0_Data(symbol, tf, klines);
                trace.Add(g0);
                if (!g0.Allow) return Finalize(trace, null);

                try
                {
                    var last = klines[^1];

                    _decisionMarkers.Add(new DecisionMarkerDto
                    {
                        Symbol = symbol,
                        Timeframe = tf.ToString(),
                        CandleTimeUtc = last.CloseTime,
                        Type = DecisionMarkerType.Evaluated,
                        Code = "EVALUATED",
                        Details = "Signal evaluated",
                    });
                }
                catch { /* never block engine */ }

                // Gate1
                SmartRegimeInfo s;
                var g1 = Gate1_SmartRegime(symbol, tf, klines, out s);
                smart = s;
                trace.Add(g1);
                if (!g1.Allow) return Finalize(trace, smart);

               

                // ---------------- BTC MACRO PROFILE (independent) ----------------
                if (GetProfile(symbol) == MarketProfileType.BtcMacro)
                {
                    // BTC expansion is a special independent branch:
                    // it MUST NOT go through Gate3_BaseSignal patterns.
                    var btcSig = await TryBtcRegimeExpansionAsync(symbol, tf, klines, ct).ConfigureAwait(false);
                    if (btcSig != null)
                    {
                        btcSig.IsSuperSignal = true;
                        return await AllowImmediatelyAsync(
                            trace, btcSig, smart,
                            reason: "BTC_REGIME_EXPANSION",
                            symbol: symbol, tf: tf, klines: klines,
                            relaxRr: relaxRr,
                            relaxLiquidity: relaxLiquidity,
                            ct: ct).ConfigureAwait(false);
                    }
                }

                var cfg = _confidenceCfg.Resolve(symbol);
                // Gate2 (PRE-CHECK / TELEMETRY ONLY)
                var g2Pre = Gate2_Confidence(smart, lowerRegimeThreshold, symbol);
                trace.Add(g2Pre);

                // ===== EARLY SPECIAL SETUP: VOLATILITY EXPANSION =====
                if (IsVolatilityExpansionAllowed(smart))
                {
                    var veSignal = TryVolatilityExpansionEntry(symbol, tf, klines);
                    if (veSignal != null)
                    {
                        // 🔒 помечаем как SPECIAL / SUPER
                        veSignal.IsSuperSignal = true;

                        // ⚠️ confidence берём минимально допустимый
                        veSignal.Confidence = Math.Max(
                            smart.Confidence,
                            cfg.MinEntry + 0.05m
                        );

                        _engineState.LastEntryDecision = "VOLATILITY_EXPANSION";
                        CurrentMode = "VolatilityExpansion";

                        // 👉 сразу в Gates 4..7 (пропускаем BasePattern)
                        trace.Allow = true;
                        trace.Signal = veSignal;

                        // RR / Liquidity / Exposure — ОБЯЗАТЕЛЬНЫ
                        trace.Add(Gate4_RR(symbol, tf, veSignal, smart, relaxRr));
                        if (!trace.Allow) return Finalize(trace, smart);

                        trace.Add(await Gate6_LiquidityAsync(
                            veSignal, smart, klines, tf, relaxLiquidity, ct));
                        if (!trace.Allow) return Finalize(trace, smart);

                        trace.Add(Gate7_Exposure(symbol, tf, veSignal, smart));
                        if (!trace.Allow) return Finalize(trace, smart);

                        return Finalize(trace, smart);
                    }
                }

                // ✅ Gate3 — генерируем базовый сигнал
                var g3 = Gate3_BaseSignal(symbol, tf, klines, smart, out baseSignal);
                trace.Add(g3);
                if (!g3.Allow || baseSignal == null)
                    return Finalize(trace, smart);

                // --- исключаем 1m
                if (tf == KlineInterval.OneMinute)
                {
                    _engineState.LastEntryDecision = "SKIP_1M_DECISION";
                    CurrentMode = "1M Skip";
                    trace.Add(FastFailResult.Fail("TF", "1m excluded from decision logic"));
                    trace.Signal = null;
                    trace.Allow = false;
                    return Finalize(trace, smart);
                }

                // --- теперь baseSignal точно не null, вычисляем confidence
                var conf = _confidenceAgg.Evaluate(smart, baseSignal, tf);

                // --- привязываем финальный confidence один раз
                baseSignal.Confidence = Math.Max(conf.Final, smart.Confidence);
                _engineState.ConfidenceRaw = (decimal)baseSignal.Confidence;
                _engineState.ConfidencePercent = (int)(baseSignal.Confidence * 100);

                // обновляем глобальный Confidence
                Confidence = baseSignal.Confidence;



                // NEW Gate2 ConfidenceFinal
                trace.Add(Gate2_ConfidenceHybrid(conf.Final, smart, lowerRegimeThreshold, symbol));
                if (!trace.Allow) return Finalize(trace, smart);




                // 🚨 НОВЫЙ ЖЁСТКИЙ ФИЛЬТР КОНЦА ТРЕНДА
                trace.Add(Gate2_5_TrendPhaseLock(klines, smart, baseSignal));
                if (!trace.Allow) return Finalize(trace, smart);

                // CRITICAL: bind confidence at entry
              //  baseSignal.Confidence = smart.Confidence;

                // канон:
                trace.Add(Gate3_5_DirectionLock(symbol, tf, klines, baseSignal, smart, allowCounterTrendInRangeLike: true));
                if (!trace.Allow) return Finalize(trace, smart);

                trace.Add(Gate3_2_LateEntryFilter(symbol, tf, klines, baseSignal, smart));
                if (!trace.Allow) return Finalize(trace, smart);


                // Gate4..5
                trace.Add(Gate4_RR(symbol, tf, baseSignal, smart, relaxRr));
                if (!trace.Allow) return Finalize(trace, smart);

                trace.Add(Gate5_Pattern(symbol, tf, klines, baseSignal, relaxPatternBlock));
                if (!trace.Allow) return Finalize(trace, smart);

                // Gate6 async
                var g6 = await Gate6_LiquidityAsync(baseSignal, smart, klines, tf, relaxLiquidity, ct).ConfigureAwait(false);
                trace.Add(g6);
                if (!g6.Allow)
                {
                    try
                    {
                        var c = klines[^1];

                        _decisionMarkers.Add(new DecisionMarkerDto
                        {
                            Symbol = baseSignal.Symbol,
                            Timeframe = tf.ToString(),
                            CandleTimeUtc = c.CloseTime,
                            Type = DecisionMarkerType.LowVolume,
                            Code = g6.Reason switch
                            {
                                "LIQ_GUARD" => "LIQ_GUARD",
                                "ClusterDanger" => "LIQ_CLUSTER",
                                _ => "LIQ_BLOCK"
                            },

                            Details = g6.Reason,
                            Metrics = new Dictionary<string, decimal>
                            {
                                ["confidence"] = smart.Confidence,
                                ["riskBias"] = smart.RiskBias
                            }
                        });
                    }
                    catch { }

                    return Finalize(trace, smart);
                }


                // Gate7 exposure
                trace.Add(Gate7_Exposure(symbol, tf, baseSignal, smart));
                if (!trace.Allow) return Finalize(trace, smart);

                // allowed
                trace.Allow = true;
                trace.Signal = baseSignal;

                _engineState.LastEntryDecision = "ENTER_ALLOWED";
                _engineState.ConfidenceRaw = smart.Confidence;
                _engineState.ConfidencePercent = (int)(smart.Confidence * 100);
                _engineState.ConfidenceLevel =
                    smart.Confidence >= cfg.Bands.HighFrom ? "HIGH" :
    smart.Confidence >= cfg.Bands.MediumFrom ? "MEDIUM" :
    smart.Confidence >= cfg.MinEntry ? "LOW" :
    "BELOW_ENTRY";

                CurrentMode = "Allowed";

                try
                {
                    var c = klines[^1];

                    _decisionMarkers.Add(new DecisionMarkerDto
                    {
                        Symbol = symbol,
                        Timeframe = tf.ToString(),
                        CandleTimeUtc = c.CloseTime,
                        Type = DecisionMarkerType.EntryAllowed,
                        Code = "ENTRY_ALLOWED",
                        Details =
                            $"{baseSignal.Reason} conf={smart.Confidence:P0}",
                        Metrics = new Dictionary<string, decimal>
                        {
                            ["confidence"] = smart.Confidence,
                            ["riskBias"] = smart.RiskBias
                        }
                    });
                }
                catch { }


                return Finalize(trace, smart);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[STRAT][{symbol}][{tf}] EvaluateSignalAsync error", symbol, tf);
                trace.Allow = false;
                trace.Signal = null;
                trace.Gates.Add(FastFailResult.Fail("ENGINE", "Evaluate exception"));
                return Finalize(trace, smart);
            }
        }

        private SignalDecisionTrace Finalize(SignalDecisionTrace trace, SmartRegimeInfo? smart)
        {
            if (trace.FailedGate != null)
            {
                trace.Allow = false;
                trace.Signal = null;
            }

            // DecisionTrace must match REAL decision; only if we have smart/regime
            if (smart != null)
            {
                try
                {
                    _aiLearning.RecordDecisionTrace(
                        trace.Signal?.Symbol ?? "UNKNOWN",
                        smart.BaseRegime,
                        trace.Gates);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AI] RecordDecisionTrace failed");
                }
            }

            return trace;
        }

        // ----------------------------- EXPOSURE DECISION (UNCHANGED LOGIC) -----------------------------
        private ExposureDecision CanIncreaseExposure(
      EngineState state,
      string symbol,
      decimal symbolNotionalUsd,
      decimal equityUsd,
      decimal usedMarginUsd,
      decimal aiEdgeScore,
      bool isSpecialSetup,
      bool isHighVolatility,
      bool isLowEquityMode)
        {
            if (equityUsd <= 0m)
                return new ExposureDecision { AllowAdd = true, UseProfitBucket = false, AllowedAddUsd = 0m, Reason = "ALLOW: exposure-skip", SymbolCapPct = 0m };

            var sKey = EngineState.Key(symbol);
            var st = state.Symbols.GetOrAdd(sKey, _ => new SymbolState());

            if (st.BucketDayUtc != DateTime.UtcNow.Date)
            {
                st.BucketDayUtc = DateTime.UtcNow.Date;
                st.RealizedPnlBucketUsd = 0m;
                st.HarvestsToday = 0;
                st.AddsToday = 0;
            }

            decimal baseCap = st.DefaultSymbolCapPct;
            if (isLowEquityMode) baseCap = Math.Min(0.35m, baseCap + 0.10m);
            if (isHighVolatility) baseCap = Math.Max(0.10m, baseCap - 0.06m);

            decimal cap = st.CapBoostUntilUtc > DateTime.UtcNow ? Math.Max(baseCap, st.CurrentSymbolCapPct) : baseCap;

            decimal maxUsedMarginPct = isLowEquityMode ? 0.70m : 0.55m;
            if (isHighVolatility) maxUsedMarginPct -= 0.08m;

            if (usedMarginUsd / equityUsd >= maxUsedMarginPct)
                return new ExposureDecision { AllowAdd = false, UseProfitBucket = false, AllowedAddUsd = 0m, Reason = $"BLOCK: usedMarginPct={usedMarginUsd / equityUsd:P0} >= {maxUsedMarginPct:P0}", SymbolCapPct = cap };

            bool capHit = (symbolNotionalUsd / equityUsd) >= cap;
            bool allowNoProfitAveraging = isSpecialSetup && aiEdgeScore >= 0.78m;
            decimal fromBucketUsd = Math.Max(0m, st.RealizedPnlBucketUsd * 0.55m);
            decimal maxAddUsd = 0m;

            if (capHit && !allowNoProfitAveraging)
                return new ExposureDecision { AllowAdd = false, UseProfitBucket = false, AllowedAddUsd = 0m, Reason = $"BLOCK: symbolCapHit {symbolNotionalUsd / equityUsd:P0} >= {cap:P0}", SymbolCapPct = cap };

            if (capHit && allowNoProfitAveraging)
                maxAddUsd = isHighVolatility ? equityUsd * 0.015m : equityUsd * 0.025m;
            else if (fromBucketUsd >= 5m)
                maxAddUsd = Math.Min(fromBucketUsd, equityUsd * (isHighVolatility ? 0.02m : 0.04m));
            else if (allowNoProfitAveraging)
                maxAddUsd = equityUsd * (isHighVolatility ? 0.015m : 0.03m);
            else
                return new ExposureDecision { AllowAdd = false, UseProfitBucket = false, AllowedAddUsd = 0m, Reason = "BLOCK: no bucket and not special-setup", SymbolCapPct = cap };

            return new ExposureDecision
            {
                AllowAdd = true,
                UseProfitBucket = fromBucketUsd >= 5m,
                AllowedAddUsd = Math.Max(0m, maxAddUsd),
                Reason = capHit ? $"ALLOW: special-setup no-profit add (capHit) edge={aiEdgeScore:F2}" : $"ALLOW: add from profit bucket/edge={aiEdgeScore:F2}",
                SymbolCapPct = cap
            };
        }

        // ----------------------------- STOP COOLDOWN -----------------------------
        public static void RegisterStop(string symbol, SignalSide side)
        {
            _lastStopTime[(symbol, side)] = DateTime.UtcNow;

            var cutoff = DateTime.UtcNow.AddHours(-6);
            foreach (var kv in _lastStopTime.Keys)
            {
                if (_lastStopTime[kv] < cutoff)
                    _lastStopTime.TryRemove(kv, out _);
            }
        }

    }
}
