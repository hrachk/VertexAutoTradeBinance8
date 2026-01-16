//  -----------------------------------------------------------------------------
//   STRATEGY ENGINE v6.5 FINAL (PRODUCTION)
//   - Reactive async pipeline (NO .GetResult / NO deadlocks)
//   - Per-key singleflight with TTL cleanup
//   - Deterministic Gate pipeline + DecisionTrace consistency
//   - Compatible signatures/events for VertexAutoTradeBinance8
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
        private readonly SignalConfidenceSettings _confidenceCfg;

        // UI flags
        public string CurrentMode { get; private set; } = "Detecting";
        public bool LastSoftEntry { get; private set; }
        public bool LastBlockedByLiquidity { get; private set; }
        public decimal? Confidence { get; set; } // 0..1

        public event Action<TradeSignal>? OnSignalGenerated;

        private EngineState _engineState => _stateSvc.State;

        private MarketDataFacade? _marketData;

        // какие TF реагируют мгновенно
        private static readonly KlineInterval[] ReactiveTf =
        {
            KlineInterval.OneMinute,
            KlineInterval.FiveMinutes
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
        }

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
            SignalConfidenceSettings confidenceCfg)
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

        public void BindReactive(MarketDataFacade marketData)
        {
            _marketData = marketData;

            marketData.OnWarm += (symbol, tf) =>
            {
                _logger.LogInformation("[STRAT][WARM] market warm confirmed {symbol} {tf}", symbol, tf);
            };

            marketData.WsClosedKline += (symbol, tf, candle) =>
            {
                if (ReactiveTf.Contains(tf))
                    RunReactive(symbol, tf, "CLOSE");
            };

            _logger.LogInformation("[STRAT][PUSH] Reactive entry-point bound");
        }

        // ----------------------------- REACTIVE (PRODUCTION) -----------------------------
        private void RunReactive(string symbol, KlineInterval interval, string reason)
        {
            var md = _marketData;
            if (md == null) return;

            var key = $"{symbol}:{interval}";
            var now = DateTime.UtcNow;

            // Warmup gate (snapshot overrides)
            if (!md.HasSnapshotState && md.IsInWarmup(symbol, interval))
            {
                _logger.LogDebug("[STRAT][PUSH][{symbol}][{tf}] skip — market warmup", symbol, interval);
                return;
            }

            // Anti-spam (CLOSE always allowed)
            if (reason != "CLOSE")
            {
                if (_lastReactiveRun.TryGetValue(key, out var last) &&
                    (now - last).TotalMilliseconds < 300)
                    return;
            }

            _lastReactiveRun[key] = now;

            // per-key singleflight
            var le = _reactiveLocks.GetOrAdd(key, _ => new LockEntry { LastUsedUtc = now });
            le.LastUsedUtc = now;

            // opportunistic cleanup (every ~60 sec)
            TryCleanupReactiveLocks(now);

            _ = Task.Run(async () =>
            {
                // 0ms wait = drop if already running same key
                if (!await le.Gate.WaitAsync(0).ConfigureAwait(false))
                    return;

                try
                {
                    var klines = await md.GetKlinesAsync(symbol, interval, need: 120).ConfigureAwait(false);
                    if (klines == null || klines.Count < 30)
                        return;

                    _logger.LogDebug(
                        "[STRAT][PUSH][{symbol}][{tf}] run reason={reason} bars={bars}",
                        symbol, interval, reason, klines.Count);

                    var decision = await EvaluateSignalAsync(symbol, interval, klines, CancellationToken.None)
                        .ConfigureAwait(false);

                    // DecisionTrace ALWAYS
                    SafeRecordDecisionTrace(symbol, interval, decision);

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
                    if (signal == null) return;

                    try
                    {
                        OnSignalGenerated?.Invoke(signal);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "[STRAT][PUSH][{symbol}][{tf}] OnSignalGenerated handler failed",
                            symbol, interval);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "[STRAT][PUSH][{symbol}][{tf}] reactive error",
                        symbol, interval);
                }
                finally
                {
                    le.Gate.Release();
                }
            });
        }

        private void TryCleanupReactiveLocks(DateTime nowUtc)
        {
            // Cleanup at most once per ~60 seconds
            var tick = nowUtc.Ticks;
            var prev = Interlocked.Read(ref _lockCleanupTick);
            if (prev != 0 && (tick - prev) < TimeSpan.FromSeconds(60).Ticks)
                return;

            if (Interlocked.CompareExchange(ref _lockCleanupTick, tick, prev) != prev)
                return;

            var cutoff = nowUtc.AddMinutes(-15); // TTL: 15 minutes idle => cleanup
            foreach (var kv in _reactiveLocks)
            {
                if (kv.Value.LastUsedUtc < cutoff)
                {
                    // remove only if same instance (avoid races)
                    _reactiveLocks.TryRemove(kv.Key, out _);
                }
            }
        }

        private void SafeRecordDecisionTrace(string symbol, KlineInterval tf, SignalDecisionTrace decision)
        {
            try
            {
                _decisionTrace.Record(new DecisionTraceSnapshot
                {
                    Symbol = symbol,
                    Timeframe = tf.ToString(),
                    Allow = decision.Allow,
                    FailedGate = decision.FailedGate?.Gate,
                    Reason = decision.FailedGate?.Reason,
                    Time = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DECISION][{symbol}][{tf}] DecisionTrace.Record failed", symbol, tf);
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

        private static void NormalizeEntryAndSl(TradeSignal s)
        {
            if (s.EntryPrice <= 0 || s.StopLoss <= 0) return;

            decimal dist = Math.Abs(s.EntryPrice - s.StopLoss);
            decimal minDist = (s.Atr.HasValue && s.Atr.Value > 0)
                ? s.Atr.Value * 0.30m
                : s.EntryPrice * 0.001m;

            if (dist >= minDist) return;

            if (s.Side == SignalSide.Buy) s.StopLoss = s.EntryPrice - minDist;
            else if (s.Side == SignalSide.Sell) s.StopLoss = s.EntryPrice + minDist;
        }

        // ----------------------------- SIGNAL PATTERNS -----------------------------
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

            bool bull = c.ClosePrice > c.OpenPrice && c.LowPrice <= ema && c.ClosePrice > ema;
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

            bool bear = c.ClosePrice < c.OpenPrice && c.HighPrice >= ema && c.ClosePrice < ema;
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

        // ----------------------------- REGIME/CONF HELPERS -----------------------------
        private static int GetAdaptiveThreshold(MarketRegime baseRegime, SmartRegimeType smartType, decimal volatility, decimal slope)
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

            if (isRangeLike) threshold = 35;
            else if (isStrongTrendLike) threshold = 60;
            else if (isTrendLike) threshold = 45;
            else threshold = 45;

            if (volatility < 0.10m) threshold -= 10;
            else if (volatility > 0.30m) threshold += 10;

            if (Math.Abs(slope) > 0.7m) threshold += 5;

            if (threshold < 25) threshold = 25;
            if (threshold > 80) threshold = 80;

            return threshold;
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
                if (!r.Allow) Allow = false;
            }
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
            smart = null!;
            try
            {
                smart = _smartRegimeService.Evaluate(symbol, tf, klines);

                // BASE market state (fail-safe)
                try
                {
                    var atr14 = Atr(klines, 14, klines.Count - 1);
                    _aiLearning.RecordMarketState(
                        symbol: symbol,
                        timeframe: tf.ToString(),
                        regime: smart.BaseRegime,
                        trendSlopePercent: smart.TrendSlopePercent,
                        volatilityPercent: smart.VolatilityPercent,
                        atr: atr14,
                        confidence: smart.Confidence
                    );
                }
                catch { /* non-critical */ }

                // HYBRID periodic snapshot (fail-safe)
                try
                {
                    var atr14 = Atr(klines, 14, klines.Count - 1);
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

                return FastFailResult.Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FAST][SMART] Evaluate failed {symbol} {tf}", symbol, tf);
                return FastFailResult.Fail("SMART", "Evaluate error");
            }
        }

        private FastFailResult Gate2_Confidence(SmartRegimeInfo smart, bool lowerRegimeThreshold)
        {
            if (smart.IsDangerChopZone)
                return FastFailResult.Fail("CONF", "DangerChopZone");

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

            // EngineState (UI)
            _engineState.LastEntryDecision = "CONF_CHECK";
            _engineState.ConfidenceRaw = smart.Confidence;
            _engineState.ConfidencePercent = (int)(smart.Confidence * 100);
            _engineState.ConfidenceLevel =
                smart.Confidence >= _confidenceCfg.Bands.HighFrom ? "HIGH" :
                smart.Confidence >= _confidenceCfg.MinEntry ? "MEDIUM" : "LOW";

            // HARD FLOOR
            if (smart.Confidence < _confidenceCfg.MinEntry)
                return FastFailResult.Fail("CONF", $"confidence={smart.Confidence:P0}<min={_confidenceCfg.MinEntry:P0}");

            // ADAPTIVE CHECK
            if (!fastTrendOverride && smart.Confidence < thrFrac - safetyBuffer)
                return FastFailResult.Fail("CONF", $"confidence={smart.Confidence:P0}<thr={adaptiveThreshold}% (buf={safetyBuffer:P0})");

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

            baseSignal = rangeLike
                ? (TryLiquidityGrab(symbol, tf, klines) ?? TryPullbackEma21(symbol, tf, klines))
                : TryPullbackEma21(symbol, tf, klines);

            _engineState.LastEntryDecision = "WAITING_PULLBACK";

            if (baseSignal == null)
                return FastFailResult.Fail("BASE", "no base pattern");

            // side-aware cooldown (same-side only)
            if (_lastStopTime.TryGetValue((symbol, baseSignal.Side), out var lastStop))
            {
                var diff = DateTime.UtcNow - lastStop;
                if (diff < TimeSpan.FromMinutes(10))
                    return FastFailResult.Fail("COOLDOWN", $"same-side cooldown {diff.TotalMinutes:F1}m");
            }

            // MICRO_SIGNAL trace (fail-safe)
            try
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
            catch { /* non-critical */ }

            return FastFailResult.Ok();
        }

        private FastFailResult Gate4_RR(string symbol, KlineInterval tf, TradeSignal signal, SmartRegimeInfo smart, bool relaxRr)
        {
            if (relaxRr) return FastFailResult.Ok();
            if (signal.TakeProfits == null || signal.TakeProfits.Count == 0) return FastFailResult.Ok();

            var slDist = Math.Abs(signal.EntryPrice - signal.StopLoss);
            var tpDist = Math.Abs(signal.TakeProfits[0] - signal.EntryPrice);

            if (slDist <= 0) return FastFailResult.Fail("RR", "slDist<=0");

            var rr = tpDist / slDist;
            var minRr = GetDynamicMinRr(symbol, tf, smart, signal);

            // AI Gate Weight (multiplier)
            var w = 1.0m;
            try
            {
                w = _aiLearning.GetGateMultiplier(symbol, smart.BaseRegime, "RR");
            }
            catch { /* non-critical */ }

            minRr *= w;

            if (rr < minRr)
                return FastFailResult.Fail("RR", $"rr={rr:F2}<min={minRr:F2} (w={w:F2})");

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
            // LiquidityGuard hard-block
            var lg = _liquidityGuardService.Analyze(
                symbol: signal.Symbol,
                interval: tf,
                klines: klines,
                side: signal.Side,
                superSignal: signal.IsSuperSignal);

            if (lg.Block && !relaxLiquidity)
            {
                _engineState.LastEntryDecision = "BLOCKED_LIQ_GUARD";
                _engineState.BlockedByLiquidity = true;
                _engineState.LiquidityReason = lg.Reason.ToString();
                LastBlockedByLiquidity = true;
                return FastFailResult.Fail("LIQ_GUARD", lg.Reason.ToString());
            }

            // LiquidityCluster async
            TradeSignal? after;
            try
            {
                after = await _liquidityClusterService.FilterAndAdjustAsync(signal, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return FastFailResult.Fail("CANCELLED", "Liquidity analysis cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[STRAT][{symbol}][{tf}] LiquidityCluster ERROR → soft-pass", signal.Symbol, tf);
                return FastFailResult.Ok(); // fail-safe
            }

            if (after == null)
            {
                // Use AI multiplier only as "strictness" gate, but never block when relaxLiquidity=true
                var w = 1.0m;
                try
                {
                    w = _aiLearning.GetGateMultiplier(signal.Symbol, smart.BaseRegime, "LIQ");
                }
                catch { /* non-critical */ }

                if (!relaxLiquidity && w >= 1.0m)
                {
                    _engineState.LastEntryDecision = "BLOCKED_LIQ_CLUSTER";
                    _engineState.BlockedByLiquidity = true;
                    _engineState.LiquidityReason = "ClusterDanger";
                    LastBlockedByLiquidity = true;
                    return FastFailResult.Fail("LIQ", "Liquidity cluster block");
                }

                return FastFailResult.Ok();
            }

            signal.CopyFrom(after);
            LastBlockedByLiquidity = false;
            return FastFailResult.Ok();
        }

        private FastFailResult Gate7_Exposure(string symbol, KlineInterval tf, TradeSignal signal, SmartRegimeInfo smart)
        {
            var es = _engineState;
            if (es == null || es.EquityUsd <= 0)
                return FastFailResult.Ok();

            var w = 1.0m;
            try
            {
                w = _aiLearning.GetGateMultiplier(symbol, smart.BaseRegime, "EXPO");
            }
            catch { /* non-critical */ }

            var res = CanIncreaseExposure(
                state: es,
                symbol: symbol,
                symbolNotionalUsd: 0m, // intentionally 0 here; executor/supervisor checks actual notional
                equityUsd: es.EquityUsd,
                usedMarginUsd: es.UsedMarginUsd,
                aiEdgeScore: smart.Confidence * w,
                isSpecialSetup: signal.IsSuperSignal,
                isHighVolatility: smart.VolatilityPercent >= 0.015m,
                isLowEquityMode: es.EquityUsd < 500m
            );

            if (!res.AllowAdd)
            {
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
                catch { /* non-critical */ }

                return FastFailResult.Fail("EXPO", res.Reason);
            }

            return FastFailResult.Ok();
        }

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

                // Gate1
                SmartRegimeInfo s;
                var g1 = Gate1_SmartRegime(symbol, tf, klines, out s);
                smart = s;
                trace.Add(g1);
                if (!g1.Allow) return Finalize(trace, smart);

                // Gate2
                var g2 = Gate2_Confidence(smart, lowerRegimeThreshold);
                trace.Add(g2);
                if (!g2.Allow) return Finalize(trace, smart);

                // Gate3
                var g3 = Gate3_BaseSignal(symbol, tf, klines, smart, out baseSignal);
                trace.Add(g3);
                if (!g3.Allow || baseSignal == null) return Finalize(trace, smart);

                // CRITICAL: bind confidence at entry
                baseSignal.Confidence = smart.Confidence;

                // Gate4..5
                trace.Add(Gate4_RR(symbol, tf, baseSignal, smart, relaxRr));
                if (!trace.Allow) return Finalize(trace, smart);

                trace.Add(Gate5_Pattern(symbol, tf, klines, baseSignal, relaxPatternBlock));
                if (!trace.Allow) return Finalize(trace, smart);

                // Gate6 async
                var g6 = await Gate6_LiquidityAsync(baseSignal, smart, klines, tf, relaxLiquidity, ct).ConfigureAwait(false);
                trace.Add(g6);
                if (!trace.Allow) return Finalize(trace, smart);

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
                    smart.Confidence >= 0.65m ? "HIGH" :
                    smart.Confidence >= 0.45m ? "MEDIUM" : "LOW";

                CurrentMode = "Allowed";

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
            trace.Allow = trace.FailedGate == null;
            if (!trace.Allow) trace.Signal = null;

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

            if (st.BucketDayUtc != DateTime.UtcNow.Date)
            {
                st.BucketDayUtc = DateTime.UtcNow.Date;
                st.RealizedPnlBucketUsd = 0m;
                st.HarvestsToday = 0;
                st.AddsToday = 0;
            }

            decimal baseCap = st.DefaultSymbolCapPct;

            if (isLowEquityMode)
                baseCap = Math.Min(0.35m, baseCap + 0.10m);

            if (isHighVolatility)
                baseCap = Math.Max(0.10m, baseCap - 0.06m);

            decimal cap = baseCap;
            if (st.CapBoostUntilUtc > DateTime.UtcNow)
                cap = Math.Max(cap, st.CurrentSymbolCapPct);

            decimal maxUsedMarginPct = isLowEquityMode ? 0.70m : 0.55m;
            if (isHighVolatility) maxUsedMarginPct -= 0.08m;

            decimal usedPct = usedMarginUsd / equityUsd;
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

            decimal symbolPct = symbolNotionalUsd / equityUsd;
            bool capHit = symbolPct >= cap;

            bool allowNoProfitAveraging = isSpecialSetup && aiEdgeScore >= 0.78m;

            decimal bucket = st.RealizedPnlBucketUsd;
            decimal reinvestRate = 0.55m;
            decimal fromBucketUsd = Math.Max(0m, bucket * reinvestRate);

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

                maxAddUsd = isHighVolatility ? equityUsd * 0.015m : equityUsd * 0.025m;
                return new ExposureDecision
                {
                    AllowAdd = true,
                    UseProfitBucket = false,
                    AllowedAddUsd = Math.Max(0m, maxAddUsd),
                    Reason = $"ALLOW: special-setup no-profit add (capHit) edge={aiEdgeScore:F2}",
                    SymbolCapPct = cap
                };
            }

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

        // ----------------------------- STOP COOLDOWN -----------------------------
        public static void RegisterStop(string symbol, SignalSide side)
        {
            _lastStopTime[(symbol, side)] = DateTime.UtcNow;

            var cutoff = DateTime.UtcNow.AddHours(-6);
            foreach (var kv in _lastStopTime)
            {
                if (kv.Value < cutoff)
                    _lastStopTime.TryRemove(kv.Key, out _);
            }
        }
    }
}
