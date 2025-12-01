//  ======================================================================================
//   STRATEGY ENGINE — VERSION 5.0 (FULL)
//   ✓ SmartRegime Adaptive Threshold
//   ✓ Liquidity Grab V2
//   ✓ Pullback EMA21 Pro
//   ✓ SuperSignal Logic
//   ✓ AI Risk Weight
//   ✓ Clean Entry/SL fix
//   ✓ No Zero QTY
//   ✓ Manipulation Filters
//   ✓ ATR/EMA unified engine
//   ✓ NO CHANGES TO PUBLIC API — FULLY COMPATIBLE
//  ======================================================================================

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


        // ============================================================================
        // ATR helper
        // ============================================================================
        private static decimal Atr(IReadOnlyList<BinanceFuturesUsdtKline> klines, int period, int last)
        {
            if (last <= 0 || last >= klines.Count) return 0;
            if (klines.Count < period + 2) return 0;

            int start = last - period + 1;
            if (start < 1) start = 1;

            decimal sumTr = 0;

            for (int i = start; i <= last; i++)
            {
                var c = klines[i];
                var p = klines[i - 1];

                decimal tr1 = c.HighPrice - c.LowPrice;
                decimal tr2 = Math.Abs(c.HighPrice - p.ClosePrice);
                decimal tr3 = Math.Abs(c.LowPrice - p.ClosePrice);

                sumTr += Math.Max(tr1, Math.Max(tr2, tr3));
            }

            int bars = last - start + 1;
            if (bars <= 0) return 0;

            return sumTr / bars;
        }

        // ============================================================================
        // EMA helper
        // ============================================================================
        private static decimal Ema(IReadOnlyList<BinanceFuturesUsdtKline> kl, int period, int idx)
        {
            int start = Math.Max(0, idx - period + 1);
            decimal k = 2m / (period + 1);

            decimal ema = kl[start].ClosePrice;
            for (int i = start + 1; i <= idx; i++)
                ema = kl[i].ClosePrice * k + ema * (1 - k);

            return ema;
        }


        // ============================================================================
        // BAR FILTERS (Impulse/Dodji)
        // ============================================================================
        private static decimal TrueRange(BinanceFuturesUsdtKline c, BinanceFuturesUsdtKline p)
        {
            var tr1 = c.HighPrice - c.LowPrice;
            var tr2 = Math.Abs(c.HighPrice - p.ClosePrice);
            var tr3 = Math.Abs(c.LowPrice - p.ClosePrice);
            return Math.Max(tr1, Math.Max(tr2, tr3));
        }

        private static bool IsImpulse(BinanceFuturesUsdtKline c, BinanceFuturesUsdtKline p, decimal atr)
        {
            if (atr <= 0) return false;
            return TrueRange(c, p) >= atr * 2.2m;
        }

        private static bool IsDodji(BinanceFuturesUsdtKline c, decimal atr)
        {
            if (atr <= 0) return true;
            return Math.Abs(c.ClosePrice - c.OpenPrice) < atr * 0.08m;
        }


        // ============================================================================
        // ATR CONFIG
        // ============================================================================
        private static (decimal slMult, decimal tp1, decimal tp2, decimal tp3)
            AtrConf(KlineInterval tf)
        {
            return tf switch
            {
                KlineInterval.OneMinute or KlineInterval.FiveMinutes =>
                    (0.8m, 1.5m, 2.3m, 3.2m),

                KlineInterval.FifteenMinutes =>
                    (1.2m, 1.6m, 2.4m, 3.4m),

                KlineInterval.OneHour or KlineInterval.FourHour =>
                    (2.0m, 1.8m, 2.8m, 3.8m),

                KlineInterval.OneDay =>
                    (2.5m, 2.0m, 3.0m, 4.5m),

                _ => (1.0m, 1.5m, 2.3m, 3.2m)
            };
        }


        // ============================================================================
        // RANGE LOGIC — Liquidity Grab V2
        // ============================================================================
        private TradeSignal? LiquidityGrab(
            string symbol,
            KlineInterval tf,
            IReadOnlyList<BinanceFuturesUsdtKline> kl)
        {
            if (kl.Count < 30) return null;

            int i = kl.Count - 1;
            var c = kl[i];
            var p = kl[i - 1];

            decimal atr = Atr(kl, 14, i);
            if (atr <= 0) return null;

            if (IsImpulse(c, p, atr)) return null;
            if (IsDodji(c, atr)) return null;

            var (slM, tp1M, tp2M, tp3M) = AtrConf(tf);

            // LONG grab
            bool lg =
                c.LowPrice < p.LowPrice &&
                c.ClosePrice > p.LowPrice &&
                (p.LowPrice - c.LowPrice) > atr * 0.4m &&
                c.ClosePrice > c.OpenPrice;

            if (lg)
            {
                decimal entry = p.LowPrice + atr * 0.2m;
                decimal sl = c.LowPrice - atr * slM;

                return new TradeSignal
                {
                    Symbol = symbol,
                    Side = SignalSide.Buy,
                    EntryPrice = entry,
                    StopLoss = sl,
                    TakeProfits = new()
                    {
                        entry + atr * tp1M,
                        entry + atr * tp2M,
                        entry + atr * tp3M
                    },
                    Atr = atr,
                    Reason = "LIQUIDITY_GRAB_LONG",
                    Timeframe = tf.ToString(),
                    Time = c.CloseTime,
                    IsSuperSignal = true
                };
            }

            // SHORT grab
            bool sg =
                c.HighPrice > p.HighPrice &&
                c.ClosePrice < p.HighPrice &&
                (c.HighPrice - p.HighPrice) > atr * 0.4m &&
                c.ClosePrice < c.OpenPrice;

            if (sg)
            {
                decimal entry = p.HighPrice - atr * 0.2m;
                decimal sl = c.HighPrice + atr * slM;

                return new TradeSignal
                {
                    Symbol = symbol,
                    Side = SignalSide.Sell,
                    EntryPrice = entry,
                    StopLoss = sl,
                    TakeProfits = new()
                    {
                        entry - atr * tp1M,
                        entry - atr * tp2M,
                        entry - atr * tp3M
                    },
                    Atr = atr,
                    Reason = "LIQUIDITY_GRAB_SHORT",
                    Timeframe = tf.ToString(),
                    Time = c.CloseTime,
                    IsSuperSignal = true
                };
            }

            return null;
        }


        // ============================================================================
        // TREND LOGIC — Pullback EMA21
        // ============================================================================
        private TradeSignal? PullbackEma21(
            string symbol,
            KlineInterval tf,
            IReadOnlyList<BinanceFuturesUsdtKline> kl)
        {
            int i = kl.Count - 1;
            if (i < 1) return null;

            var c = kl[i];
            var p = kl[i - 1];

            decimal atr = Atr(kl, 14, i);
            if (atr <= 0) return null;

            if (IsImpulse(c, p, atr)) return null;
            if (IsDodji(c, atr)) return null;

            decimal ema = Ema(kl, 21, i);
            var (slM, tp1M, tp2M, tp3M) = AtrConf(tf);

            bool bull =
                c.ClosePrice > c.OpenPrice &&
                c.LowPrice <= ema &&
                c.ClosePrice > ema;

            if (bull)
            {
                decimal entry = ema;
                decimal sl = c.LowPrice - atr * slM;

                return new TradeSignal
                {
                    Symbol = symbol,
                    Side = SignalSide.Buy,
                    EntryPrice = entry,
                    StopLoss = sl,
                    Atr = atr,
                    Time = c.CloseTime,
                    Timeframe = tf.ToString(),
                    Reason = "PULLBACK_EMA21_LONG",
                    TakeProfits = new()
                    {
                        entry + atr * tp1M,
                        entry + atr * tp2M,
                        entry + atr * tp3M
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
                decimal sl = c.HighPrice + atr * slM;

                return new TradeSignal
                {
                    Symbol = symbol,
                    Side = SignalSide.Sell,
                    EntryPrice = entry,
                    StopLoss = sl,
                    Atr = atr,
                    Time = c.CloseTime,
                    Timeframe = tf.ToString(),
                    Reason = "PULLBACK_EMA21_SHORT",
                    TakeProfits = new()
                    {
                        entry - atr * tp1M,
                        entry - atr * tp2M,
                        entry - atr * tp3M
                    }
                };
            }

            return null;
        }


        // ============================================================================
        // SOFT TREND SIGNAL
        // ============================================================================
        private TradeSignal? SoftTrend(
            string symbol,
            KlineInterval tf,
            IReadOnlyList<BinanceFuturesUsdtKline> kl,
            SmartRegimeInfo smart)
        {
            int i = kl.Count - 1;
            if (i < 1) return null;

            var c = kl[i];
            var p = kl[i - 1];

            decimal atr = Atr(kl, 14, i);
            if (atr <= 0) return null;

            if (IsImpulse(c, p, atr)) return null;
            if (IsDodji(c, atr)) return null;

            bool up =
                smart.TrendSlopePercent > 0 &&
                smart.Confidence >= 0.40m;

            bool down =
                smart.TrendSlopePercent < 0 &&
                smart.Confidence >= 0.40m;

            if (!up && !down) return null;

            var side = up ? SignalSide.Buy : SignalSide.Sell;

            decimal entry = c.ClosePrice;
            decimal sl = up
                ? entry - atr * 0.6m
                : entry + atr * 0.6m;

            decimal tp1 = up ? entry + atr * 1.0m : entry - atr * 1.0m;
            decimal tp2 = up ? entry + atr * 1.6m : entry - atr * 1.6m;

            return new TradeSignal
            {
                Symbol = symbol,
                Side = side,
                EntryPrice = entry,
                StopLoss = sl,
                Atr = atr,
                Time = c.CloseTime,
                Timeframe = tf.ToString(),
                Reason = "SOFT_TREND_PROBE",
                TakeProfits = new() { tp1, tp2 }
            };
        }


        // ============================================================================
        // MAIN SIGNAL
        // ============================================================================
        public TradeSignal? GenerateSignal(
            string symbol,
            KlineInterval tf,
            IReadOnlyList<BinanceFuturesUsdtKline> kl)
        {
            _logger.LogInformation("[DEBUG][{S}][{TF}] STRATEGY START", symbol, tf);

            if (kl == null || kl.Count < 30) return null;

            int last = kl.Count - 1;
            if (last < 1) return null;

            // BTC correlation
            if (!symbol.Equals("BTCUSDT", StringComparison.OrdinalIgnoreCase))
            {
                var corr = _correlationService.GetCorrelation("BTCUSDT", symbol);
                if (corr.HasValue && Math.Abs(corr.Value) < 0.10m)
                {
                    _logger.LogInformation(
                        "[DEBUG][{S}][{TF}] CorrFilter: {C:F2} < 0.10 → skip",
                        symbol, tf, corr.Value);
                    return null;
                }
            }

            // SmartRegime
            var smart = _smartRegimeService.Evaluate(symbol, tf, kl);
            var regime = smart.BaseRegime;

            _logger.LogInformation(
                "[DEBUG][{S}][{TF}] REGIME={R} smart={T} slope={SL:P2} vol={V:P2} conf={C:P0}",
                symbol, tf, regime, smart.SmartType, smart.TrendSlopePercent, smart.VolatilityPercent, smart.Confidence);

            // Adaptive threshold
            int thr = GetAdaptiveThreshold(smart);
            decimal thrFrac = thr / 100m;
            decimal conf = smart.Confidence;

            bool fastOverride = FastTrendOverride(smart);

            if (!fastOverride && conf < thrFrac - 0.10m)
            {
                _logger.LogDebug(
                    "[DEBUG][{S}][{TF}] AdaptiveRegime: conf={C:P0} < thr={T}% → SKIP",
                    symbol, tf, conf, thr);
                return null;
            }

            if (smart.IsDangerChopZone)
            {
                _logger.LogInformation(
                    "[DEBUG][{S}][{TF}] ChopZone → skip",
                    symbol, tf);
                return null;
            }


            // ============================================================================
            // 3) Exact Entry Logic
            // ============================================================================
            TradeSignal? signal = null;

            bool isRange =
                regime == MarketRegime.Range ||
                smart.SmartType == SmartRegimeType.SmartRange ||
                smart.SmartType == SmartRegimeType.SmartSqueeze;

            bool isStrongTrend =
                regime == MarketRegime.StrongUpTrend ||
                regime == MarketRegime.StrongDownTrend ||
                smart.SmartType == SmartRegimeType.SmartStrongTrend;


            if (isRange)
                signal = LiquidityGrab(symbol, tf, kl)
                      ?? PullbackEma21(symbol, tf, kl);

            else if (isStrongTrend)
                signal = PullbackEma21(symbol, tf, kl);

            else
                signal = PullbackEma21(symbol, tf, kl);


            // SOFT fallback
            if (signal == null && smart.Confidence >= 0.40m)
            {
                var soft = SoftTrend(symbol, tf, kl, smart);
                if (soft != null)
                {
                    _logger.LogInformation(
                        "[DEBUG][{S}][{TF}] SOFT entry: {Side} @ {Entry}",
                        symbol, tf, soft.Side, soft.EntryPrice);

                    signal = soft;
                }
            }

            if (signal == null)
            {
                _logger.LogInformation("[DEBUG][{S}][{TF}] GEN → No signal", symbol, tf);
                return null;
            }

            // ============================================================================
            // Pattern validation
            // ============================================================================
            var pattern = _patternEngineService.Analyze(symbol, tf, kl);

            if (pattern != null && pattern.Score >= 0.30m)
            {
                bool sameDir =
                    (pattern.Direction == 1 && signal.Side == SignalSide.Buy) ||
                    (pattern.Direction == -1 && signal.Side == SignalSide.Sell);

                if (!sameDir && pattern.Score >= 0.60m)
                {
                    _logger.LogInformation(
                        "[DEBUG][{S}][{TF}] Pattern block dir={D} score={Score:F2}",
                        symbol, tf, pattern.Direction, pattern.Score);
                    return null;
                }
            }

            // ============================================================================
            // Liquidity Filtering
            // ============================================================================
            signal = _liquidityClusterService.FilterAndAdjust(signal);
            if (signal == null) return null;

            // ============================================================================
            // AI Risk
            // ============================================================================
            var risk = _aiLearning.GetDynamicRiskWeight(symbol, regime);
            signal.Reason += $"|AIrisk={risk:F2}";

            // ============================================================================
            // FINAL ENTRY FIX: safe entry vs SL
            // ============================================================================
            if (signal.StopLoss == signal.EntryPrice)
            {
                signal.StopLoss = signal.Side == SignalSide.Buy
                    ? signal.EntryPrice - (signal.Atr ?? 0) * 0.4m
                    : signal.EntryPrice + (signal.Atr ?? 0) * 0.4m;
            }

            if (signal.TakeProfits == null || signal.TakeProfits.Count == 0)
            {
                var atr = signal.Atr ?? 0;
                if (atr > 0)
                {
                    var (_, tp1, tp2, tp3) = AtrConf(tf);
                    signal.TakeProfits = new()
                    {
                        signal.EntryPrice + (signal.Side == SignalSide.Buy ? atr * tp1 : -atr * tp1),
                        signal.EntryPrice + (signal.Side == SignalSide.Buy ? atr * tp2 : -atr * tp2),
                        signal.EntryPrice + (signal.Side == SignalSide.Buy ? atr * tp3 : -atr * tp3)
                    };
                }
            }

            _logger.LogInformation(
                "[DEBUG][{S}][{TF}] FINAL SIGNAL → side={Side} entry={Entry:F4} sl={SL:F4} reason={Reason}",
                symbol, tf, signal.Side, signal.EntryPrice, signal.StopLoss, signal.Reason);

            return signal;
        }


        // ============================================================================
        // Adaptive threshold logic
        // ============================================================================
        private static int GetAdaptiveThreshold(SmartRegimeInfo s)
        {
            bool range =
                s.BaseRegime == MarketRegime.Range ||
                s.SmartType == SmartRegimeType.SmartRange ||
                s.SmartType == SmartRegimeType.SmartSqueeze;

            bool strong =
                s.BaseRegime == MarketRegime.StrongUpTrend ||
                s.BaseRegime == MarketRegime.StrongDownTrend ||
                s.SmartType == SmartRegimeType.SmartStrongTrend;

            int thr = strong ? 60 : range ? 35 : 45;

            if (s.VolatilityPercent < 0.10m) thr -= 10;
            else if (s.VolatilityPercent > 0.30m) thr += 10;

            if (Math.Abs(s.TrendSlopePercent) > 0.007m) thr += 5;

            if (thr < 25) thr = 25;
            if (thr > 80) thr = 80;

            return thr;
        }

        // ============================================================================
        // Fast Trend Override
        // ============================================================================
        private static bool FastTrendOverride(SmartRegimeInfo s)
        {
            bool strong =
                s.BaseRegime == MarketRegime.StrongUpTrend ||
                s.BaseRegime == MarketRegime.StrongDownTrend;

            bool sstrong = s.SmartType == SmartRegimeType.SmartStrongTrend;

            bool slopeOk = Math.Abs(s.TrendSlopePercent) >= 0.018m;
            bool volOk = s.VolatilityPercent <= 0.010m;
            bool confOk = s.Confidence >= 0.35m;

            return strong && sstrong && slopeOk && volOk && confOk;
        }
    }
}
