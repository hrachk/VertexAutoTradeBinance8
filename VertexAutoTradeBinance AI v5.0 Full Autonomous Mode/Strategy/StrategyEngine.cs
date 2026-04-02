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
using System.Runtime.CompilerServices;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Models.DTO.Debug;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.DecisionTrace;
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
        private readonly ConcurrentDictionary<(string symbol, KlineInterval tf), long> _lastReactiveRun = new();

        // side-aware cooldown after stop
        private static readonly ConcurrentDictionary<(string symbol, SignalSide side), DateTime> _lastStopTime = new();

        // per-key singleflight locks + TTL cleanup
        private readonly ConcurrentDictionary<string, LockEntry> _reactiveLocks = new();
        private long _lockCleanupTick; // interlocked tick

        // LockEntry upgrade
        private sealed class LockEntry
        {
            public readonly SemaphoreSlim Gate = new(1, 1);
            public DateTime LastUsedUtc;
            public int Pending; // 0/1 через Interlocked
        }

        private readonly SignalConfidenceAggregator _confidenceAgg;
        private readonly ConcurrentDictionary<string, long> _lastRealtimeEval = new();

        private readonly ConcurrentDictionary<string, DateTime> _lastSignalUtc = new();
        private static readonly TimeSpan SignalCooldown = TimeSpan.FromSeconds(10);
        private static readonly long RealtimeThrottleTicks = (long)(Stopwatch.Frequency * 0.250); // 250 ms

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
            var now = Stopwatch.GetTimestamp();

            var updated = _lastRealtimeEval.AddOrUpdate(
                symbol,
                now,
                (_, last) =>
                {
                    // если не прошло окно — оставляем старое значение
                    if (now - last < RealtimeThrottleTicks)
                        return last;

                    // иначе обновляем
                    return now;
                });

            // если значение обновилось → можно запускать
            return updated == now;
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
            int count = klines.Count;
            if (count < 60 || atr <= 0)
                return TrendPhase.Unknown;

            int last = count - 1; // последняя ЗАКРЫТАЯ свеча

            // --- окна с безопасными границами ---
            int impStart = Math.Max(1, last - 12);
            int rangeStart = Math.Max(1, last - 10);
            int range5Start = Math.Max(1, last - 5);
            int extStart = Math.Max(1, last - 6);

            bool hadImpulse = false;

            decimal sumRange10 = 0m, sumVol10 = 0m;
            decimal sumRange5 = 0m;

            decimal hi6 = klines[extStart].HighPrice;
            decimal lo6 = klines[extStart].LowPrice;

            int n10 = 0, n5 = 0;

            // --- один проход ---
            for (int i = impStart; i <= last; i++)
            {
                var k = klines[i];
                var range = k.HighPrice - k.LowPrice;

                // импульс
                if (!hadImpulse && i < last - 3)
                {
                    var body = Math.Abs(k.ClosePrice - k.OpenPrice);
                    if (body >= atr * 1.1m)
                        hadImpulse = true;
                }

                // avg 10
                if (i >= rangeStart)
                {
                    sumRange10 += range;
                    sumVol10 += k.Volume;
                    n10++;
                }

                // avg 5
                if (i >= range5Start)
                {
                    sumRange5 += range;
                    n5++;
                }

                // экстремумы
                if (i >= extStart)
                {
                    hi6 = Math.Max(hi6, k.HighPrice);
                    lo6 = Math.Min(lo6, k.LowPrice);
                }
            }

            if (!hadImpulse)
                return TrendPhase.Unknown;

            var c = klines[last];

            decimal avgRange10 = n10 > 0 ? sumRange10 / n10 : (c.HighPrice - c.LowPrice);
            decimal avgVol10 = n10 > 0 ? sumVol10 / n10 : c.Volume;
            decimal recentRange5 = n5 > 0 ? sumRange5 / n5 : (c.HighPrice - c.LowPrice);

            // --- признаки ---
            bool climax =
                (c.HighPrice - c.LowPrice) >= avgRange10 * 1.5m &&
                c.Volume >= avgVol10 * 1.5m;

            bool compression = recentRange5 <= atr * 0.55m;

            bool noContinuationUp = c.HighPrice < hi6 && c.ClosePrice < hi6;
            bool noContinuationDown = c.LowPrice > lo6 && c.ClosePrice > lo6;

            // --- EMA guard ---
            if (count < 21)
                return TrendPhase.Unknown;

            decimal ema21 = Ema(klines, 21, last);
            decimal distEmaAtr = Math.Abs(c.ClosePrice - ema21) / atr;

            // --- нормализация slope ---
            decimal slopeNorm = slope / atr;

            // --- decision (строгий приоритет) ---

            // 1. Distribution
            if ((compression || climax) && distEmaAtr >= 0.75m)
            {
                if (slopeNorm > 0 && noContinuationUp)
                    return TrendPhase.Distribution;

                if (slopeNorm < 0 && noContinuationDown)
                    return TrendPhase.Distribution;
            }

            // 2. Exhaustion
            if (Math.Abs(slopeNorm) < 0.002m && distEmaAtr >= 1.1m)
                return TrendPhase.Exhaustion;

            // 3. Continuation
            if (Math.Abs(slopeNorm) >= 0.0025m)
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
            if (klines == null || klines.Count < 30 || atr <= 0m)
                return false;

            if (last <= 0 || last >= klines.Count)
                return false;

            if (klines.Count < 21)
                return false;

            var c = klines[last];
            var p = klines[last - 1];

            // 1) candle intent
            decimal body = Math.Abs(c.ClosePrice - c.OpenPrice);
            if (body < atr * 0.35m)
                return false;

            bool bull = c.ClosePrice > c.OpenPrice;
            bool bear = c.ClosePrice < c.OpenPrice;

            if (side == SignalSide.Buy && !bull) return false;
            if (side == SignalSide.Sell && !bear) return false;

            // 2) EMA21 cross (close-confirmed)
            decimal ema21 = Ema(klines, 21, last);

            if (side == SignalSide.Buy)
            {
                if (!(c.LowPrice <= ema21 && c.ClosePrice > ema21))
                    return false;
            }
            else
            {
                if (!(c.HighPrice >= ema21 && c.ClosePrice < ema21))
                    return false;
            }

            // 3) swing + BOS (один проход)
            int lb = 10;
            int start = Math.Max(1, last - lb);

            decimal swingHi = klines[start].HighPrice;
            decimal swingLo = klines[start].LowPrice;

            for (int i = start; i < last; i++)
            {
                var k = klines[i];
                if (k.HighPrice > swingHi) swingHi = k.HighPrice;
                if (k.LowPrice < swingLo) swingLo = k.LowPrice;
            }

            decimal buffer = atr * 0.05m;

            if (side == SignalSide.Buy)
            {
                // must break high AND close above it
                if (!(c.HighPrice > swingHi + buffer && c.ClosePrice > swingHi))
                    return false;
            }
            else
            {
                if (!(c.LowPrice < swingLo - buffer && c.ClosePrice < swingLo))
                    return false;
            }

            // 4) anti-spike (после подтверждения структуры)
            if (IsTooBigImpulseBar(c, p, atr))
                return false;

            // 5) regime filter (soft guard)
            if (smart == null || smart.Confidence < 0.35m)
                return false;

            return true;
        }
        private MarketDataFacade? _boundMarketData;

        private Action<string, KlineInterval>? _onWarmHandler;
        private Action<string, KlineInterval, BinanceFuturesUsdtKline>? _onKlineHandler;
        private Action<string, decimal>? _onRealtimeHandler;

        public void BindReactive(MarketDataFacade marketData)
        {
            if (marketData == null)
                throw new ArgumentNullException(nameof(marketData));

            // idempotency: если уже забинжено — сначала отписываемся
            if (_boundMarketData != null)
                UnbindReactive();

            _marketData = marketData;
            _boundMarketData = marketData;

            _onWarmHandler = (symbol, tf) =>
            {
                _logger.LogInformation(
                    "[STRAT][WARM] market warm confirmed {symbol} {tf}",
                    symbol, tf);
            };

            _onKlineHandler = (symbol, tf, candle) =>
            {
                if (!ReactiveTf.Contains(tf))
                    return;

                SafeFireAndForget(() => RunReactive(symbol, tf, "CLOSE"));
            };

            _onRealtimeHandler = (symbol, price) =>
            {
                if (!ShouldRunRealtime(symbol))
                    return;

                // фиксируем TF как design choice
                SafeFireAndForget(() => RunReactive(symbol, KlineInterval.FiveMinutes, "REALTIME"));
            };

            marketData.OnWarm += _onWarmHandler;
            marketData.WsClosedKline += _onKlineHandler;
            marketData.RealtimePrice += _onRealtimeHandler;

            _logger.LogInformation(
                "[STRAT][PUSH] Reactive entry-point bound (REALTIME ENABLED)");
        }

        public void UnbindReactive()
        {
            if (_boundMarketData == null)
                return;

            if (_onWarmHandler != null)
                _boundMarketData.OnWarm -= _onWarmHandler;

            if (_onKlineHandler != null)
                _boundMarketData.WsClosedKline -= _onKlineHandler;

            if (_onRealtimeHandler != null)
                _boundMarketData.RealtimePrice -= _onRealtimeHandler;

            _boundMarketData = null;
        }

        private void SafeFireAndForget(Func<Task> action)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await action().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[STRAT][ASYNC] Unhandled exception in reactive pipeline");
                }
            });
        }

        private static readonly HashSet<string> _htfSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        "BTCUSDT",
        "ETHUSDT",
        "SOLUSDT",
        "XRPUSDT",
        "BNBUSDT"
    };

        private readonly ConcurrentDictionary<(string symbol, SignalSide side, KlineInterval tf), DateTime> _lastSignalCandle = new();
        private async Task RunReactive(string symbol, KlineInterval interval, string reason)
        {
            var md = _marketData;
            if (md == null)
                return;

            var decisionTf = _htfSymbols.Contains(symbol)
                ? KlineInterval.FifteenMinutes
                : KlineInterval.FiveMinutes;

            var key = (symbol, decisionTf);
            var nowTick = Stopwatch.GetTimestamp();

            // --- WARMUP ---
            if (!md.HasSnapshotState && md.IsInWarmup(symbol, decisionTf))
                return;

            // --- REALTIME THROTTLE (atomic) ---
            if (reason == "REALTIME")
            {
                var updated = _lastReactiveRun.AddOrUpdate(
                    key,
                    nowTick,
                    (_, last) => (nowTick - last < RealtimeThrottleTicks) ? last : nowTick);

                if (updated != nowTick)
                    return;
            }
            else
            {
                _lastReactiveRun[key] = nowTick;
            }

            // --- LOCK ---
            var le = _reactiveLocks.GetOrAdd($"{symbol}:{decisionTf}", _ => new LockEntry());
            le.LastUsedUtc = DateTime.UtcNow;

            if (!await le.Gate.WaitAsync(0))
            {
                Interlocked.Exchange(ref le.Pending, 1);
                return;
            }

            try
            {
                while (true) // drain loop
                {
                    Interlocked.Exchange(ref le.Pending, 0);

                    // --- LOAD ---
                    var klines = await md.GetKlinesAsync(
                        symbol,
                        decisionTf,
                        need: 200,
                        CancellationToken.None);

                    if (klines == null || klines.Count < 50)
                        return;

                    var working = klines;

                    // --- REALTIME PATCH (без лишних аллокаций если не нужно) ---
                    if (reason == "REALTIME")
                    {
                        var price = md.GetLastPrice(symbol);
                        if (price > 0)
                        {
                            var last = klines[^1];

                            var modified = new BinanceFuturesUsdtKline
                            {
                                OpenTime = last.OpenTime,
                                CloseTime = last.CloseTime,
                                OpenPrice = last.OpenPrice,
                                HighPrice = Math.Max(last.HighPrice, price),
                                LowPrice = Math.Min(last.LowPrice, price),
                                ClosePrice = price,
                                Volume = last.Volume,
                                QuoteVolume = last.QuoteVolume,
                                TradeCount = last.TradeCount,
                                TakerBuyBaseVolume = last.TakerBuyBaseVolume,
                                TakerBuyQuoteVolume = last.TakerBuyQuoteVolume
                            };

                            var temp = new List<BinanceFuturesUsdtKline>(klines);
                            temp[^1] = modified;
                            working = temp;
                        }
                    }

                    // --- EVAL ---
                    var sw = Stopwatch.StartNew();

                    var decision = await EvaluateSignalAsync(
                        symbol,
                        decisionTf,
                        working,
                        CancellationToken.None);

                    sw.Stop();

                    SafeRecordDecisionTrace(symbol, decisionTf, decision);

                    if (!decision.Allow || decision.Signal == null)
                        break;

                    var signal = decision.Signal;

                    signal.Symbol = symbol;
                    signal.Timeframe = decisionTf.ToString();
                    signal.Time = DateTime.UtcNow;

                    if (signal.EntryPrice <= 0)
                        signal.EntryPrice = working[^1].ClosePrice;

                    if (signal.StopLoss <= 0)
                        signal.StopLoss = working[^1].ClosePrice;

                    if (!Enum.IsDefined(typeof(SignalSide), signal.Side))
                        break;

                    // --- COOLDOWN (atomic-ish) ---
                    var sigKey = (symbol, signal.Side, decisionTf);
                    var nowUtc = DateTime.UtcNow;
                    var candleTime = working[^1].OpenTime;

                    if (_lastSignalUtc.TryGetValue($"{symbol}:{signal.Side}:{decisionTf}", out var lastUtc))
                    {
                        if (nowUtc - lastUtc < SignalCooldown)
                            break;
                    }

                    if (_lastSignalCandle.TryGetValue(sigKey, out var lastCandle))
                    {
                        if (lastCandle == candleTime)
                            break;
                    }

                    _lastSignalUtc[$"{symbol}:{signal.Side}:{decisionTf}"] = nowUtc;
                    _lastSignalCandle[sigKey] = candleTime;

                    _logger.LogInformation(
                        "[LATENCY] {symbol} {reason} eval {ms}ms",
                        symbol,
                        reason,
                        sw.ElapsedMilliseconds);

                    OnSignalGenerated?.Invoke(signal);

                    // если нет pending — выходим
                    if (Interlocked.CompareExchange(ref le.Pending, 0, 0) == 0)
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[STRAT][{symbol}] reactive error",
                    symbol);
            }
            finally
            {
                le.Gate.Release();
            }
        }

        private readonly ConcurrentDictionary<(string symbol, KlineInterval tf), long> _traceThrottle = new();

        private static readonly long TraceThrottleTicks =
            (long)(Stopwatch.Frequency * 0.100); // 100 ms
        private void SafeRecordDecisionTrace(string symbol, KlineInterval tf, SignalDecisionTrace decision)
        {
            var trace = _decisionTrace;
            if (trace == null || decision == null)
                return;

            var key = (symbol, tf);
            var now = Stopwatch.GetTimestamp();

            var updated = _traceThrottle.AddOrUpdate(
                key,
                now,
                (_, last) => (now - last < TraceThrottleTicks) ? last : now);

            if (updated != now)
                return;

            try
            {
                var snapshot = new DecisionTraceSnapshot
                {
                    Symbol = symbol,
                    Timeframe = tf.ToString(),
                    Allow = decision.Allow,
                    FailedGate = decision.FailedGate?.Gate,
                    Reason = decision.FailedGate?.Reason,
                    Time = DateTime.UtcNow,
                    Confidence = decision.Signal?.Confidence ?? 0m
                };

                trace.Record(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[DECISION][{symbol}][{tf}] DecisionTrace.Record failed",
                    symbol, tf);
            }
        }
        // ----------------------------- CORE HELPERS -----------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (decimal slMult, decimal tp1Mult, decimal tp2Mult, decimal tp3Mult)
      GetAtrConfig(KlineInterval interval)
        {
            return interval switch
            {
                // scalping / noisy
                KlineInterval.OneMinute or KlineInterval.FiveMinutes
                    => (0.9m, 1.6m, 2.4m, 3.3m),

                // intraday
                KlineInterval.FifteenMinutes
                    => (1.2m, 1.8m, 2.6m, 3.6m),

                // swing
                KlineInterval.OneHour or KlineInterval.FourHour
                    => (1.8m, 2.2m, 3.2m, 4.2m),

                // position
                KlineInterval.OneDay
                    => (2.5m, 2.8m, 3.8m, 5.0m),

                _ => (1.0m, 1.6m, 2.4m, 3.3m)
            };
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static decimal Ema(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period, int index)
        {
            if (klines == null || klines.Count == 0 || index < 0 || index >= klines.Count)
                return 0;

            int start = Math.Max(0, index - period + 1);
            int count = index - start + 1;

            // начальное значение = SMA
            decimal sum = 0m;
            for (int i = start; i <= index; i++)
                sum += klines[i].ClosePrice;

            decimal ema = sum / count;

            decimal k = 2m / (period + 1);

            for (int i = start + 1; i <= index; i++)
                ema = klines[i].ClosePrice * k + ema * (1 - k);

            return ema;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static decimal Atr(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period, int lastIndex)
        {
            if (klines == null || klines.Count < period + 1)
                return 0;

            if (lastIndex <= 0 || lastIndex >= klines.Count)
                return 0;

            int start = lastIndex - period + 1;
            if (start <= 0) start = 1;

            decimal atr = 0m;

            // начальный ATR = SMA TR
            for (int i = start; i <= lastIndex; i++)
                atr += GetTrueRange(klines[i], klines[i - 1]);

            atr /= (lastIndex - start + 1);

            // Wilder smoothing (1 шаг назад для стабильности)
            for (int i = lastIndex - period + 1; i <= lastIndex; i++)
            {
                var tr = GetTrueRange(klines[i], klines[i - 1]);
                atr = ((atr * (period - 1)) + tr) / period;
            }

            return atr;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static decimal GetTrueRange(
     BinanceFuturesUsdtKline current,
     BinanceFuturesUsdtKline prev)
        {
            if (current == null || prev == null)
                return 0;

            var tr1 = current.HighPrice - current.LowPrice;
            var tr2 = Math.Abs(current.HighPrice - prev.ClosePrice);
            var tr3 = Math.Abs(current.LowPrice - prev.ClosePrice);

            return Math.Max(tr1, Math.Max(tr2, tr3));
        }
        private const decimal ImpulseAtrMultiplier = 2.2m;

        private static bool IsTooBigImpulseBar(
            BinanceFuturesUsdtKline current,
            BinanceFuturesUsdtKline prev,
            decimal atr)
        {
            if (atr <= 0 || current == null || prev == null)
                return false;

            var tr = GetTrueRange(current, prev);
            return tr >= atr * ImpulseAtrMultiplier;
        }
        private static bool IsTooSmallBody(
      BinanceFuturesUsdtKline current,
      decimal atr)
        {
            if (current == null || atr <= 0)
                return false; // раньше ломало pipeline

            var body = Math.Abs(current.ClosePrice - current.OpenPrice);
            return body < atr * 0.08m;
        }
        private static void NormalizeEntryAndSl(TradeSignal s)
        {
            if (s == null || s.EntryPrice <= 0 || s.StopLoss <= 0)
                return;

            decimal dist = Math.Abs(s.EntryPrice - s.StopLoss);
            if (dist <= 0)
                return;

            decimal minDist;

            if (s.Atr is > 0)
            {
                decimal atr = s.Atr.Value;

                decimal atrMult = s.Reason switch
                {
                    "PULLBACK_EMA21_LONG" or "PULLBACK_EMA21_SHORT" => 0.55m,
                    "LIQUIDITY_GRAB_CONTINUATION_LONG" or "LIQUIDITY_GRAB_CONTINUATION_SHORT" => 0.65m,
                    "VOLATILITY_EXPANSION_BREAKOUT_LONG" or "VOLATILITY_EXPANSION_BREAKOUT_SHORT" => 0.85m,
                    _ => 0.60m
                };

                minDist = atr * atrMult;

                decimal hardFloor = s.EntryPrice * 0.0015m;
                if (minDist < hardFloor)
                    minDist = hardFloor;
            }
            else
            {
                minDist = s.EntryPrice * 0.0025m;
            }

            if (dist >= minDist)
                return;

            if (s.Side == SignalSide.Buy)
                s.StopLoss = Math.Max(0, s.EntryPrice - minDist);
            else
                s.StopLoss = s.EntryPrice + minDist;
        }
        /// // ----------------------------- SIGNAL PATTERNS -----------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HasImpulseBefore(
      IReadOnlyList<BinanceFuturesUsdtKline> klines,
      int last,
      decimal atr)
        {
            if (klines == null || klines.Count < 10 || atr <= 0)
                return false;

            if (last <= 1 || last >= klines.Count)
                return false;

            // окно: последние 3–6 свечей ДО текущей
            int start = Math.Max(1, last - 6);
            int end = last - 2; // исключаем последнюю и предпоследнюю

            for (int i = start; i <= end; i++)
            {
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
            if (last < 2)
                return null;

            var c = klines[last - 1];
            var prev = klines[last - 2];

            decimal price = c.ClosePrice;
            if (price <= 0)
                return null;

            // --- ATR ---
            decimal atr = Atr(klines, 14, last - 1);
            if (atr <= 0 || atr / price < 0.0010m)
                return null;

            if (IsTooBigImpulseBar(c, prev, atr) || IsTooSmallBody(c, atr))
                return null;

            // --- EMA slope ---
            decimal emaNow = Ema(klines, 21, last - 1);
            decimal emaPast = Ema(klines, 21, last - 6);

            if (emaPast == 0)
                return null;

            decimal slope = (emaNow - emaPast) / atr;

            const decimal strong = 0.35m;
            const decimal weak = 0.20m;

            bool up = slope > weak;
            bool down = slope < -weak;

            if (!up && !down)
                return null;

            var (slMult, tp1Mult, tp2Mult, tp3Mult) = GetAtrConfig(interval);

            // ================= LONG =================
            if (up)
            {
                decimal grab = prev.LowPrice - c.LowPrice;

                bool valid =
                    c.LowPrice < prev.LowPrice &&
                    c.ClosePrice > prev.LowPrice &&
                    grab >= atr * 0.45m && // чуть жестче
                    c.ClosePrice > c.OpenPrice;

                if (valid)
                {
                    decimal entry = c.ClosePrice + atr * 0.05m;
                    decimal sl = c.LowPrice - atr * slMult;

                    decimal risk = entry - sl;
                    if (risk <= atr * 0.35m)
                        return null;

                    decimal tp1 = entry + atr * tp1Mult;
                    decimal rr = (tp1 - entry) / risk;

                    if (rr < 1.3m)
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
                        Reason = "LIQ_GRAB_LONG_V4",
                        IsSuperSignal = slope > strong,
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

            // ================= SHORT =================
            if (down)
            {
                decimal grab = c.HighPrice - prev.HighPrice;

                bool valid =
                    c.HighPrice > prev.HighPrice &&
                    c.ClosePrice < prev.HighPrice &&
                    grab >= atr * 0.45m &&
                    c.ClosePrice < c.OpenPrice;

                if (valid)
                {
                    decimal entry = c.ClosePrice - atr * 0.05m; // ❗ FIX
                    decimal sl = c.HighPrice + atr * slMult;

                    decimal risk = sl - entry;
                    if (risk <= atr * 0.35m)
                        return null;

                    decimal tp1 = entry - atr * tp1Mult;
                    decimal rr = (entry - tp1) / risk;

                    if (rr < 1.3m)
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
                        Reason = "LIQ_GRAB_SHORT_V4",
                        IsSuperSignal = slope < -strong,
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

        private TradeSignal? TryPullbackEma21(
      string symbol,
      KlineInterval interval,
      IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines == null || klines.Count < 40)
                return null;

            int last = klines.Count - 1;
            if (last < 2)
                return null;

            var c = klines[last - 1];
            var prev = klines[last - 2];

            decimal atr = Atr(klines, 14, last - 1);
            if (atr <= 0)
                return null;

            if (VolumeSpike(klines, last - 1) || IsExhaustion(klines, last - 1, atr))
                return null;

            decimal emaNow = Ema(klines, 21, last - 1);
            decimal emaPast = Ema(klines, 21, last - 6);
            if (emaPast == 0)
                return null;

            // --- нормализованный slope ---
            decimal slope = (emaNow - emaPast) / atr;
            if (Math.Abs(slope) < 0.25m)
                return null;

            // --- импульс до отката ---
            if (!HasImpulseBefore(klines, last - 1, atr))
                return null;

            // --- проверка отклонения от EMA (был ли stretch) ---
            decimal maxDist = 0m;
            int start = Math.Max(21, last - 10);

            for (int i = start; i <= last - 1; i++)
            {
                var dist = Math.Abs(klines[i].ClosePrice - emaNow);
                if (dist > maxDist)
                    maxDist = dist;
            }

            if (maxDist < atr * 1.2m)
                return null;

            if (IsTooBigImpulseBar(c, prev, atr) || IsTooSmallBody(c, atr))
                return null;

            var (slMult, tp1Mult, tp2Mult, tp3Mult) = GetAtrConfig(interval);

            // ================= LONG =================
            if (slope > 0)
            {
                bool rejection =
                    c.LowPrice <= emaNow &&
                    c.ClosePrice > emaNow &&
                    c.ClosePrice > c.OpenPrice;

                if (rejection)
                {
                    decimal entry = c.ClosePrice + atr * 0.05m;
                    decimal sl = c.LowPrice - atr * slMult;

                    decimal risk = entry - sl;
                    if (risk <= atr * 0.35m)
                        return null;

                    decimal tp1 = entry + atr * tp1Mult;
                    decimal rr = (tp1 - entry) / risk;

                    if (rr < 1.3m)
                        return null;

                    // защита от late entry
                    if (c.ClosePrice - emaNow > atr * 0.6m)
                        return null;

                    var s = new TradeSignal
                    {
                        Symbol = symbol,
                        Side = SignalSide.Buy,
                        EntryPrice = entry,
                        StopLoss = sl,
                        Atr = atr,
                        Timeframe = interval.ToString(),
                        Time = c.CloseTime,
                        Reason = "PULLBACK_EMA21_LONG_V2",
                        IsSuperSignal = slope > 0.4m,
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
            }

            // ================= SHORT =================
            if (slope < 0)
            {
                bool rejection =
                    c.HighPrice >= emaNow &&
                    c.ClosePrice < emaNow &&
                    c.ClosePrice < c.OpenPrice;

                if (rejection)
                {
                    decimal entry = c.ClosePrice - atr * 0.05m; // FIX symmetry
                    decimal sl = c.HighPrice + atr * slMult;

                    decimal risk = sl - entry;
                    if (risk <= atr * 0.35m)
                        return null;

                    decimal tp1 = entry - atr * tp1Mult;
                    decimal rr = (entry - tp1) / risk;

                    if (rr < 1.3m)
                        return null;

                    // защита от late entry
                    if (emaNow - c.ClosePrice > atr * 0.6m)
                        return null;

                    var s = new TradeSignal
                    {
                        Symbol = symbol,
                        Side = SignalSide.Sell,
                        EntryPrice = entry,
                        StopLoss = sl,
                        Atr = atr,
                        Timeframe = interval.ToString(),
                        Time = c.CloseTime,
                        Reason = "PULLBACK_EMA21_SHORT_V2",
                        IsSuperSignal = slope < -0.4m,
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
                smartType == SmartRegimeType.SmartTrend;

            // -------------------------
            // 1️⃣ Base threshold
            // -------------------------

            if (isRangeLike)
                threshold = 42;
            else if (isStrongTrendLike)
                threshold = 48;
            else if (isTrendLike)
                threshold = 45;
            else
                threshold = 45;

            // -------------------------
            // 2️⃣ Volatility adjustment
            // -------------------------

            if (volatility < 0.008m)          // dead market
                threshold += 4;

            else if (volatility > 0.030m)     // high turbulence
                threshold += 6;

            // -------------------------
            // 3️⃣ Trend acceleration
            // -------------------------

            if (Math.Abs(slope) > Math.Max(0.004m, volatility * 0.6m))
                threshold -= 4;

            // -------------------------
            // 4️⃣ Clamp
            // -------------------------

            return Math.Clamp(threshold, 32, 70);
        }

        private static bool IsFastTrendOverride(SmartRegimeInfo smart)
        {
            bool strongTrend =
                smart.BaseRegime == MarketRegime.StrongUpTrend ||
                smart.BaseRegime == MarketRegime.StrongDownTrend ||
                smart.SmartType == SmartRegimeType.SmartStrongTrend;

            bool slopeOk =
                smart.VolatilityPercent > 0 &&
                Math.Abs(smart.TrendSlopePercent) >= smart.VolatilityPercent * 0.8m;

            bool volOk =
                smart.VolatilityPercent >= 0.006m &&
                smart.VolatilityPercent <= 0.030m;

            bool confOk = smart.Confidence >= 0.35m;

            return strongTrend && slopeOk && volOk && confOk;
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

        // ----------------------------- GATES 0..7 (PRODUCTION) -----------------------------
        private FastFailResult Gate0_Data(string symbol, KlineInterval tf, IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            if (klines == null) return FastFailResult.Fail("DATA", "klines=null");
            if (klines.Count < 60) return FastFailResult.Fail("DATA", $"bars={klines.Count}<60");
            return FastFailResult.Ok();
        }


        private FastFailResult Gate1_SmartRegime(
      string symbol,
      KlineInterval tf,
      IReadOnlyList<BinanceFuturesUsdtKline> klines,
      out SmartRegimeInfo smart)
        {
            // ------------------------------------------------------------
            // SAFE DEFAULT (never null)
            // ------------------------------------------------------------
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
                {
                    _engineState.LastEntryDecision = "REGIME_FALLBACK";
                    return FastFailResult.Ok(); // ❗ НЕ блокируем систему
                }

                // ------------------------------------------------------------
                // SANITY NORMALIZATION (NO HARD FAILS)
                // ------------------------------------------------------------

                // Confidence
                if (evaluated.Confidence < 0m || evaluated.Confidence > 1.0m)
                {
                    _logger.LogWarning(
                        "[SMART] Confidence out of range {value} → clamped [{symbol} {tf}]",
                        evaluated.Confidence,
                        symbol,
                        tf);

                    evaluated.Confidence = Math.Clamp(evaluated.Confidence, 0m, 1m);
                }

                // Trend slope (убираем hard fail → clamp)
                if (Math.Abs(evaluated.TrendSlopePercent) > 5.0m)
                {
                    _logger.LogWarning(
                        "[SMART] Slope extreme {slope:F4} → clamped [{symbol} {tf}]",
                        evaluated.TrendSlopePercent,
                        symbol,
                        tf);

                    evaluated.TrendSlopePercent =
                        Math.Clamp(evaluated.TrendSlopePercent, -5.0m, 5.0m);
                }

                // Volatility (убираем hard fail → clamp)
                if (evaluated.VolatilityPercent < 0m || evaluated.VolatilityPercent > 1.0m)
                {
                    _logger.LogWarning(
                        "[SMART] Volatility out of range {vol:F4} → clamped [{symbol} {tf}]",
                        evaluated.VolatilityPercent,
                        symbol,
                        tf);

                    evaluated.VolatilityPercent =
                        Math.Clamp(evaluated.VolatilityPercent, 0m, 1.0m);
                }

                smart = evaluated;

                // ------------------------------------------------------------
                // SHARED ATR (fail-safe)
                // ------------------------------------------------------------
                decimal atr14 = 0m;
                try
                {
                    if (klines != null && klines.Count > 20)
                        atr14 = Atr(klines, 14, klines.Count - 1);
                }
                catch
                {
                    // non-critical
                }

                // ------------------------------------------------------------
                // AI STATE RECORD (fail-safe, no impact on trading)
                // ------------------------------------------------------------
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
                catch { }

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
                catch { }

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

                // ❗ FAIL-SAFE: НЕ блокируем торговлю
                _engineState.LastEntryDecision = "REGIME_ERROR_FALLBACK";

                return FastFailResult.Ok();
            }
        }

        private FastFailResult Gate2_ConfidenceHybrid(
       decimal finalConfidence,
       SmartRegimeInfo smart,
       bool lowerRegimeThreshold,
       string symbol)
        {
            var cfg = _confidenceCfg.Resolve(symbol);

            if (smart.IsDangerChopZone)
                return FastFailResult.Fail("CONF", "DangerChopZone");

            int adaptiveThreshold = GetAdaptiveThreshold(
                smart.BaseRegime,
                smart.SmartType,
                smart.VolatilityPercent,
                smart.TrendSlopePercent);

            if (lowerRegimeThreshold)
            {
                adaptiveThreshold = (int)(adaptiveThreshold * 0.85m);
                _engineState.SoftEntry = true;
                LastSoftEntry = true;
            }

            decimal adaptiveFloor = adaptiveThreshold / 100m;

            bool fastTrend = IsFastTrendOverride(smart);
            if (fastTrend)
                adaptiveFloor *= 0.85m;

            decimal absoluteFloor = cfg.MinEntry;
           // decimal finalFloor = Math.Max(absoluteFloor, adaptiveFloor);

           // finalFloor = Math.Clamp(finalFloor, 0.10m, 0.85m);

            decimal finalFloor = adaptiveFloor;

            if (finalFloor < absoluteFloor)
                finalFloor = (finalFloor + absoluteFloor) * 0.5m;

            if (finalConfidence < finalFloor)
            {
                return FastFailResult.Fail(
                    "CONF",
                    $"conf={finalConfidence:P0}<thr={finalFloor:P0} (fastTrend={fastTrend})");
            }

            _engineState.LastEntryDecision = "CONF_OK";
            _engineState.ConfidenceRaw = finalConfidence;
            _engineState.ConfidencePercent = (int)(finalConfidence * 100);

            _engineState.ConfidenceLevel =
                finalConfidence >= cfg.Bands.HighFrom ? "HIGH" :
                finalConfidence >= cfg.MinEntry ? "MEDIUM" : "LOW";

            Confidence = finalConfidence;

            return FastFailResult.Ok();
        }
        private FastFailResult Gate2_5_TrendPhaseLock(
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            SmartRegimeInfo smart,
            TradeSignal? candidate)
        {
            if (candidate == null || klines == null || klines.Count < 20)
                return FastFailResult.Ok();

            decimal atr = candidate.Atr ?? Atr(klines, 14, klines.Count - 1);
            if (atr <= 0m) return FastFailResult.Ok();

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

        private FastFailResult Gate3_2_LateEntryFilter(
            string symbol,
            KlineInterval tf,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            TradeSignal signal,
            SmartRegimeInfo smart)
        {
            // ============================================================
            // 0️⃣ GUARDS
            // ============================================================

            if (klines == null || klines.Count < 40 || signal == null || smart == null)
                return FastFailResult.Ok();

            int last = klines.Count - 1;
            var c = klines[last];
            var prev = klines[last - 1];

            decimal atr;
            try
            {
                atr = signal.Atr ?? Atr(klines, 14, last);
            }
            catch
            {
                return FastFailResult.Ok();
            }

            if (atr <= 0m)
                return FastFailResult.Ok();

            decimal ema21 = Ema(klines, 21, last);
            if (ema21 <= 0m)
                return FastFailResult.Ok();

            decimal distFromEmaAtr = Math.Abs(c.ClosePrice - ema21) / atr;

            // ============================================================
            // 1️⃣ REGIME FLAGS
            // ============================================================

            bool rangeLike =
                smart.BaseRegime == MarketRegime.Range ||
                smart.SmartType == SmartRegimeType.SmartRange ||
                smart.SmartType == SmartRegimeType.SmartSqueeze;

            bool strongTrend =
                smart.BaseRegime == MarketRegime.StrongUpTrend ||
                smart.BaseRegime == MarketRegime.StrongDownTrend ||
                smart.SmartType == SmartRegimeType.SmartStrongTrend;

            // ============================================================
            // 2️⃣ EARLY TREND OVERRIDE (critical fix vs FOMO block)
            // ============================================================

            if (strongTrend)
            {
                decimal slopeLock = 0.012m + smart.VolatilityPercent * 1.2m;
                slopeLock = Math.Clamp(slopeLock, 0.01m, 0.03m);

                bool slopeOk =
                    (smart.TrendSlopePercent > 0 && smart.TrendSlopePercent < slopeLock) ||
                    (smart.TrendSlopePercent < 0 && smart.TrendSlopePercent > -slopeLock);

                if (slopeOk && distFromEmaAtr <= 1.0m)
                    return FastFailResult.Ok(); // 🚀 не душим ранний тренд
            }

            // ============================================================
            // 3️⃣ HARD NO-PULLBACK BLOCK (ANTI-FOMO)
            // ============================================================

            decimal noPullbackThr =
                rangeLike ? 0.9m :
                strongTrend ? 1.6m :
                1.2m;

            if (signal.Side == SignalSide.Buy && distFromEmaAtr > noPullbackThr)
            {
                _engineState.LastEntryDecision = "BLOCKED_NO_PULLBACK";
                return FastFailResult.Fail("NO_PULLBACK", "too far from EMA");
            }

            if (signal.Side == SignalSide.Sell && distFromEmaAtr > noPullbackThr)
            {
                _engineState.LastEntryDecision = "BLOCKED_NO_PULLBACK";
                return FastFailResult.Fail("NO_PULLBACK", "too far from EMA");
            }

            // ============================================================
            // 4️⃣ MICRO IMPULSE / FLOW
            // ============================================================

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

            bool overheatFlow = sameDirBars >= 4;

            decimal maxEmaDistAtr =
                rangeLike ? 0.8m :
                strongTrend ? 1.4m :
                1.1m;

            decimal impulseAtrThr =
                rangeLike ? 1.2m :
                strongTrend ? 2.0m :
                1.6m;

            // ============================================================
            // 5️⃣ STRUCTURE OVEREXTENSION (FIX: не душим тренд)
            // ============================================================

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

            decimal structureMove = (cycleHi - cycleLo) / atr;

            if (!strongTrend && structureMove > 3.5m)
            {
                _engineState.LastEntryDecision = "BLOCKED_LATE_STRUCTURE";
                return FastFailResult.Fail("LATE_STRUCTURE", "overextended");
            }

            // ============================================================
            // 6️⃣ LATE CYCLE BLOCK
            // ============================================================

            decimal maxCycleAtr =
                rangeLike ? 1.6m :
                strongTrend ? 3.2m :
                2.4m;

            decimal maxEmaExt =
                rangeLike ? 0.85m :
                strongTrend ? 1.5m :
                1.1m;

            if (structureMove >= maxCycleAtr && distFromEmaAtr >= maxEmaExt)
            {
                _engineState.LastEntryDecision = "BLOCKED_LATE_CYCLE";
                CurrentMode = "Blocked:LATE_CYCLE";

                return FastFailResult.Fail("LATE_CYCLE", "cycle exhausted");
            }

            // ============================================================
            // 7️⃣ LATE CHASE → PENALTY ONLY
            // ============================================================

            bool lateChase =
                moveAtr >= impulseAtrThr &&
                distFromEmaAtr >= maxEmaDistAtr;

            if (hugeBarNow && distFromEmaAtr >= maxEmaDistAtr * 0.9m)
                lateChase = true;

            if (overheatFlow && distFromEmaAtr >= maxEmaDistAtr)
                lateChase = true;

            if (lateChase)
            {
                bool retest =
                    Math.Abs(signal.EntryPrice - ema21) <= atr * 0.25m;

                if (!retest)
                {
                    signal.Confidence *= lateChase ? 0.85m : 0.90m;
                    _engineState.LastEntryDecision = lateChase ? "WARN_LATE_ENTRY" : "LATE_OK";
                }

                return FastFailResult.Ok();
            }

            // ============================================================
            // 8️⃣ LIGHT PENALTY (only if slightly extended)
            // ============================================================

            if (distFromEmaAtr > 0.8m)
            {
                signal.Confidence *= 0.90m;
                _engineState.LastEntryDecision = "LATE_PENALTY";
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

            // =========================
            // SAFETY GUARD
            // =========================
            if (klines == null || klines.Count < 60)
            {
                _engineState.LastEntryDecision = "DATA_NOT_READY";
                return FastFailResult.Ok(); // soft-pass вместо блокировки
            }

            int i = klines.Count - 1;
            var last = klines[i];

            var atr = Atr(klines, 14, i);
            if (atr <= 0 || last.ClosePrice <= 0)
                return FastFailResult.Fail("ATR", "invalid ATR");

            if (atr / last.ClosePrice < 0.0008m)
                _engineState.LastEntryDecision = "LOW_VOL_WARNING";


            // =========================
            // REGIME FLAGS
            // =========================
            bool rangeLike =
                smart.BaseRegime == MarketRegime.Range ||
                smart.SmartType == SmartRegimeType.SmartRange;

            bool squeezeLike =
                smart.SmartType == SmartRegimeType.SmartSqueeze;

            bool trendLike =
                smart.BaseRegime == MarketRegime.UpTrend ||
                smart.BaseRegime == MarketRegime.DownTrend ||
                smart.BaseRegime == MarketRegime.StrongUpTrend ||
                smart.BaseRegime == MarketRegime.StrongDownTrend ||
                smart.SmartType == SmartRegimeType.SmartTrend ||
                smart.SmartType == SmartRegimeType.SmartStrongTrend;


            // =========================
            // PATTERNS
            // =========================
            TradeSignal? pullback = null;
            TradeSignal? earlyTrend = null;
            TradeSignal? continuation = null;
            TradeSignal? liquidity = null;


            // =========================
            // PATTERN PRIORITY (НЕ БЛОКИРУЕТ)
            // =========================
            if (squeezeLike)
            {
                earlyTrend = TryEarlyTrendJoin(symbol, tf, klines, smart);
                if (earlyTrend == null)
                    pullback = TryPullbackEma21(symbol, tf, klines);
                if (pullback == null)
                    liquidity = TryLiquidityGrab(symbol, tf, klines);
            }
            else if (rangeLike)
            {
                liquidity = TryLiquidityGrab(symbol, tf, klines);
                if (liquidity == null)
                    pullback = TryPullbackEma21(symbol, tf, klines);
                if (pullback == null)
                    earlyTrend = TryEarlyTrendJoin(symbol, tf, klines, smart);
            }
            else
            {
                // TREND MODE
                pullback = TryPullbackEma21(symbol, tf, klines);
                if (pullback == null)
                    earlyTrend = TryEarlyTrendJoin(symbol, tf, klines, smart);

                // continuation всегда пробуем
                if (pullback == null && earlyTrend == null)
                    continuation = TryImpulseContinuation(symbol, tf, klines, smart);

                if (pullback == null && earlyTrend == null && continuation == null)
                    liquidity = TryLiquidityGrab(symbol, tf, klines);
            }


            // =========================
            // FINAL PICK
            // =========================
            baseSignal = pullback ?? earlyTrend ?? continuation ?? liquidity;

            //if (baseSignal == null)
            //{
            //    _engineState.LastEntryDecision = "NO_BASE_PATTERN";
            //    return FastFailResult.Fail("BASE", "no base pattern");
            //}
            if (baseSignal == null)
            {
                _engineState.LastEntryDecision = "NO_BASE_PATTERN";
                return FastFailResult.Ok(); // soft-pass
            }

            // =========================
            // SOFT SLOPE CHECK (НЕ БЛОКИРУЕТ)
            // =========================
            var slopeLock = 0.006m + smart.VolatilityPercent * 1.0m;
            slopeLock = Math.Clamp(slopeLock, 0.005m, 0.020m);

            bool slopeUp = smart.TrendSlopePercent > slopeLock;
            bool slopeDown = smart.TrendSlopePercent < -slopeLock;

            bool strongTrend =
                smart.BaseRegime == MarketRegime.StrongUpTrend ||
                smart.BaseRegime == MarketRegime.StrongDownTrend ||
                smart.SmartType == SmartRegimeType.SmartStrongTrend;

            if (strongTrend)
            {
                bool counter =
                    (slopeUp && baseSignal.Side == SignalSide.Sell) ||
                    (slopeDown && baseSignal.Side == SignalSide.Buy);

                if (counter)
                {
                    _engineState.LastEntryDecision = "WARN_COUNTER_TREND";
                }
            }


            // =========================
            // COOLDOWN (РЕАЛЬНЫЙ БЛОК)
            // =========================
            if (_lastStopTime.TryGetValue((symbol, baseSignal.Side), out var lastStop))
            {
                var diff = DateTime.UtcNow - lastStop;
                bool regimeChanged = smart.Confidence > 0.6m &&
                                     Math.Abs(smart.TrendSlopePercent) > 0.01m;

                if (diff < TimeSpan.FromMinutes(7) && !regimeChanged)
                {
                    _engineState.LastEntryDecision = "COOLDOWN";

                    // Soft-penalty вместо блокировки
                    baseSignal.Confidence *= 0.92m;

                    return FastFailResult.Ok();
                }
            }

            // =========================
            // TRACE
            // =========================
            _engineState.LastEntryDecision =
                baseSignal == pullback ? "BASE_PULLBACK" :
                baseSignal == earlyTrend ? "BASE_EARLY_TREND" :
                baseSignal == continuation ? "BASE_CONTINUATION" :
                "BASE_LIQUIDITY";

            return FastFailResult.Ok();
        }

        private FastFailResult Gate_TrapFilter(
        IReadOnlyList<BinanceFuturesUsdtKline> klines,
        TradeSignal signal,
        out decimal confidenceMultiplier)
        {
            confidenceMultiplier = 1.0m;

            if (klines == null || klines.Count < 2)
                return FastFailResult.Ok();

            var last = klines[^1];
            var prev = klines[^2];

            bool fakeBreakUp = last.HighPrice > prev.HighPrice && last.ClosePrice < prev.HighPrice;
            bool fakeBreakDown = last.LowPrice < prev.LowPrice && last.ClosePrice > prev.LowPrice;

            var range = last.HighPrice - last.LowPrice;
            var body = Math.Abs(last.ClosePrice - last.OpenPrice);

            decimal boost = 1.0m;

            if (range > 0)
            {
                var bodyRatio = body / range;
                if (bodyRatio > 0.6m) boost = 1.15m;
                else if (bodyRatio > 0.4m) boost = 1.10m;
            }

            if (signal.Side == SignalSide.Sell && fakeBreakUp)
                confidenceMultiplier *= boost;

            if (signal.Side == SignalSide.Buy && fakeBreakDown)
                confidenceMultiplier *= boost;

            if (signal.Reason == "LIQUIDITY")
            {
                if (signal.Side == SignalSide.Buy && !fakeBreakDown)
                    confidenceMultiplier *= 0.85m;

                if (signal.Side == SignalSide.Sell && !fakeBreakUp)
                    confidenceMultiplier *= 0.85m;
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
            // ============================================================
            // 0️⃣ GUARDS
            // ============================================================

            if (signal == null || smart == null)
                return FastFailResult.Ok();

            if (signal.TakeProfits == null || signal.TakeProfits.Count == 0)
                return FastFailResult.Ok();

            var slDist = Math.Abs(signal.EntryPrice - signal.StopLoss);

            if (slDist <= 0m)
                return FastFailResult.Fail("RR", "slDist<=0");

            // ============================================================
            // 1️⃣ ATR SANITY
            // ============================================================

            var atr = signal.Atr ?? 0m;

            if (atr > 0m && slDist < atr * 0.35m)
                return FastFailResult.Fail("RR", "SL too tight vs ATR");

            // ============================================================
            // 2️⃣ TP DIST (median)
            // ============================================================

            var tpDistances = signal.TakeProfits
                .Select(tp => Math.Abs(tp - signal.EntryPrice))
                .Where(d => d > 0)
                .OrderBy(d => d)
                .ToList();

            if (tpDistances.Count == 0)
                return FastFailResult.Fail("RR", "tpDist<=0");

            decimal tpDist = tpDistances.Count == 1
                ? tpDistances[0]
                : tpDistances[tpDistances.Count / 2];

            var rr = tpDist / slDist;

            // абсолютный мусор фильтр
            if (rr <= 0.6m)
                return FastFailResult.Fail("RR", $"rr={rr:F2} too low");

            // ============================================================
            // 3️⃣ RELAX MODE
            // ============================================================

            if (relaxRr)
            {
                if (rr < 1.0m)
                    return FastFailResult.Fail("RR", "relaxRR floor");

                return FastFailResult.Ok();
            }

            // ============================================================
            // 4️⃣ BASE RR
            // ============================================================

            var minRr = GetDynamicMinRr(symbol, tf, smart, signal);

            // ============================================================
            // 5️⃣ AI MULTIPLIER
            // ============================================================

            decimal w = 1.0m;

            try
            {
                w = _aiLearning.GetGateMultiplier(symbol, smart.BaseRegime, "RR");
            }
            catch { }

            w = Math.Clamp(w, 0.75m, 1.25m);

            minRr *= w;

            // ============================================================
            // 6️⃣ FAST TREND OVERRIDE (single unified logic)
            // ============================================================

            bool fastTrend = IsFastTrendOverride(smart);

            if (fastTrend)
                minRr *= 0.75m;

            // ============================================================
            // 7️⃣ FINAL CLAMP
            // ============================================================

            minRr = Math.Clamp(minRr, 1.0m, 3.0m);

            // ============================================================
            // 8️⃣ FINAL CHECK
            // ============================================================

            if (rr < minRr)
            {
                return FastFailResult.Fail(
                    "RR",
                    $"rr={rr:F2}<min={minRr:F2} (w={w:F2}, fastTrend={fastTrend})"
                );
            }

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
            if (signal == null) return FastFailResult.Ok();

            bool rangeLike =
                smart.BaseRegime == MarketRegime.Range ||
                smart.SmartType == SmartRegimeType.SmartRange;

            if (rangeLike && allowCounterTrendInRangeLike)
                return FastFailResult.Ok();

            if (klines == null || klines.Count < 20)
                return FastFailResult.Ok(); // 🔥 FIX: не блокируем

            decimal atr = signal.Atr ?? Atr(klines, 14, klines.Count - 1);
            if (atr <= 0m) return FastFailResult.Ok();

            var phase = DetectTrendPhase(klines, atr, smart.TrendSlopePercent);

            bool endPhase =
                phase == TrendPhase.Distribution ||
                phase == TrendPhase.Exhaustion;

            if (endPhase)
            {
                if (smart.TrendSlopePercent > 0 && signal.Side == SignalSide.Sell)
                {
                    if (IsConfirmedReversal(klines, klines.Count - 1, atr, smart, SignalSide.Sell))
                        return FastFailResult.Ok();
                }

                if (smart.TrendSlopePercent < 0 && signal.Side == SignalSide.Buy)
                {
                    if (IsConfirmedReversal(klines, klines.Count - 1, atr, smart, SignalSide.Buy))
                        return FastFailResult.Ok();
                }
            }

            var slopeLock = 0.008m + smart.VolatilityPercent * 1.2m;
            slopeLock = Math.Clamp(slopeLock, 0.006m, 0.025m);

            bool slopeUp = smart.TrendSlopePercent > slopeLock;
            bool slopeDown = smart.TrendSlopePercent < -slopeLock;

            if (slopeUp && signal.Side == SignalSide.Sell)
                signal.Confidence *= 0.88m;

            if (slopeDown && signal.Side == SignalSide.Buy)
                signal.Confidence *= 0.88m;

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
            if (ct.IsCancellationRequested)
            {
                _logger.LogDebug("[LIQ] cancelled → soft-pass");
                return FastFailResult.Ok();
            }

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
                    // 🔥 allow strong signals
                    var conf = signal.Confidence ?? 0m;

                    bool strongSignal = conf >= 0.45m || signal.IsSuperSignal;

                    // 🔥 allow continuation / momentum setups
                    bool momentum =
                        signal.Reason == "CONTINUATION" ||
                        signal.Reason == "EARLY_TREND";

                    // 🔥 allow if aligned with trend pressure
                    bool aligned =
                        (smart.TrendSlopePercent < 0 && signal.Side == SignalSide.Sell) ||
                        (smart.TrendSlopePercent > 0 && signal.Side == SignalSide.Buy);

                    // 🔥 если сильный и по тренду — НЕ БЛОКИРУЕМ
                    if ((strongSignal && aligned) || momentum)
                    {
                        signal.Confidence *= 0.82m;

                        _engineState.LastEntryDecision = "WARN_LIQ_GUARD_STRONG_PASS";
                        LastBlockedByLiquidity = false;

                        return FastFailResult.Ok();
                    }

                    // 🔥 иначе — реально блокируем
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
                return FastFailResult.Ok(); // 🔥 FIX: НЕ блокируем
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
                    // ❗ ЭТО НЕ BLOCK — это просто нет улучшений

                    var w = 1.0m;
                    try
                    {
                        w = _aiLearning.GetGateMultiplier(signal.Symbol, smart.BaseRegime, "LIQ");
                    }
                    catch (OperationCanceledException)
                    {
                        return FastFailResult.Ok(); // НЕ блокировать
                    }

                    w = Math.Clamp(w, 0.7m, 1.5m);

                    // 👉 только soft penalty
                    if (!signal.IsSuperSignal)
                    {
                        var penalty = w >= 1.2m ? 0.82m : 0.92m;
                        signal.Confidence *= penalty;

                        _engineState.LastEntryDecision = "WARN_LIQ_CLUSTER";
                    }

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

                // ------------------------------------------------------------------
                // 1) AI multiplier (defensive)
                // ------------------------------------------------------------------
                var w = 1.0m;
                try
                {
                    w = _aiLearning.GetGateMultiplier(symbol, smart.BaseRegime, "EXPO");
                }
                catch { /* non-critical */ }

                // Clamp AI influence to sane bounds
                w = Math.Clamp(w, 0.7m, 1.3m);

                // ------------------------------------------------------------------
                // 2) Edge score normalization (CRITICAL)
                // ------------------------------------------------------------------
                var aiEdgeScore = smart.Confidence * w;
                aiEdgeScore = Math.Clamp(aiEdgeScore, 0.0m, 1.0m);

                // ------------------------------------------------------------------
                // 3) Exposure decision
                // ------------------------------------------------------------------
                var res = CanIncreaseExposure(
                    state: es,
                    symbol: symbol,
                    symbolNotionalUsd: 0m, // intentionally 0; executor/supervisor checks real notional
                    equityUsd: es.EquityUsd,
                    usedMarginUsd: es.UsedMarginUsd,
                    aiEdgeScore: aiEdgeScore,
                    isSpecialSetup: signal.IsSuperSignal,
                    isHighVolatility: smart.VolatilityPercent >= 0.015m,
                    isLowEquityMode: es.EquityUsd < 500m
                );

                // ------------------------------------------------------------------
                // 4) BLOCK handling
                // ------------------------------------------------------------------
                if (!res.AllowAdd)
                {
                    // UI / EngineState
                    _engineState.LastEntryDecision = "BLOCKED_EXPOSURE";
                    CurrentMode = "Blocked:EXPO";

                    // AI trace (fail-safe)
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
        //==============================================BTC HTF BLOCK

        bool VolumeSpike(IReadOnlyList<BinanceFuturesUsdtKline> klines, int i)
        {
            decimal avg = 0m;

            for (int j = i - 20; j < i; j++)
                avg += klines[j].Volume;

            avg /= 20m;

            return klines[i].Volume > avg * 2.5m;
        }

        bool IsExhaustion(IReadOnlyList<BinanceFuturesUsdtKline> klines, int i,  decimal atr)
        {
            int bullish = 0;

            for (int j = i - 4; j <= i; j++)
            {
                if (klines[j].ClosePrice > klines[j].OpenPrice)
                    bullish++;
            }

            decimal move =
                Math.Abs(
                    klines[i].ClosePrice -
                    klines[i - 4].OpenPrice
                );

            return bullish >= 4 && move > atr * 3.0m;
        }

        private TradeSignal? TryImpulseContinuation(string symbol, KlineInterval tf, IReadOnlyList<BinanceFuturesUsdtKline> klines, SmartRegimeInfo smart)
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

            if (VolumeSpike(klines, i))
                return null;

            if (IsExhaustion(klines, i, atr))
                return null;

            decimal ema21 = EmaClose(klines, 21, i);

            // --- distance filter (CORE) ---
            decimal dist = Math.Abs(c0.ClosePrice - ema21);
            decimal minDist = atr * 0.9m;
            decimal maxDist = atr * 2.1m;

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
        private TradeSignal? TryEarlyTrendJoin(string symbol, KlineInterval tf, IReadOnlyList<BinanceFuturesUsdtKline> klines, SmartRegimeInfo smart)
        {
            if (klines == null || klines.Count < 120) return null;

            int i = klines.Count - 1;
            var c0 = klines[i];
            var c1 = klines[i - 1];
            var c2 = klines[i - 2];

            decimal atr = Atr(klines, 14, i);
            if (atr <= 0m) return null;

            // GLOBAL MARKET FILTERS
            if (VolumeSpike(klines, i))
                return null;

            if (IsExhaustion(klines, i, atr))
                return null;

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
            decimal minDist = atr * 0.25m;
            decimal maxDist = atr * 0.90m;

            if (dist < minDist || dist > maxDist)
                return null;

            // 3) Импульсность: последние 1-2 свечи должны быть "смысловые"
            //decimal body0 = Math.Abs(c0.ClosePrice - c0.ClosePrice);
            //decimal body1 = Math.Abs(c1.ClosePrice - c1.ClosePrice);
            decimal body0 = Math.Abs(c0.ClosePrice - c0.OpenPrice);
            decimal body1 = Math.Abs(c1.ClosePrice - c1.OpenPrice);

            bool climax = body0 > atr * 1.4m;

            bool impulseOk =
                !climax &&
                (
                    body0 >= atr * 0.45m ||
                    (body0 + body1) >= atr * 0.80m
                );

            if (!impulseOk) return null;

            // 4) Поддержка тренда: EMA21 должна быть не "мертвая"
            //    (минимальный наклон/драйв, но без overfit)
            decimal slopeAbs = Math.Abs(smart.TrendSlopePercent);
            bool slopeOk =
     slopeAbs >= 0.0012m &&
     smart.VolatilityPercent >= 0.015m;

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
                int rStart = Math.Max(1, last - 20);
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
                decimal s1 = 1m - Math.Clamp((rOverAtr - 0.70m) / 0.60m, 0m, 1m);     // <~0.7atr => high
                decimal s2 = 1m - Math.Clamp((atrPct - 0.010m) / 0.010m, 0m, 1m);     // <1% => high
                decimal squeezeScore = Math.Clamp(0.60m * s1 + 0.40m * s2, 0m, 1m);

                // bias dir
                int dir = slope >= 0.0012m ? +1 : slope <= -0.0012m ? -1 : 0;

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
            // safety guard
            if (klines == null || klines.Count < 80)
                return null;

            int last = klines.Count - 1;

            if (last < 30)
                return null;

            var c = klines[last];
            var prev = klines[last - 1];

            // ATR
            decimal atr = Atr(klines, 14, last);
            if (atr <= 0)
                return null;

            decimal price = c.ClosePrice;
            if (price <= 0)
                return null;

            // ===================== HTF STATE =====================

            var h4 = await GetOrUpdateBtcHtfStateAsync(symbol, KlineInterval.FourHour, ct).ConfigureAwait(false);
            var d1 = await GetOrUpdateBtcHtfStateAsync(symbol, KlineInterval.OneDay, ct).ConfigureAwait(false);

            if (!h4.Valid || !d1.Valid)
                return null;

            bool htfSqueezeOk = h4.SqueezeScore >= 0.62m && h4.AtrPct <= 0.014m;
            bool d1StableOk = d1.AtrPct <= 0.022m;

            if (!htfSqueezeOk || !d1StableOk)
                return null;

            int bias = h4.BiasDir;
            if (bias == 0)
                return null;

            if (d1.BiasDir != 0 && Math.Sign(d1.BiasDir) != Math.Sign(bias))
                return null;

            // ===================== EXPANSION BAR =====================

            decimal body = Math.Abs(c.ClosePrice - c.OpenPrice);

            if (body < atr * 0.35m)
                return null;

            if (IsTooBigImpulseBar(c, prev, atr))
                return null;

            // ===================== VOLUME SHOCK =====================

            decimal avgVol20 = 0m;

            int volStart = last - 20;

            for (int i = volStart; i < last; i++)
                avgVol20 += klines[i].Volume;

            avgVol20 /= 20m;

            if (avgVol20 <= 0)
                return null;

            if (c.Volume < avgVol20 * 1.8m)
                return null;

            // ===================== CLOSE POSITION =====================

            decimal range = c.HighPrice - c.LowPrice;

            if (range <= 0)
                return null;

            bool closeNearHigh = (c.HighPrice - c.ClosePrice) <= range * 0.22m;
            bool closeNearLow = (c.ClosePrice - c.LowPrice) <= range * 0.22m;

            // ===================== BREAKOUT RANGE =====================

            decimal hi20 = decimal.MinValue;
            decimal lo20 = decimal.MaxValue;

            for (int i = last - 20; i < last; i++)
            {
                var k = klines[i];

                if (k.HighPrice > hi20)
                    hi20 = k.HighPrice;

                if (k.LowPrice < lo20)
                    lo20 = k.LowPrice;
            }

            bool longBreak =
                c.ClosePrice >= hi20 + atr * 0.15m &&
                closeNearHigh;

            bool shortBreak =
                c.ClosePrice <= lo20 - atr * 0.15m &&
                closeNearLow;

            if (bias > 0 && !longBreak)
                return null;

            if (bias < 0 && !shortBreak)
                return null;

            // ===================== SIGNAL BUILD =====================

            var (slMult, tp1Mult, tp2Mult, tp3Mult) = tf switch
            {
                KlineInterval.FifteenMinutes => (1.6m, 1.4m, 2.2m, 3.2m),
                KlineInterval.OneHour => (2.0m, 1.6m, 2.6m, 3.8m),
                _ => (1.8m, 1.5m, 2.4m, 3.4m)
            };

            if (longBreak)
            {
                decimal entry = c.ClosePrice;
                decimal sl = c.LowPrice - atr * slMult;

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
            // bind confidence
            signal.Confidence = Math.Max(smart.Confidence, cfg.MinEntry + 0.05m);

            _engineState.LastEntryDecision = reason;
            CurrentMode = reason;

            // RR
            trace.Add(Gate4_RR(symbol, tf, signal, smart, relaxRr));
            if (!trace.Allow) return Finalize(trace, smart);

            // Liquidity (guard + cluster)
            var g6 = await Gate6_LiquidityAsync(signal, smart, klines, tf, relaxLiquidity, ct).ConfigureAwait(false);
            trace.Add(g6);
            if (!trace.Allow) return Finalize(trace, smart);

            // Exposure
            trace.Add(Gate7_Exposure(symbol, tf, signal, smart));
            if (!trace.Allow) return Finalize(trace, smart);

            trace.Allow = true;
            trace.Signal = signal;
            return Finalize(trace, smart);
        }

        //===========================================================================END BTC HTF BLOCK

        private bool IsParabolicMove(IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            int n = klines.Count;

            var c1 = klines[n - 1].ClosePrice;
            var c2 = klines[n - 2].ClosePrice;
            var c3 = klines[n - 3].ClosePrice;

            return (c1 > c2 && c2 > c3) &&
                   (c1 - c3) / c3 > 0.006m;
        }
        private bool IsAgainstMicroMomentum(
        IReadOnlyList<BinanceFuturesUsdtKline> klines,
        SignalSide side)
        {
            int n = klines.Count;

            var last = klines[n - 1];
            var prev = klines[n - 2];

            if (side == SignalSide.Buy)
                return last.ClosePrice < prev.ClosePrice;

            if (side == SignalSide.Sell)
                return last.ClosePrice > prev.ClosePrice;

            return false;
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

            // reset engine-state per decision
            _engineState.LastDecisionTime = DateTime.UtcNow;
            _engineState.LastEntryDecision = "EVALUATING";
            _engineState.BlockedByLiquidity = false;
            _engineState.SoftEntry = false;
            LastSoftEntry = false;
            LastBlockedByLiquidity = false;
            CurrentMode = "Detecting";

            bool testMode = _test.Enabled;
            bool relaxRr = testMode && _test.RelaxRR;
            bool relaxPatternBlock = testMode && _test.RelaxPatternBlock;
            bool relaxLiquidity = testMode && _test.RelaxLiquidity;
            bool lowerRegimeThreshold = testMode && _test.LowerRegimeThreshold;

            try
            {
                // ✅ Проверка входных данных
                if (klines == null || klines.Count < 3)
                {
                    trace.Allow = false;
                    trace.Signal = null;
                    trace.Gates.Add(FastFailResult.Fail("DATA", "Insufficient Klines"));
                    return Finalize(trace, null);
                }

                // -----------------------------
                // Gate0: Data sanity check
                // -----------------------------
                var g0 = Gate0_Data(symbol, tf, klines);
                trace.Add(g0);
                if (!g0.Allow) return Finalize(trace, null);

                // Decision marker
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
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[DECISION_MARKER] Failed to add marker for {symbol} {tf}", symbol, tf);
                }

                // -----------------------------
                // Gate1: Smart Regime
                // -----------------------------
                var g1 = Gate1_SmartRegime(symbol, tf, klines, out SmartRegimeInfo s);
                smart = s;
                trace.Add(g1);
                if (!g1.Allow) return Finalize(trace, smart);

                // Block ultra-low timeframe
                if (tf == KlineInterval.OneMinute)
                {
                    trace.Allow = false;
                    trace.Gates.Add(FastFailResult.Fail("TF", "1m blocked for signal"));
                    return Finalize(trace, smart);
                }

                // BTC Macro Expansion
                if (GetProfile(symbol) == MarketProfileType.BtcMacro)
                {
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

                // Gate2 pre-check / telemetry
                //var g2Pre = Gate2_Confidence(smart, lowerRegimeThreshold, symbol);
                //trace.Add(g2Pre);

                // Volatility Expansion
                if (IsVolatilityExpansionAllowed(smart))
                {
                    var veSignal = TryVolatilityExpansionEntry(symbol, tf, klines);
                    if (veSignal != null)
                    {
                        veSignal.IsSuperSignal = true;

                        if (veSignal.IsSuperSignal)
                        {
                            veSignal.Confidence = Math.Min(veSignal.Confidence ?? 0, 0.75m);
                        }
                        _engineState.LastEntryDecision = "VOLATILITY_EXPANSION";
                        CurrentMode = "VolatilityExpansion";

                        trace.Allow = true;
                        trace.Signal = veSignal;

                        trace.Add(Gate4_RR(symbol, tf, veSignal, smart, relaxRr));
                        if (!trace.Allow) return Finalize(trace, smart);

                        trace.Add(await Gate6_LiquidityAsync(veSignal, smart, klines, tf, relaxLiquidity, ct));
                        if (!trace.Allow) return Finalize(trace, smart);

                        trace.Add(Gate7_Exposure(symbol, tf, veSignal, smart));
                        if (!trace.Allow) return Finalize(trace, smart);

                        return Finalize(trace, smart);
                    }
                }
                // =========================
                // Gate3 BaseSignal
                // =========================
                var g3 = Gate3_BaseSignal(symbol, tf, klines, smart, out baseSignal);
                trace.Add(g3);
                if (!g3.Allow || baseSignal == null)
                    return Finalize(trace, smart);

                // =========================
                // CONFIDENCE (PRO)
                // =========================
                var conf = _confidenceAgg.Evaluate(smart, baseSignal, tf);
                decimal finalConfidence = conf?.Final ?? smart.Confidence;
                int lastIndex = klines.Count - 1;

                // --- PARABOLIC ---
                if (IsParabolicMove(klines))
                {
                    finalConfidence *= 0.82m;
                    _engineState.LastEntryDecision = "WARN_PARABOLIC";
                }

                // --- MICRO MOMENTUM ---
                if (IsAgainstMicroMomentum(klines, baseSignal.Side))
                {
                    finalConfidence *= 0.90m;
                    _engineState.LastEntryDecision = "WARN_MICRO_MOMENTUM";
                }

                // --- ABSORPTION ---
                if (IsAbsorption(klines, lastIndex))
                {
                    finalConfidence *= 0.85m;
                    _engineState.LastEntryDecision = "WARN_ABSORPTION";
                }

                // --- FAKE MOMENTUM ---
                if (IsFakeMomentum(klines, lastIndex))
                {
                    finalConfidence *= 0.88m;
                    _engineState.LastEntryDecision = "WARN_FAKE_MOMENTUM";
                }

                // --- TRAP FILTER ---
                var trap = Gate_TrapFilter(klines, baseSignal, out var trapMult);
                trace.Add(trap);
                finalConfidence *= trapMult;

                // --- NO REACTION AFTER BREAK ---
                if (NoReactionAfterBreak(klines, lastIndex, baseSignal.Side))
                {
                    finalConfidence *= 0.87m;
                    _engineState.LastEntryDecision = "WARN_NO_REACTION";
                }

                // --- BAD ENTRY LOCATION ---
                if (IsBadEntryLocation(klines, baseSignal, smart))
                {
                    finalConfidence *= 0.88m;
                    _engineState.LastEntryDecision = "WARN_BAD_LOCATION";
                    _logger.LogInformation("[BAD_LOCATION] {symbol} {tf} conf adjusted {conf:F2}", symbol, tf, finalConfidence);
                }

                // --- EMA DISTANCE / PULLBACK ---
                var c = klines[^1];
                var ema21 = Ema(klines, 21, lastIndex);
                var atr = Atr(klines, 14, lastIndex);

                if (Math.Abs(c.ClosePrice - ema21) > atr * 1.2m)
                {
                    finalConfidence *= 0.82m;
                    _engineState.LastEntryDecision = "WARN_NO_PULLBACK";
                    _logger.LogInformation("[PULLBACK] {symbol} {tf} conf adjusted {conf:F2}", symbol, tf, finalConfidence);
                }

                // --- APPLY FINAL CONFIDENCE ---
                baseSignal.Confidence = finalConfidence;

                // =========================
                // ENGINE STATE (1 РАЗ!)
                // =========================
                Confidence = finalConfidence;
                _engineState.ConfidenceRaw = finalConfidence;
                _engineState.ConfidencePercent = (int)(finalConfidence * 100);
                var cfg = _confidenceCfg.Resolve(symbol);
                _engineState.ConfidenceLevel =
                    finalConfidence >= cfg.Bands.HighFrom ? "HIGH" :
                    finalConfidence >= cfg.Bands.MediumFrom ? "MEDIUM" :
                    finalConfidence >= cfg.MinEntry ? "LOW" :
                    "BELOW_ENTRY";

                // --- LOGGING ---
                _logger.LogInformation(
                    "[TRAP] {symbol} {tf} mult={mult:F2} conf={conf:F2}",
                    symbol,
                    tf,
                    trapMult,
                    finalConfidence
                );


                // =========================
                // SOFT CONFIDENCE GATE
                // =========================
                trace.Add(Gate2_ConfidenceHybrid(finalConfidence, smart, lowerRegimeThreshold, symbol));
                if (!trace.Allow) return Finalize(trace, smart);

                trace.Add(Gate2_5_TrendPhaseLock(klines, smart, baseSignal));
                if (!trace.Allow) return Finalize(trace, smart);

                trace.Add(Gate3_5_DirectionLock(symbol, tf, klines, baseSignal, smart, allowCounterTrendInRangeLike: true));
                if (!trace.Allow) return Finalize(trace, smart);

                var late = Gate3_2_LateEntryFilter(symbol, tf, klines, baseSignal, smart);
                trace.Add(late);
                if (!late.Allow)
                {
                    finalConfidence *= 0.82m;
                    baseSignal.Confidence = finalConfidence;
                    _engineState.LastEntryDecision = "WARN_LATE_ENTRY";
                }

                trace.Add(Gate4_RR(symbol, tf, baseSignal, smart, relaxRr));
                if (!trace.Allow) return Finalize(trace, smart);

                trace.Add(Gate5_Pattern(symbol, tf, klines, baseSignal, relaxPatternBlock));
                if (!trace.Allow) return Finalize(trace, smart);

                var g6 = await Gate6_LiquidityAsync(baseSignal, smart, klines, tf, relaxLiquidity, ct).ConfigureAwait(false);
                trace.Add(g6);
                if (!g6.Allow) return Finalize(trace, smart);

                trace.Add(Gate7_Exposure(symbol, tf, baseSignal, smart));
                if (!trace.Allow) return Finalize(trace, smart);

                // -----------------------------
                // FINAL
                // -----------------------------
                trace.Allow = true;
                trace.Signal = baseSignal;
                _engineState.LastEntryDecision = "SIGNAL_READY";
                CurrentMode = "Allowed";

                try
                {
                    c = klines[^1];
                    _decisionMarkers.Add(new DecisionMarkerDto
                    {
                        Symbol = symbol,
                        Timeframe = tf.ToString(),
                        CandleTimeUtc = c.CloseTime,
                        Type = DecisionMarkerType.EntryAllowed,
                        Code = "ENTRY_ALLOWED",
                        Details = $"{baseSignal.Reason} conf={finalConfidence:P0}",
                        Metrics = new Dictionary<string, decimal>
                        {
                            ["confidence"] = finalConfidence,
                            ["riskBias"] = smart.RiskBias
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[DECISION_MARKER] Failed to add EntryAllowed marker for {symbol} {tf}", symbol, tf);
                }

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

        private bool IsAbsorption(
    IReadOnlyList<BinanceFuturesUsdtKline> klines,
    int last)
        {
            if (last < 5) return false;

            decimal move = Math.Abs(
                klines[last].ClosePrice - klines[last - 5].ClosePrice);

            decimal vol = 0m;
            for (int i = last - 5; i <= last; i++)
                vol += klines[i].Volume;

            decimal avgVol = vol / 5m;

          
            return
    klines[last].Volume > avgVol * 1.5m &&
    move < klines[last].ClosePrice * 0.002m;
        }
        private bool IsFakeMomentum(
    IReadOnlyList<BinanceFuturesUsdtKline> klines,
    int last)
        {
            var c = klines[last];
            var p = klines[last - 1];

            decimal body = Math.Abs(c.ClosePrice - c.OpenPrice);
            decimal range = c.HighPrice - c.LowPrice;

            if (range <= 0) return false;

            decimal bodyRatio = body / range;

            // большой range, но слабое закрытие
            return
      bodyRatio < 0.35m &&
      (c.HighPrice - Math.Max(c.OpenPrice, c.ClosePrice)) > range * 0.4m;
        }

        private bool NoReactionAfterBreak(
        IReadOnlyList<BinanceFuturesUsdtKline> klines,
        int last,
        SignalSide side)
        {
            if (last < 2) return false;

            var c = klines[last];       // текущая
            var p = klines[last - 1];   // пробой
            var pp = klines[last - 2];  // уровень до пробоя

            if (side == SignalSide.Buy)
            {
                // пробили вверх, но нет продолжения
                return
                    p.ClosePrice > pp.HighPrice &&
                    c.ClosePrice <= p.ClosePrice;
            }
            else
            {
                // пробили вниз, но нет продолжения
                return
                    p.ClosePrice < pp.LowPrice &&
                    c.ClosePrice >= p.ClosePrice;
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

        private bool IsBadEntryLocation(
     IReadOnlyList<BinanceFuturesUsdtKline> klines,
     TradeSignal signal,
     SmartRegimeInfo smart)
        {
            int lookback = 50;
            int last = klines.Count - 1;

            int start = Math.Max(0, last - lookback);

            decimal hi = klines.Skip(start).Max(x => x.HighPrice);
            decimal lo = klines.Skip(start).Min(x => x.LowPrice);

            if (hi == lo) return false;

            decimal price = klines[last].ClosePrice;
            decimal pos = (price - lo) / (hi - lo);

            bool strongTrend =
                smart.BaseRegime == MarketRegime.StrongUpTrend ||
                smart.BaseRegime == MarketRegime.StrongDownTrend;

            // 🔥 В тренде не мешаем
            if (strongTrend)
                return false;

            if (signal.Side == SignalSide.Buy && pos > 0.85m)
                return true;

            if (signal.Side == SignalSide.Sell && pos < 0.15m)
                return true;

            return false;
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
            foreach (var kv in _lastStopTime.ToArray())
            {
                if (kv.Value < cutoff)
                    _lastStopTime.TryRemove(kv.Key, out _);
            }
        }

    }
}
