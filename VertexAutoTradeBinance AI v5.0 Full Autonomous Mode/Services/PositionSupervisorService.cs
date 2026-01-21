using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using CryptoExchange.Net.Objects;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services.Diagnostics;
using VertexAutoTradeBinance8.Strategy;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// PositionSupervisorService v8.2 PRO (Production)
    ///
    /// v8.2 FIXES (раз и навсегда):
    /// 0) EARLY TP (Partial close) 35% на +0.9 ATR → чтобы прибыль фиксировалась ДО откатов
    /// 1) SL -> BE (безубыток + буфер) на +1.2 ATR → чтобы после ранней прибыли не ловить минус
    /// 2) Анти-спам: partial/BE выполняются один раз на "позицию" (entry+qty+side)
    /// 3) UpdateSL: без reduceOnly (и без зависания на -1106), WorkingType.Mark используем осторожно
    /// 4) Если Binance вернёт -4120 → ставим/обновляем через RAW /fapi/v1/algoOrder (CONDITIONAL)
    /// 5) NEW 15-15-12-2025 -КОНЦЕПЦИЯ: PROTECT → PROBE → CONFIRM → SCALE PROBE — умный тест обратного движения(ключ)

    /// </summary>
    public class PositionSupervisorService
    {
        private readonly ILogger<PositionSupervisorService> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly SymbolInfoService _symbolInfo;
        private readonly AiStopLossOptimizer _slOptimizer;
        private readonly AiSelfLearningService _aiLearning;
        private readonly MarketDataService _marketData;
        private readonly AiMarketRegimeService _regime;
        private readonly ManualPositionHandler _manualHandler;
        private readonly BinanceAlgoOrderRaw _algoRaw;
        private readonly LiquidityGuardService _liquidityGuard;
        private readonly IOrderDispatcher _dispatcher;
        private MarketRegime _regimeNow;
        private readonly TradeResultMonitorService _tradeResultMonitor;


        // === Anti-spam guards for EarlyTP / BE-move ===
        private readonly ConcurrentDictionary<string, long> _earlyTpDone = new();   // key -> unixMs
        private readonly ConcurrentDictionary<string, long> _beMoved = new();      // key -> unixMs
         
        // BE staircase stages: (triggerATR, bufferATR)
        private static readonly (decimal TriggerAtr, decimal BufferAtr)[] BeStages = new[]
        {
            (1.20m, 0.15m),
            (1.80m, 0.25m),
            (2.40m, 0.35m),
        };

        // stage per position key
        private readonly ConcurrentDictionary<string, int> _beStage = new(StringComparer.OrdinalIgnoreCase);

        // earlyTP in-flight guard (prevents double enqueue burst)
        private readonly ConcurrentDictionary<string, long> _earlyTpInFlight = new(StringComparer.OrdinalIgnoreCase);

        // BE in-flight guard (prevents double enqueue burst)
        private readonly ConcurrentDictionary<string, long> _beInFlight = new(StringComparer.OrdinalIgnoreCase);


        private readonly ConcurrentDictionary<string, decimal> _restoredEntries = new();
        // === Harvest block after partial close ===
        private readonly ConcurrentDictionary<string, long> _recentPartialClose = new();
        private readonly EngineStateSnapshotService _stateSvc;
        private readonly SmartRegimeService _smartRegime;
        private readonly ReverseProbeEngine _reverseProbe;
        // === Attach idempotency (existing position attach) ===
        private readonly ConcurrentDictionary<string, bool> _attached = new();
        // === Funding risk guard ===
        private readonly ConcurrentDictionary<string, decimal> _fundingCost = new();
        // somewhere in class (field). If you already have it - DO NOT duplicate.
        private readonly ConcurrentDictionary<string, long> _fundingLastIncomeTs = new();
        // symbol -> cumulative funding (signed)
        private readonly ConcurrentDictionary<string, long> _fundingLastSync = new();   // symbol -> unixMs
        // === HEDGE COOLDOWN (GLOBAL) ===
        private static readonly ConcurrentDictionary<string, DateTime> _hedgeCooldown = new();
        private static readonly TimeSpan HedgeCooldownPeriod = TimeSpan.FromMinutes(10);
        // === Position fingerprint for anti-spam (stable qty) ===
        private readonly ConcurrentDictionary<string, decimal> _posBaseQty = new();   // key: symbol|side|entry -> baseQty
        private readonly ConcurrentDictionary<string, decimal> _posBaseEntry = new(); // key: symbol|side -> entry (latest stable)
        private readonly OpenPositionSymbolTracker _openPos;
        private readonly MarketDataFacade _marketDataFacade;
        // ===== Supervisor checks/min =====
        private int _supervisorChecks = 0;
        private DateTime _supervisorWindowUtc = DateTime.UtcNow;

        private readonly ConcurrentDictionary<string, long> _uiGuard = new();
        private readonly HedgeKillSettings _hedgeCfg;

        private readonly SignalConfidenceSettings _confidenceCfg;
        private bool UiSpamGuard(string symbol, PositionSide side, string action, int ms = 2500)
        {
            var key = $"{symbol}|{side}|{action}";
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (_uiGuard.TryGetValue(key, out var prev) && (now - prev) < ms)
                return false;

            _uiGuard[key] = now;
            return true;
        }

        private readonly DateTime _supervisorStartedUtc = DateTime.UtcNow;

        private bool IsColdStart =>
            (DateTime.UtcNow - _supervisorStartedUtc).TotalSeconds < 10;


        public PositionSupervisorService(
            ILogger<PositionSupervisorService> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            AiStopLossOptimizer slOptimizer,
            AiSelfLearningService aiLearning,
            MarketDataService marketData,
            AiMarketRegimeService regime,
            ManualPositionHandler manualHandler,
            IConfiguration cfg,
            IHttpClientFactory httpFactory,
            LiquidityGuardService liquidityGuard,
            IOrderDispatcher dispatcher, EngineStateSnapshotService stateSvc,
            SmartRegimeService smartRegime,
            ReverseProbeEngine reverseProbe, OpenPositionSymbolTracker openPos,
            IOptions<HedgeKillSettings> hedgeCfg, SignalConfidenceSettings confidenceCfg, MarketDataFacade marketDataFacade, 
            TradeResultMonitorService tradeResultMonitor)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
            _slOptimizer = slOptimizer;
            _aiLearning = aiLearning;
            _marketData = marketData;
            _regime = regime;
            _manualHandler = manualHandler;
            _regimeNow = MarketRegime.Range;
            _algoRaw = new BinanceAlgoOrderRaw(cfg, httpFactory, _logger);
            _liquidityGuard = liquidityGuard;
            _dispatcher = dispatcher;
            _stateSvc = stateSvc;
            _smartRegime = smartRegime;
            _reverseProbe = reverseProbe;
            _openPos = openPos;
            _hedgeCfg = hedgeCfg.Value;
            _confidenceCfg = confidenceCfg;
            _marketDataFacade = marketDataFacade;
            _tradeResultMonitor = tradeResultMonitor;
        }
        private bool IsHedgeOnCooldown(string symbol)
        {
            if (!_hedgeCfg.UseCooldown)
                return false;

            var now = DateTime.UtcNow;

            if (_hedgeCooldown.TryGetValue(symbol, out var until))
            {
                if (now < until)
                    return true;

                // cleanup expired cooldown
                _hedgeCooldown.TryRemove(symbol, out _);
            }

            return false;
        }

        private static string BuildPosEntryKey(string symbol, PositionSide side, decimal entry)
        {
            // Normalize entry to 8 decimals to avoid floating drift
            var e = Math.Round(entry, 8, MidpointRounding.ToZero);
            return $"{symbol}|{side}|E={e.ToString("0.########", CultureInfo.InvariantCulture)}";
        }

        private decimal GetOrSetBaseQty(string symbol, PositionSide side, decimal entry, decimal qtyAbs)
        {
            if (qtyAbs <= 0m)
                return 0m;

            var key = BuildPosEntryKey(symbol, side, entry);

            return _posBaseQty.AddOrUpdate(
                key,
                qtyAbs,
                (_, prev) =>
                {
                    // allow increase only, never shrink
                    if (qtyAbs > prev)
                        return qtyAbs;

                    return prev;
                });
        }

        private void ClearBaseQty(string symbol, PositionSide side)
        {
            var prefix = $"{symbol}|{side}|";

            foreach (var kv in _posBaseQty)
            {
                if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    _posBaseQty.TryRemove(kv.Key, out _);
            }

            _posBaseEntry.TryRemove($"{symbol}|{side}", out _);

            _logger.LogDebug(
                "[BASE-QTY][{symbol}][{side}] base quantities cleared",
                symbol, side);
        }

        private void MarkHedgeCooldown(string symbol)
        {
            if (!_hedgeCfg.UseCooldown)
                return;

            var mins = _hedgeCfg.CooldownMinutes;
            if (mins <= 0)
                mins = 10;

            var until = DateTime.UtcNow.AddMinutes(mins);

            _hedgeCooldown.AddOrUpdate(
                symbol,
                until,
                (_, prev) => prev > until ? prev : until
            );

            _logger.LogInformation(
                "[HEDGE][{symbol}] cooldown set until {until:HH:mm:ss} (+{mins}m)",
                symbol, until, mins);
        }

        private EngineState _engineState => _stateSvc.State;

        private static decimal NormalizeEntry(decimal entry)
        {
            // Binance Futures canonical precision
            return Math.Round(entry, 8, MidpointRounding.ToZero);
        }

        private static bool IsEntryClose(decimal a, decimal b)
        {
            // micro-drift guard (~0.0001%)
            return Math.Abs(a - b) <= a * 0.000001m;
        }

        // =====================================================================
        // MAIN ENTRY
        // =====================================================================
        public async Task SuperviseAsync(string symbol, TradeSignal? lastSignal, CancellationToken ct)
        {
            using var client = _factory.CreateRestClient();

            // ==========================================================
            // 0) SUPERVISOR HEARTBEAT (ПЕРВЫМ ДЕЛОМ)
            // ==========================================================
            var nowUtc = DateTime.UtcNow;

            _engineState.LastSupervisorAction = nowUtc;

            _supervisorChecks++;
            if ((nowUtc - _supervisorWindowUtc).TotalSeconds >= 60)
            {
                _engineState.SupervisorChecksLastMinute = _supervisorChecks;
                _supervisorChecks = 0;
                _supervisorWindowUtc = nowUtc;
            }

            // ==========================================================
            // 1) MANUAL → inject virtual signal (если lastSignal == null)
            // ==========================================================
            TradeSignal? effectiveLastSignal = lastSignal;
            if (effectiveLastSignal == null)
            {
                try
                {
                    var manualSignal = await _manualHandler.DetectManualAsync(client, symbol, ct);
                    if (manualSignal != null)
                    {
                        effectiveLastSignal = manualSignal;
                        _logger.LogWarning("[MANUAL][{symbol}] Virtual signal injected", symbol);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[MANUAL][{symbol}] DetectManual failed", symbol);
                }
            }

            // ==========================================================
            // 2) POSITIONS (retry-safe)
            // ==========================================================
            var posInfo = await GetPositionsWithRetryAsync(client, symbol, ct);
            if (!posInfo.Success || posInfo.Data == null)
            {
                _logger.LogWarning("[SUPERVISOR][{symbol}] Positions unavailable", symbol);
                return;
            }

            var positions = posInfo.Data
                .Where(p => p.Symbol == symbol && p.Quantity != 0m)
                .ToList();

            var longPos = positions.FirstOrDefault(p => p.PositionSide == PositionSide.Long);
            var shortPos = positions.FirstOrDefault(p => p.PositionSide == PositionSide.Short);

            var hasLong = longPos != null && longPos.Quantity != 0m;
            var hasShort = shortPos != null && shortPos.Quantity != 0m;

            var sKey = EngineState.Key(symbol);
            var st = _engineState.Symbols.GetOrAdd(sKey, _ => new SymbolState());

            st.HasOpenPosition = hasLong || hasShort;

            // ==========================================================
            // 2.1) NO POSITIONS → debounce + confirm → funding reset + tracker sync
            // ==========================================================
            if (!hasLong && !hasShort)
            {
                // debounce (Binance sync lag)
                try { await Task.Delay(800, ct); } catch { /* ignore */ }

                try
                {
                    var confirm = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                    if (confirm.Success && confirm.Data != null &&
                        !confirm.Data.Any(p => p.Symbol == symbol && p.Quantity != 0m))
                    {
                        _fundingCost.TryRemove(symbol, out _);
                        _fundingLastIncomeTs.TryRemove(symbol, out _);

                        _openPos.MarkClosed(symbol);

                        st.HasOpenPosition = false;

                        _engineState.LastSupervisorMessage =
                            $"Supervisor: no positions (funding reset) [{symbol}]";

                        _logger.LogInformation("[SUPERVISOR][{symbol}] No positions confirmed → funding reset", symbol);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[SUPERVISOR][{symbol}] Confirm no-positions failed", symbol);
                }

                return;
            }

            // ==========================================================
            // 2.2) tracker sync (idempotent)
            // ==========================================================
            _openPos.MarkOpen(symbol);

            _engineState.LastSupervisorMessage =
                $"Supervisor: active position [{symbol}]";

            // ==========================================================
            // 3) FUNDING COST REFRESH (real, not fiction)
            // ==========================================================
            try
            {
                await RefreshFundingCostAsync(client, symbol, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FUNDING][{symbol}] RefreshFundingCost failed", symbol);
            }

            // ==========================================================
            // 4) OPEN ORDERS
            // ==========================================================
            List<BinanceUsdFuturesOrder> openOrders;
            try
            {
                openOrders = await LoadOrdersAsync(client, symbol, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SUPERVISOR][{symbol}] LoadOrders failed", symbol);
                openOrders = new List<BinanceUsdFuturesOrder>();
            }
                       
            // ==========================================================
            // 5) KLINES 1m — EXECUTION CONTEXT ONLY (NO DECISION)
            // ==========================================================
            IReadOnlyList<BinanceFuturesUsdtKline>? klines1m = null;
            decimal atr14_1m = 0m;

            try
            {
                klines1m = await _marketDataFacade.GetKlinesAsync(
                    symbol,
                    KlineInterval.OneMinute,
                    160,
                    ct);

                if (klines1m != null && klines1m.Count >= 50)
                {
                    // ⚠️ ВАЖНО:
                    // 1m ATR используется ТОЛЬКО для execution-фильтров
                    // (late entry / exhaustion / impulse done)
                    atr14_1m = _marketData.CalculateAtr(klines1m, 14);
                }

                // ❌ НЕ ДЕЛАЕМ:
                // - DetectRegime(1m)
                // - SmartRegime.Evaluate(1m)
                // - НЕ ТРОГАЕМ _regimeNow
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[SUPERVISOR][{symbol}] 1m execution context failed",
                    symbol);
            }
            // ==========================================================
            // DECISION TF (15m) — SOURCE OF TRUTH
            // ==========================================================
            IReadOnlyList<BinanceFuturesUsdtKline>? klines15m = null;
            SmartRegimeInfo? smart15m = null;

            klines15m = await _marketDataFacade.GetKlinesAsync(
                symbol,
                KlineInterval.FifteenMinutes,
                200,
                ct);

            if (klines15m != null && klines15m.Count >= 80)
            {
                var rr15 = _regime.DetectRegime(
                    symbol,
                    KlineInterval.FifteenMinutes,
                    klines15m);

                if (rr15 != null)
                    _regimeNow = rr15.Regime;

                smart15m = _smartRegime.Evaluate(
                    symbol,
                    KlineInterval.FifteenMinutes,
                    klines15m);
            }


            // ==========================================================
            // 6) CONFIRM OR KILL HEDGE (v8.2 PRO)
            // ==========================================================
            if (hasLong && hasShort && klines1m != null && klines1m.Count >= 30)
            {
                try
                {
                    await ConfirmOrKillHedgeAsync(client, symbol, longPos!, shortPos!, klines1m, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[HEDGE][{symbol}] ConfirmOrKillHedge failed", symbol);
                }
            }

            // ==========================================================
            // 7) REVERSE PROBE (ONE SHOT per supervise tick)
            // ==========================================================
            if (smart15m != null && atr14_1m > 0 && klines1m != null)
            {
                try
                {
                    await TryReverseProbeAsync(client, symbol, longPos, shortPos, smart15m, atr14_1m, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[PROBE][{symbol}] TryReverseProbe failed", symbol);
                }
            }

            // ==========================================================
            // 8) BUILD PER-SIDE SIGNALS (NO shared instance mutation)
            // ==========================================================
            TradeSignal? longSignal = null;
            TradeSignal? shortSignal = null;

            // base: clone lastSignal into per-side (если он есть)
            if (effectiveLastSignal != null)
            {
                // если у тебя уже есть Clone/Copy метод — используй его.
                // тут делаем безопасный shallow-copy с явным Side.
                longSignal = CopyForSide(effectiveLastSignal, SignalSide.Buy);
                shortSignal = CopyForSide(effectiveLastSignal, SignalSide.Sell);
            }

            // если всё ещё нет сигнала — строим виртуальный от позиции (production fallback)
            if (hasLong && longSignal == null && longPos != null)
            {
                longSignal = BuildVirtualFromPosition(symbol, SignalSide.Buy, longPos, atr14_1m);
                _logger.LogWarning("[SUPERVISOR][{symbol}] lastSignal missing → virtual LONG signal", symbol);
            }

            if (hasShort && shortSignal == null && shortPos != null)
            {
                shortSignal = BuildVirtualFromPosition(symbol, SignalSide.Sell, shortPos, atr14_1m);
                _logger.LogWarning("[SUPERVISOR][{symbol}] lastSignal missing → virtual SHORT signal", symbol);
            }

            // ==========================================================
            // 9) HANDLE SIDES
            // ==========================================================
            if (hasLong && longPos != null)
            {
                await HandleSideAsync(client, symbol, PositionSide.Long, longPos, openOrders, longSignal, klines1m, ct);
            }

            if (hasShort && shortPos != null)
            {
                await HandleSideAsync(client, symbol, PositionSide.Short, shortPos, openOrders, shortSignal, klines1m, ct);
            }
        }

        // =====================================================================
        // helper: copy signal safely (no shared mutation between sides)
        // =====================================================================
        private static TradeSignal CopyForSide(TradeSignal src, SignalSide side)
        {
            var copy = new TradeSignal
            {
                Symbol = src.Symbol,
                Side = side,

                EntryPrice = src.EntryPrice,
                StopLoss = src.StopLoss,
                TakeProfit = src.TakeProfit,
                Atr = src.Atr,

                Timeframe = src.Timeframe,
                Time = src.Time,

                Reason = src.Reason,
                IsSuperSignal = src.IsSuperSignal,
                IsManual = src.IsManual,

                SafetyRiskMultiplier = src.SafetyRiskMultiplier,
                HighTfSafetyMode = src.HighTfSafetyMode,
                AiQuality = src.AiQuality,
                Confidence = src.Confidence,
                Leverage = src.Leverage,
            };

            if (src.TakeProfits != null && src.TakeProfits.Count > 0)
                copy.TakeProfits.AddRange(src.TakeProfits);



            return copy;
        }

        private async Task<List<BinanceUsdFuturesOrder>> LoadOrdersAsync(
        BinanceRestClient client,
        string symbol,
        CancellationToken ct)
        {
            var res = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct);

            if (!res.Success || res.Data == null)
                return new List<BinanceUsdFuturesOrder>();

            return res.Data
                .Where(o => o.Quantity > 0m) // hard filter: ignore zero-qty garbage
                .OrderBy(o => GetOrderSortTime(o))
                .ToList();
        }

        private static long GetOrderSortTime(BinanceUsdFuturesOrder o)
        {
            var t = o.UpdateTime != default
                ? o.UpdateTime
                : o.CreateTime;

            return new DateTimeOffset(t).ToUnixTimeMilliseconds();
        }

        private static TradeSignal BuildVirtualFromPosition(
        string symbol,
        SignalSide side,
        BinancePositionDetailsUsdt pos,
        decimal atr1m)
        {
            if (pos == null)
                throw new ArgumentNullException(nameof(pos));

            // =================================================
            // ENTRY RESOLUTION (STRICT, NO FANTASY)
            // =================================================
            // NOTE:
            // pos.EntryPrice is allowed here ONLY as initial seed.
            // All protection logic MUST use anchor (GetOrSetBaseEntry) in Supervisor.
            decimal entry = pos.EntryPrice;


            if (entry <= 0m && pos.MarkPrice > 0m)
                entry = pos.MarkPrice;

            if (entry <= 0m)
                throw new InvalidOperationException(
                    $"[VIRTUAL-SIGNAL][{symbol}] Cannot resolve entry price");

            // =================================================
            // ATR RESOLUTION (GUARD-SAFE)
            // =================================================
            bool atrSynthetic = false;

            if (atr1m <= 0m)
            {
                // synthetic ATR:
                // - minimal
                // - non-zero
                // - does NOT affect logic materially
                atr1m = entry * 0.0005m; // 0.05%
                atrSynthetic = true;
            }

            // absolute floor (safety for divisions)
            if (atr1m <= 0m)
                atr1m = 0.00000001m;

            // =================================================
            // PLACEHOLDER SL / TP
            // (Supervisor WILL recompute / restore)
            // =================================================
            decimal sl;
            decimal tp1;

            if (side == SignalSide.Buy)
            {
                sl = entry - atr1m * 1.2m;
                tp1 = entry + atr1m * 2.0m;
            }
            else
            {
                sl = entry + atr1m * 1.2m;
                tp1 = entry - atr1m * 2.0m;
            }

            // =================================================
            // BUILD SIGNAL (CANONICAL VIRTUAL)
            // =================================================
            var sig = new TradeSignal
            {
                Symbol = symbol,
                Side = side,

                EntryPrice = entry,
                Atr = atr1m,

                // placeholders only
                StopLoss = sl,
                TakeProfit = tp1,

                Timeframe = "OneMinute",
                Time = DateTime.UtcNow,

                // explicit semantic
                Reason = atrSynthetic
                    ? "VIRTUAL_FROM_POSITION|ATR_SYNTH"
                    : "VIRTUAL_FROM_POSITION",

                IsManual = true,
                IsSuperSignal = false,

                // conservative defaults
                SafetyRiskMultiplier = 1m,
                HighTfSafetyMode = true,

                // AI / confidence intentionally LOW
                AiQuality = 0.20m,
                Confidence = 0.20m,

                // informational only
                Leverage = pos.Leverage > 0 ? pos.Leverage : null
            };

            // Multi-TP compatibility
            sig.TakeProfits.Add(tp1);

            return sig;
        }

        //========================================================new for HIIBRID 
        private decimal CalcAtrPct(decimal atr, decimal price)
        {
            // Hard guards — unknown volatility
            if (atr <= 0m || price <= 0m)
                return 0m;

            var pct = atr / price;

            // Safety clamp (protects confidence / hedge logic)
            if (pct < 0m) return 0m;
            if (pct > 0.50m) return 0.50m; // 50% ATR is already chaos

            return pct;
        }

        private decimal CalcKillConfidence(
      string symbol,
      SmartRegimeInfo? smart,
      decimal atrPct,
      bool fundingPressure,
      bool liquidityDanger)
        {
            // ------------------------------------------------------------------
            // BASE: neutral decision state
            // ------------------------------------------------------------------
            decimal c = 0.50m;

            // ------------------------------------------------------------------
            // TREND / STRUCTURE (adds confidence)
            // ------------------------------------------------------------------
            if (smart != null)
            {
                var slopeAbs = Math.Abs(smart.TrendSlopePercent);

                // Strong regime = clearer winner
                if (smart.BaseRegime is MarketRegime.StrongUpTrend or MarketRegime.StrongDownTrend)
                    c += 0.15m;

                if (slopeAbs >= _hedgeCfg.SlopeWeak)
                    c += 0.08m;

                if (slopeAbs >= _hedgeCfg.SlopeStrong)
                    c += 0.07m;
            }

            // ------------------------------------------------------------------
            // COST PRESSURE (forces resolution, but not blindly)
            // ------------------------------------------------------------------
            if (fundingPressure)
                c += 0.15m;

            // ------------------------------------------------------------------
            // RISK SUPPRESSORS (cap confidence, not just subtract)
            // ------------------------------------------------------------------

            // Liquidity danger → market unreliable
            if (liquidityDanger)
                c = Math.Min(c, 0.60m);

            // Extreme volatility → whipsaw risk
            if (atrPct >= _hedgeCfg.AtrPctExtreme)
                c = Math.Min(c, 0.65m);

            // ------------------------------------------------------------------
            // HARD CLAMP (absolute bounds)
            // ------------------------------------------------------------------
            if (c < 0.05m) c = 0.05m;
            if (c > 0.95m) c = 0.95m;

            return c;
        }

        private decimal CalcGivebackBudget(decimal bucket, decimal confidence)
        {
            // 1) Map confidence → bucket share
            decimal share =
                confidence >= 0.80m ? _hedgeCfg.GivebackBucketHigh :
                confidence >= 0.65m ? _hedgeCfg.GivebackBucketMid :
                                      _hedgeCfg.GivebackBucketLow;

            // 2) NO BUCKET → NO GIVEBACK
            // hedge cannot consume profit that does not exist
            if (bucket <= 0m)
                return 0m;

            // 3) Base budget from realized profit
            decimal budget = bucket * share;

            // 4) Absolute clamps (only meaningful when bucket > 0)
            if (budget < _hedgeCfg.GivebackMinUsd)
                budget = _hedgeCfg.GivebackMinUsd;

            if (budget > _hedgeCfg.GivebackMaxUsd)
                budget = _hedgeCfg.GivebackMaxUsd;

            return budget;
        }

        //==================================================

        private async Task ConfirmOrKillHedgeAsync(
       BinanceRestClient client,
       string symbol,
       BinancePositionDetailsUsdt longPos,
       BinancePositionDetailsUsdt shortPos,
       IReadOnlyList<BinanceFuturesUsdtKline> klines,
       CancellationToken ct)
        {
            // =========================
            // HARD BLOCKS (as-is)
            // =========================

            // ⛔ NO HEDGE IN SQUEEZE
            if (_regimeNow == MarketRegime.Squeeze)
            {
                _logger.LogWarning("[HEDGE][{symbol}] SKIP hedge decision → SQUEEZE regime", symbol);

                _aiLearning.RecordMarketStateTriggered(
                    reason: "HEDGE_BLOCK_SQUEEZE",
                    symbol: symbol,
                    timeframe: "HEDGE",
                    regime: MarketRegime.Squeeze,
                    slope: 0m,
                    volatility: 0m,
                    atr: 0m,
                    confidence: 0.90m);

                return;
            }

            // ⛔ NO-HEDGE DURING LIQUIDITY DANGER
            if (_liquidityGuard.LastDanger?.Block == true)
            {
                _logger.LogWarning(
                    "[HEDGE][{symbol}] SKIP hedge decision → liquidity danger {reason}",
                    symbol, _liquidityGuard.LastDanger?.Reason);
                return;
            }

            // ⛔ HEDGE COOLDOWN
            if (IsHedgeOnCooldown(symbol))
            {
                _logger.LogDebug("[HEDGE][{symbol}] skipped — cooldown active", symbol);
                return;
            }

            // =========================
            // BASIC SAFETY
            // =========================
            if (longPos == null || shortPos == null)
                return;

            if (longPos.Quantity == 0m || shortPos.Quantity == 0m)
                return;

            if (klines == null || klines.Count < 20)
                return;

            ct.ThrowIfCancellationRequested();

            // Use LAST CLOSED candle (avoid live candle noise)
            var lastClosed = klines.Count >= 2 ? klines[^2] : klines[^1];
            var lastClosePx = lastClosed.ClosePrice;
            if (lastClosePx <= 0m)
                return;

            // =========================
            // PnL & legs
            // =========================
            decimal longPnl = longPos.UnrealizedPnl;
            decimal shortPnl = shortPos.UnrealizedPnl;

            var loser = longPnl < shortPnl ? PositionSide.Long : PositionSide.Short;
            var winner = loser == PositionSide.Long ? PositionSide.Short : PositionSide.Long;

            var loserPos = loser == PositionSide.Long ? longPos : shortPos;
            var winnerPos = winner == PositionSide.Long ? longPos : shortPos;

            // Entry sanity (critical for confirmation + protection)
            var loserEntryRef = loserPos.EntryPrice;
            var winnerEntryRef = winnerPos.EntryPrice;

            if (loserEntryRef <= 0m || winnerEntryRef <= 0m)
            {
                _logger.LogWarning(
                    "[HEDGE][{symbol}] SKIP → entry unresolved loserEntry={le} winnerEntry={we}",
                    symbol, loserEntryRef, winnerEntryRef);
                return;
            }

            // =========================
            // Small pnl-diff guard (keep logic, but fix notional)
            // =========================
            if (Math.Abs(longPnl - shortPnl) < 3m)
            {
                var longNotional =
                    (longPos.MarkPrice > 0m && longPos.Quantity != 0m) ? Math.Abs(longPos.Quantity) * longPos.MarkPrice : 0m;

                var shortNotional =
                    (shortPos.MarkPrice > 0m && shortPos.Quantity != 0m) ? Math.Abs(shortPos.Quantity) * shortPos.MarkPrice : 0m;

                var symbolNotionalCheck = longNotional + shortNotional;

                if (symbolNotionalCheck <= 0m || !IsFundingRiskExceeded(symbol, symbolNotionalCheck))
                    return;
            }

            // =========================
            // ATR
            // =========================
            var atr = _marketData.CalculateAtr(klines, 14);
            if (atr <= 0m)
                return;

            // =========================
            // Winner confirmation (closed candle)
            // =========================
            bool winnerConfirmed =
                winner == PositionSide.Long
                    ? lastClosePx > winnerEntryRef + atr * 0.4m
                    : lastClosePx < winnerEntryRef - atr * 0.4m;

            if (!winnerConfirmed)
                return;

            // =========================
            // SMART HEDGE-KILL GATES (PRO)
            // =========================
            decimal netPnl = longPnl + shortPnl;

            var sKey = EngineState.Key(symbol);
            var st = _engineState.Symbols.GetOrAdd(sKey, _ => new SymbolState());
            decimal bucket = st.RealizedPnlBucketUsd;

            // FUNDING GUARD (NOTIONAL-BASED, HEDGE)
            decimal symbolNotional = 0m;
            if (longPos.Quantity != 0m && longPos.MarkPrice > 0m)
                symbolNotional += Math.Abs(longPos.Quantity) * longPos.MarkPrice;
            if (shortPos.Quantity != 0m && shortPos.MarkPrice > 0m)
                symbolNotional += Math.Abs(shortPos.Quantity) * shortPos.MarkPrice;

            bool fundingPressureByNotional = symbolNotional > 0m && IsFundingRiskExceeded(symbol, symbolNotional);

            if (fundingPressureByNotional)
            {
                _logger.LogWarning(
                    "[HEDGE][{symbol}] Funding pressure detected | notional={notional:F2} cost={cost:F4}",
                    symbol,
                    symbolNotional,
                    _fundingCost.TryGetValue(symbol, out var c) ? c : 0m);
            }

            bool liquidityDanger = false;
            try
            {
                if (_liquidityGuard.LastDanger?.Block == true)
                    liquidityDanger = true;
                else if (_liquidityGuard.IsDangerRecent(TimeSpan.FromMinutes(5)))
                {
                    var ld = _liquidityGuard.LastDanger;
                    if (ld != null)
                    {
                        var age = DateTime.UtcNow - ld.UtcTime;
                        if (age < TimeSpan.FromMinutes(2))
                            liquidityDanger = true;
                    }
                }
            }
            catch { /* ignore */ }

            SmartRegimeInfo? smart = null;
            try
            {
                if (klines.Count >= 50)
                    smart = _smartRegime.Evaluate(symbol, KlineInterval.FiveMinutes, klines);
            }
            catch { /* ignore */ }

            var atrPct = CalcAtrPct(atr, lastClosePx);
            var confidence = CalcKillConfidence(symbol, smart, atrPct, fundingPressureByNotional, liquidityDanger);
            var givebackBudget = CalcGivebackBudget(bucket, confidence);

            decimal loserPnl = loser == PositionSide.Long ? longPnl : shortPnl;
            decimal loserLossAbs = Math.Abs(Math.Min(0m, loserPnl));

            decimal loserQtyAbsForAtr = Math.Abs(loserPos.Quantity);
            decimal atrNotional = atr * loserQtyAbsForAtr;
            decimal hardLoserByAtr = atrNotional * _hedgeCfg.HardLoserAtrMult;

            bool hardLoss =
                loserLossAbs >= Math.Max(_hedgeCfg.HardLoserUsd, hardLoserByAtr) ||
                netPnl <= _hedgeCfg.HardNetUsd;

            // Gate A: netPnL ok -> skip unless funding/hardLoss
            if (!hardLoss && !fundingPressureByNotional && netPnl >= _hedgeCfg.NetOkUsd)
            {
                _logger.LogInformation(
                    "[HEDGE-KILL][{symbol}] SKIP → netPnL ok net={net:F2} loser={loser} loss={loss:F2} bucket={bucket:F2} conf={conf:F2}",
                    symbol, netPnl, loser, loserLossAbs, bucket, confidence);
                return;
            }

            // Gate B: giveback limiter -> skip unless funding/hardLoss
            if (!hardLoss && !fundingPressureByNotional && loserLossAbs > givebackBudget)
            {
                _logger.LogWarning(
                    "[HEDGE-KILL][{symbol}] SKIP → giveback limit loss={loss:F2} budget={budget:F2} net={net:F2} bucket={bucket:F2} conf={conf:F2}",
                    symbol, loserLossAbs, givebackBudget, netPnl, bucket, confidence);
                return;
            }

            // =========================
            // DECISION: kill loser
            // =========================
            _logger.LogWarning(
                "[HEDGE-KILL][{symbol}] CLOSE LOSER {loser} pnl={pnl:F2} | KEEP {winner}",
                symbol, loser, loserPnl, winner);

            _aiLearning.RecordMarketStateTriggered(
                reason: "HEDGE_KILL",
                symbol: symbol,
                timeframe: "HEDGE",
                regime: MarketRegime.Unknown,
                slope: 0m,
                volatility: 0m,
                atr: atr,
                confidence: Math.Min(0.95m, Math.Abs(longPnl - shortPnl) / 10m));

            // =========================
            // CLOSE LOSER (MARKET) with proper qty quantize (MARKET RULES!)
            // =========================
            var closeSide = loser == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            // IMPORTANT: use MARKET qty rules here
            var fMkt = await _symbolInfo.GetFuturesFiltersAsync(symbol, SymbolInfoService.QtyRule.Market);
            var step = fMkt.step > 0m ? fMkt.step : 1m;
            var minQty = fMkt.minQty;

            decimal loserQtyAbs = Math.Abs(loserPos.Quantity);
            if (loserQtyAbs <= 0m)
                return;

            decimal frac =
                _hedgeCfg.Mode == HedgeKillMode.Aggressive ? 1.00m :
                _hedgeCfg.Mode == HedgeKillMode.Safe ? 0.80m :
                _hedgeCfg.LoserCloseFraction;

            decimal closeQty = loserQtyAbs * frac;

            if (fundingPressureByNotional || hardLoss)
                closeQty = loserQtyAbs;

            // Quantize DOWN to step
            closeQty = Math.Floor(closeQty / step) * step;

            // If partial close becomes dust -> full close (still quantized)
            if (closeQty < minQty)
            {
                closeQty = Math.Floor(loserQtyAbs / step) * step;
                if (closeQty < minQty)
                {
                    _logger.LogWarning(
                        "[HEDGE-KILL][{symbol}] CloseQty below minQty even for full close | qty={qty} minQty={min}",
                        symbol, closeQty, minQty);
                    return;
                }
            }

            var closeRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: closeSide,
                type: FuturesOrderType.Market,
                quantity: closeQty,
                positionSide: loser,
                ct: ct);

            if (!closeRes.Success)
            {
                _logger.LogWarning(
                    "[HEDGE-KILL][{symbol}] Close loser failed: {code} {msg}",
                    symbol, closeRes.Error?.Code, closeRes.Error?.Message);
                return;
            }

            await Task.Delay(450, ct);

            // =========================
            // REFRESH winner position snapshot (critical)
            // =========================
            BinancePositionDetailsUsdt? winnerNow = null;
            try
            {
                var info = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                if (info.Success && info.Data != null)
                {
                    winnerNow = info.Data.FirstOrDefault(p =>
                        p.Symbol == symbol &&
                        p.PositionSide == winner &&
                        p.Quantity != 0m);
                }
            }
            catch { /* ignore */ }

            var winnerQtyAbs = winnerNow != null ? Math.Abs(winnerNow.Quantity) : Math.Abs(winnerPos.Quantity);
            var winnerEntry = winnerNow != null && winnerNow.EntryPrice > 0m ? winnerNow.EntryPrice : winnerEntryRef;

            if (winnerQtyAbs <= 0m || winnerEntry <= 0m)
                return;

            // =========================
            // PROTECT WINNER → SL to BE+buffer (proper tick)
            // =========================
            var allFilters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            var tick = allFilters.tickSize > 0m ? allFilters.tickSize : 0.0001m;

            decimal buffer = atr * 0.25m;

            decimal newSlRaw =
                winner == PositionSide.Long
                    ? winnerEntry + buffer
                    : winnerEntry - buffer;

            // Quantize SL to tick in correct direction (never worse than entry)
            decimal newSl =
                winner == PositionSide.Long
                    ? Math.Floor(newSlRaw / tick) * tick
                    : Math.Ceiling(newSlRaw / tick) * tick;

            if (winner == PositionSide.Long && newSl < winnerEntry)
                newSl = winnerEntry;
            if (winner == PositionSide.Short && newSl > winnerEntry)
                newSl = winnerEntry;

            var ordersRes = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct);
            if (!ordersRes.Success || ordersRes.Data == null)
                return;

            var winnerCloseSide = winner == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var slOrder = ordersRes.Data.FirstOrDefault(o =>
                o.Type == FuturesOrderType.StopMarket &&
                o.PositionSide == winner &&
                o.Side == winnerCloseSide);

            if (slOrder != null)
            {
                var ok = await UpdateSL_ProAsync(
                    client,
                    symbol,
                    winner,
                    winnerQtyAbs,
                    slOrder,
                    winnerEntry,
                    newSl,
                    signal: null,
                    ct);

                if (!ok)
                    return;
            }
            else
            {
                _logger.LogWarning(
                    "[HEDGE-KILL][{symbol}] SL missing on winner → CREATE EMERGENCY SL",
                    symbol);

                await CreateEmergencySLAsync(
                    client,
                    symbol,
                    winner,
                    winnerQtyAbs,
                    winnerEntry,
                    signal: null,
                    ct);
            }

            // keep your existing MarkProtection signature (no redesign)
            MarkProtection(symbol);

            _logger.LogWarning(
                "[HEDGE-KILL][{symbol}] WINNER {side} protected → SL enforced (newSL={sl})",
                symbol, winner, newSl);

            MarkHedgeCooldown(symbol);
        }


        private bool IsFundingRiskExceeded(
            string symbol,
            decimal positionNotionalUsd)
        {
            // Нет позиции → нет funding-risk
            if (positionNotionalUsd <= 0m)
                return false;

            if (!_fundingCost.TryGetValue(symbol, out var cost))
                return false;

            var equity = _engineState.EquityUsd;
            if (equity <= 0m)
            {
                _logger.LogWarning(
                    "[FUNDING-GUARD][{symbol}] Equity unavailable → skip funding check",
                    symbol);
                return false;
            }

            const decimal MAX_EQUITY_PCT = 0.03m;    // 3% equity
            const decimal MAX_NOTIONAL_PCT = 0.0025m;  // 25 bps of position

            var absCost = Math.Abs(cost);

            var equityPct = absCost / equity;
            var notionalPct = absCost / positionNotionalUsd;

            var blocked =
                equityPct >= MAX_EQUITY_PCT ||
                notionalPct >= MAX_NOTIONAL_PCT;

            if (blocked)
            {
                _logger.LogWarning(
                    "[FUNDING-GUARD][{symbol}] BLOCK | cost={cost:F4} eqPct={eq:P3} notionalPct={notional:P3}",
                    symbol,
                    cost,
                    equityPct,
                    notionalPct);
            }

            return blocked;
        }


        private bool CanIncreasePosition(string symbol, decimal positionNotionalUsd)
        {
            return !IsFundingRiskExceeded(symbol, positionNotionalUsd);
        }
        private async Task RefreshFundingCostAsync(BinanceRestClient client, string symbol, CancellationToken ct)
        {
            try
            {
                // throttle: не чаще 60 сек на symbol
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (_fundingLastSync.TryGetValue(symbol, out var last) && (now - last) < 60_000)
                    return;

                _fundingLastSync[symbol] = now;

                // Pull delta from income history (Funding Fee)
                var delta = await GetFundingIncomeDeltaAsync(client, symbol, ct);

                if (delta != 0m)
                {
                    _fundingCost.AddOrUpdate(symbol, delta, (_, prev) => prev + delta);

                    _logger.LogInformation(
                        "[FUNDING][{symbol}] +delta={delta:F6} cum={cum:F6}",
                        symbol, delta, _fundingCost[symbol]);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[FUNDING][{symbol}] refresh failed", symbol);
            }
        }

        private async Task<decimal> GetFundingIncomeDeltaAsync(
            BinanceRestClient client,
            string symbol,
            CancellationToken ct)
        {
            // Берём funding за последние 2 дня (больше не нужно)
            var startTime = DateTime.UtcNow.AddDays(-2);

            // В ТВОЕЙ версии Binance.Net incomeType = string, startTime = DateTime
            // Поэтому передаём Binance API code:
            const string fundingType = "FUNDING_FEE";

            var res = await client.UsdFuturesApi.Account.GetIncomeHistoryAsync(
            symbol: symbol,
            incomeType: fundingType,
            startTime: DateTime.UtcNow.AddDays(-2),
            limit: 100,
            ct: ct);

            if (!res.Success || res.Data == null)
                return 0m;

            // anti-dup: суммируем только новые элементы после lastTs
            long lastTs = _fundingLastIncomeTs.TryGetValue(symbol, out var v) ? v : 0L;

            decimal delta = 0m;
            long newLastTs = lastTs;

            foreach (var item in res.Data)
            {
                // item может быть разного типа в разных версиях → берём безопасно через dynamic
                dynamic x = item;

                long ts = 0;
                decimal income = 0m;

                // 1) timestamp
                try
                {
                    // часто: x.Time (long ms) или x.Timestamp (DateTime/DateTimeOffset) или x.Date
                    try { ts = (long)x.Time; } catch { }

                    if (ts == 0)
                    {
                        try
                        {
                            DateTime dt = (DateTime)x.Timestamp;
                            ts = new DateTimeOffset(dt).ToUnixTimeMilliseconds();
                        }
                        catch { }
                    }

                    if (ts == 0)
                    {
                        try
                        {
                            DateTimeOffset dto = (DateTimeOffset)x.Timestamp;
                            ts = dto.ToUnixTimeMilliseconds();
                        }
                        catch { }
                    }

                    if (ts == 0)
                    {
                        try
                        {
                            DateTime dt = (DateTime)x.Date;
                            ts = new DateTimeOffset(dt).ToUnixTimeMilliseconds();
                        }
                        catch { }
                    }

                    if (ts == 0)
                    {
                        // крайний fallback: если нет времени — пропускаем, чтобы не наделать дублей
                        continue;
                    }

                    // 2) income
                    try { income = (decimal)x.Income; } catch { }
                    if (income == 0m)
                    {
                        try { income = (decimal)x.Amount; } catch { }
                    }
                }
                catch
                {
                    continue;
                }

                // только НОВЫЕ записи
                if (ts <= lastTs)
                    continue;

                delta += income;
                if (ts > newLastTs) newLastTs = ts;
            }

            if (newLastTs > lastTs)
                _fundingLastIncomeTs[symbol] = newLastTs;

            return delta;
        }

        // =====================================================================
        // RETRY POSITIONS
        // =====================================================================
        private async Task<WebCallResult<BinancePositionDetailsUsdt[]>> GetPositionsWithRetryAsync(
        BinanceRestClient client,
        string symbol,
        CancellationToken ct)
        {
            const int maxAttempts = 5;
            WebCallResult<BinancePositionDetailsUsdt[]> last = null!;

            for (int i = 0; i < maxAttempts; i++)
            {
                ct.ThrowIfCancellationRequested();

                // 🔥 БЕЗ symbol-фильтра — Binance bug-safe
                var res = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                last = res;

                if (res.Success && res.Data != null)
                {
                    // ⏳ ждём, пока позиция реально появится
                    if (res.Data.Any(p => p.Symbol == symbol && p.Quantity != 0m))
                        return res;
                }

                await Task.Delay(300, ct);
            }

            return last;
        }

        public async Task AttachExistingPositionAsync(
         string symbol,
         PositionSide side,
         decimal qty,
         decimal entryPrice,
         CancellationToken ct)
        {
            qty = Math.Abs(qty);

            if (qty <= 0 || entryPrice <= 0) return;

            var key = $"{symbol}:{side}:{entryPrice:F8}:{qty:F8}";

            if (_attached.TryAdd(key, true) == false)
            {
                _logger.LogInformation("[SUPERVISOR][ATTACH] already attached {key}", key);
                return;
            }

            // ✅ N4: track open position symbol
            _openPos.MarkOpen(symbol);

            _logger.LogWarning(
                "[SUPERVISOR][ATTACH] attaching existing position {symbol} {side} qty={qty} entry={entry}",
                symbol, side, qty, entryPrice);

            await EnsureEmergencyProtectionAsync(symbol, side, qty, entryPrice, ct);
        }
        private async Task EnsureEmergencyProtectionAsync(
        string symbol,
        PositionSide side,
        decimal qty,
        decimal entryPrice,
        CancellationToken ct)
        {
            if (qty <= 0 || entryPrice <= 0)
                return;

            // Важно: qty у позиции в Binance может быть со знаком
            qty = Math.Abs(qty);

            using var client = _factory.CreateRestClient();

            // 1) Проверяем существующие ордера
            var openOrders = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(
                symbol: symbol,
                ct: ct);

            if (!openOrders.Success || openOrders.Data == null)
            {
                _logger.LogWarning(
                    "[SUPERVISOR][EMERGENCY] cannot fetch open orders {symbol}: {err}",
                    symbol, openOrders.Error?.Message);
                return;
            }

            bool hasSL = openOrders.Data.Any(o =>
                o.Type == FuturesOrderType.StopMarket &&
                o.PositionSide == side);

            if (hasSL)
            {
                _logger.LogInformation(
                    "[SUPERVISOR][EMERGENCY] SL already exists {symbol} {side}",
                    symbol, side);
                return;
            }

            // 2) Emergency SL без klines (bootstrap-safe)
            // Консервативно: 1.2% от entry (под твой v8.2 смысл "быстро защитить")
            const decimal emergencyPct = 0.012m;

            decimal slPrice = side == PositionSide.Long
                ? entryPrice * (1m - emergencyPct)
                : entryPrice * (1m + emergencyPct);

            _logger.LogWarning(
                "[SUPERVISOR][EMERGENCY] placing SL {symbol} {side} qty={qty} sl={sl}",
                symbol, side, qty, slPrice);

            // 3) Пробуем обычный endpoint (без reduceOnly параметра -> меньше риска -1106)
            var orderSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var sl = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: orderSide,
                type: FuturesOrderType.StopMarket,
                quantity: qty,
                stopPrice: slPrice,
                positionSide: side,
                workingType: WorkingType.Mark,
                reduceOnly: null,
                ct: ct);

            if (sl.Success)
            {
                _logger.LogInformation(
                    "[SUPERVISOR][EMERGENCY] SL placed OK (NORMAL) {symbol} {side} sl={sl}",
                    symbol, side, slPrice);
                return;
            }

            // 4) Fallback: ALGO RAW CONDITIONAL (если -4120)
            if (IsAlgoRequired(sl.Error))
            {
                _logger.LogWarning(
                    "[SUPERVISOR][EMERGENCY] requires ALGO (-4120) -> RAW /fapi/v1/algoOrder {symbol} {side}",
                    symbol, side);

                // workingType: "MARK_PRICE" чтобы совпасть с WorkingType.Mark
                var (safeQty, safeTrig) = await NormalizeForAlgoAsync(symbol, qty, slPrice, ct);

                var ok = await _algoRaw.PlaceConditionalAsync(
                    symbol: symbol,
                    side: orderSide,
                    positionSide: side,
                    type: "STOP_MARKET",
                    quantity: safeQty,
                    triggerPrice: safeTrig,
                    workingType: "MARK_PRICE",
                    reduceOnly: null,
                    ct: ct);


                if (ok)
                {
                    _logger.LogWarning(
                        "[SUPERVISOR][EMERGENCY] SL placed OK (ALGO-RAW) {symbol} {side} sl={sl}",
                        symbol, side, slPrice);
                    return;
                }

                _logger.LogCritical(
                    "[SUPERVISOR][EMERGENCY] ALGO-RAW FAILED {symbol} {side}",
                    symbol, side);
                return;
            }

            _logger.LogError(
                "[SUPERVISOR][EMERGENCY] SL FAILED (NORMAL) {symbol}: {err}",
                symbol, sl.Error?.Message);
        }

        private async Task TryReverseProbeAsync(
      BinanceRestClient client,
      string symbol,
      BinancePositionDetailsUsdt? longPos,
      BinancePositionDetailsUsdt? shortPos,
      SmartRegimeInfo smart,
      decimal atr,
      CancellationToken ct)
        {
            // ==========================================================
            // 0) PROTECT STAGE — must exist (EarlyTP / BE already done)
            // ==========================================================
            var sKey = EngineState.Key(symbol);
            if (!_engineState.Symbols.TryGetValue(sKey, out var st))
                return;

            bool protectedRecently = st.LastProtectionUtc > DateTime.UtcNow.AddMinutes(-15);
            if (!protectedRecently)
                return;

            // ==========================================================
            // 1) POSITION STATE — exactly ONE side must exist
            // ==========================================================
            bool hasLong = longPos != null && longPos.Quantity != 0m;
            bool hasShort = shortPos != null && shortPos.Quantity != 0m;

            if (hasLong && hasShort)
                return;

            // ==========================================================
            // 2) REFERENCE PRICE (from existing leg only)
            // ==========================================================
            decimal refPrice =
                hasLong ? longPos!.MarkPrice :
                hasShort ? shortPos!.MarkPrice : 0m;

            if (refPrice <= 0m)
                return;

            // ==========================================================
            // 3) VOLATILITY GATE — extreme ATR relative block
            // ==========================================================
            try
            {
                if (atr > 0m)
                {
                    var atrPct = atr / refPrice; // 0.01 = 1%
                    if (atrPct >= 0.018m)
                    {
                        _logger.LogWarning(
                            "[PROBE][{symbol}] BLOCK → extreme ATR vol atrPct={atrPct:P2}",
                            symbol, atrPct);
                        return;
                    }
                }
            }
            catch { }

            // ==========================================================
            // 4) BASE SIDE (existing position)
            // ==========================================================
            PositionSide baseSide =
                hasLong ? PositionSide.Long :
                hasShort ? PositionSide.Short :
                PositionSide.Both;

            if (baseSide == PositionSide.Both)
                return;

            // ==========================================================
            // 5) LIQUIDITY GUARD — recent danger veto
            // ==========================================================
            if (_liquidityGuard.IsDangerRecent(TimeSpan.FromMinutes(5)))
            {
                if (DateTime.UtcNow - _liquidityGuard.LastDanger!.UtcTime < TimeSpan.FromMinutes(2))
                {
                    _logger.LogInformation(
                        "[PROBE][{symbol}] SKIP → recent liquidity danger {reason}",
                        symbol, _liquidityGuard.LastDanger?.Reason);
                    return;
                }
            }

            if (_liquidityGuard.LastDanger?.Reason == LiquidityGuardReason.LowVolume)
                return;

            // ==========================================================
            // 6) FLIP CONDITION — strict regime confirmation
            // ==========================================================
            bool flipToShort =
                baseSide == PositionSide.Long &&
                smart.BaseRegime == MarketRegime.StrongDownTrend &&
                smart.TrendSlopePercent < -0.01m;

            bool flipToLong =
                baseSide == PositionSide.Short &&
                smart.BaseRegime == MarketRegime.StrongUpTrend &&
                smart.TrendSlopePercent > 0.01m;

            if (!flipToShort && !flipToLong)
                return;

            var probeSide = flipToShort ? PositionSide.Short : PositionSide.Long;

            // ==========================================================
            // 7) ANTI-SPAM — one probe per symbol window
            // ==========================================================
            if (!_reverseProbe.CanProbeNow(symbol))
                return;

            // ==========================================================
            // 8) BASE QTY (existing exposure)
            // ==========================================================
            decimal baseQtyAbs =
                Math.Abs(baseSide == PositionSide.Long
                    ? longPos!.Quantity
                    : shortPos!.Quantity);

            if (baseQtyAbs <= 0m)
                return;

            // ==========================================================
            // 9) BASE NOTIONAL (USD exposure of existing leg)
            // ==========================================================
            decimal baseNotional = baseQtyAbs * refPrice;

            // ==========================================================
            // 🔒 FUNDING RISK GATE — FAIL FAST (ONCE, BEFORE SIZING)
            // ==========================================================
            if (IsFundingRiskExceeded(symbol, baseNotional))
            {
                _logger.LogWarning(
                    "[REVERSE-PROBE][{symbol}] Funding pressure (NOTIONAL) → probe blocked | notional={notional:F2}",
                    symbol, baseNotional);
                return;
            }

            // ==========================================================
            // 10) PROBE SIZING — % of base notional
            // ==========================================================
            decimal probeNotionalTarget = baseNotional * 0.07m;

            // ==========================================================
            // 11) EXCHANGE FILTERS
            // ==========================================================
            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            var step = filters.step > 0 ? filters.step : 1m;

            // ==========================================================
            // 12) PROBE QTY — floor only (never round up)
            // ==========================================================
            decimal probeQty = probeNotionalTarget / refPrice;
            probeQty = Math.Floor(probeQty / step) * step;

            if (probeQty < filters.minQty)
                return;

            // ==========================================================
            // 13) PORTFOLIO / SYMBOL EXPOSURE CAP
            // ==========================================================
            decimal equity = _engineState.EquityUsd;
            if (equity <= 0m)
                return;

            decimal existingSymbolNotional = 0m;
            if (hasLong) existingSymbolNotional += Math.Abs(longPos!.Quantity) * longPos.MarkPrice;
            if (hasShort) existingSymbolNotional += Math.Abs(shortPos!.Quantity) * shortPos.MarkPrice;

            decimal probeNotional = probeQty * refPrice;
            const decimal maxSymbolPct = 0.55m;

            if ((existingSymbolNotional + probeNotional) / equity > maxSymbolPct)
            {
                _logger.LogWarning(
                    "[PORTFOLIO-CAP][{symbol}] symbol exposure limit hit → probe blocked",
                    symbol);
                return;
            }

            // ==========================================================
            // 14) PLACE MARKET ENTRY (HEDGE-AWARE)
            // ==========================================================
            var orderSide = probeSide == PositionSide.Long ? OrderSide.Buy : OrderSide.Sell;

            _logger.LogWarning(
                "[PROBE][{symbol}] START micro-hedge: base={baseSide} -> probe={probeSide} qty={qty} smart={reg} slope={slope:P2}",
                symbol, baseSide, probeSide, probeQty, smart.BaseRegime, smart.TrendSlopePercent);

            _dispatcher.Enqueue(async token =>
            {
                using var c = _factory.CreateRestClient();

                var entryRes = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: orderSide,
                    type: FuturesOrderType.Market,
                    quantity: probeQty,
                    positionSide: probeSide,
                    ct: token);

                if (!entryRes.Success)
                {
                    _logger.LogWarning("[PROBE][{symbol}] Entry failed: {err}", symbol, entryRes.Error);
                    return;
                }

                await Task.Delay(350, token);

                var posInfo = await c.UsdFuturesApi.Account.GetPositionInformationAsync(ct: token);
                if (!posInfo.Success || posInfo.Data == null)
                    return;

                var p = posInfo.Data.FirstOrDefault(x =>
                    x.Symbol == symbol &&
                    x.PositionSide == probeSide &&
                    Math.Abs(x.Quantity) > 0m);

                if (p == null || p.EntryPrice <= 0m)
                {
                    _logger.LogWarning("[PROBE][{symbol}] Entry resolve failed after open", symbol);
                    return;
                }

                var entry = p.EntryPrice;

                await CreateEmergencySLAsync(c, symbol, probeSide, probeQty, entry, signal: null, token);
                await CreateEmergencyTPAsync(c, symbol, probeSide, probeQty, entry, signal: null, token);

                _logger.LogWarning(
                    "[PROBE][{symbol}] OPENED {side} qty={qty} entry={entry} -> SL/TP restored",
                    symbol, probeSide, probeQty, entry);
            });

            _reverseProbe.MarkProbe(symbol);
        }


        private static decimal GetNotionalFromPosition(BinancePositionDetailsUsdt pos)
        {
            if (pos == null) return 0m;

            var qty = Math.Abs(pos.Quantity);
            var price = pos.MarkPrice;

            if (qty <= 0m || price <= 0m)
                return 0m;

            return qty * price;
        }



        private async Task<List<BinanceUsdFuturesOrder>> LoadOrdersSafeAsync(
         BinanceRestClient client,
         string symbol,
         CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var res = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(
                symbol: symbol,
                ct: ct);

            if (!res.Success || res.Data == null)
                return new();

            // HARD FILTER: ignore zero-qty garbage
            return res.Data
        // Binance иногда возвращает "мусорные" open-orders с qty=0
        .Where(o => o.Quantity > 0)
        // UpdateTime может быть DateTime.MinValue → проверяем ЯВНО
        .OrderBy(o => o.UpdateTime > o.CreateTime ? o.UpdateTime : o.CreateTime)
        .ToList();
        }

        private decimal GetOrSetBaseEntry(
    string symbol,
    PositionSide side,
    decimal resolvedEntry)
        {
            if (resolvedEntry <= 0m)
                return 0m;

            resolvedEntry = NormalizeEntry(resolvedEntry);

            var key = $"{symbol}|{side}";

            return _posBaseEntry.AddOrUpdate(
                key,
                resolvedEntry,
                (_, prev) =>
                {
                    if (prev <= 0m)
                        return resolvedEntry;

                    // ignore micro drift
                    if (IsEntryClose(prev, resolvedEntry))
                        return prev;

                    // allow improvement ONLY in position direction (scale-in)
                    return side == PositionSide.Long
                        ? Math.Min(prev, resolvedEntry)
                        : Math.Max(prev, resolvedEntry);
                });
        }

        // =====================================================================
        // HANDLE SIDE  (v8.2 PRO) — PROP DESK FIXED (Harvest + Restore Order)
        // =====================================================================
        private async Task HandleSideAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            BinancePositionDetailsUsdt pos,
            List<BinanceUsdFuturesOrder> allOrders,
            TradeSignal? signal,
            IReadOnlyList<BinanceFuturesUsdtKline>? klines,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // ============================
            // 0) SNAPSHOT (authoritative)
            // ============================
            var qtyAbs = Math.Abs(pos.Quantity);

            // ============================
            // 1) CLOSE DETECTOR (ROBUST)
            // ============================
            var stateKey = $"{symbol}_{side}";
            var prevQty = _manualHandler.GetPrevQty(stateKey);
            var prevEntry = _manualHandler.GetPrevEntry(stateKey);

            // update snapshot immediately (next tick consistency)
            _manualHandler.SetPrevState(stateKey, pos.Quantity, pos.EntryPrice);

            // CLOSED = had qty → now zero
            if (Math.Abs(prevQty) > 0m && qtyAbs <= 0m)
            {
                await HandlePositionClosedAsync(client, symbol, side, pos, prevEntry, ct);

                var exitPrice =
                    pos.MarkPrice > 0m
                        ? pos.MarkPrice
                        : prevEntry;
                decimal realizedPnlUsd = _engineState.RealizedPnlUsd;
               

                if (realizedPnlUsd == 0m && prevEntry > 0m)
                {
                    var dir = side == PositionSide.Long ? 1m : -1m;
                    realizedPnlUsd = dir * (exitPrice - prevEntry) * Math.Abs(prevQty);
                }

                await _tradeResultMonitor.CheckClosedPositionAsync(
                    symbol: symbol,
                    signal: signal!,
                    realizedPnlUsd: realizedPnlUsd,
                    exitPrice: exitPrice,
                    exitRegime: _regimeNow,
                    ct: ct);

                return;
            }

            // ============================
            // 2) NO POSITION → EXIT
            // ============================
            if (qtyAbs <= 0m)
            {
                _logger.LogDebug("[SUPERVISOR][{symbol}][{side}] no open qty", symbol, side);
                return;
            }

            // ============================
            // 3) FUNDING GUARD (ADD/SCALE ONLY)
            // ============================
            decimal positionNotional =
                pos.MarkPrice > 0m
                    ? qtyAbs * pos.MarkPrice
                    : 0m;

            bool fundingBlocked =
                positionNotional > 0m &&
                IsFundingRiskExceeded(symbol, positionNotional);

            if (fundingBlocked)
            {
                _logger.LogWarning(
                    "[FUNDING-GUARD][{symbol}] funding pressure → block ADD / SCALE only",
                    symbol);
            }

            // ============================
            // 4) ENTRY RESOLVE (CANONICAL)
            // ============================
            var entry = await ResolveEntrySafeAsync(client, symbol, side, pos, prevEntry, ct);
            if (entry <= 0m)
            {
                _logger.LogError(
                    "[SUPERVISOR][{symbol}][{side}] entry unresolved → skip cycle",
                    symbol, side);
                return;
            }

            // anchor base entry (stable across partials)
            GetOrSetBaseEntry(symbol, side, entry);

            // ============================
            // 5) ATR RESOLVE (NON-BLOCKING)
            // ============================
            var atr14 = ResolveAtr14(signal, klines);
            // atr14 may be 0 → early/BE/harvest will self-skip

            // ============================
            // 6) ORDERS SNAPSHOT (HEDGE-SAFE)
            // ============================
            var orders = allOrders
                .Where(o => o.PositionSide == side)
                .ToList();

            var closeSide =
                side == PositionSide.Long
                    ? OrderSide.Sell
                    : OrderSide.Buy;

            var slOrders = orders
                .Where(o => o.Side == closeSide && o.Type == FuturesOrderType.StopMarket)
                .ToList();

            var tpOrders = orders
                .Where(o => o.Side == closeSide && o.Type == FuturesOrderType.TakeProfitMarket)
                .ToList();

            bool hasMultipleSL = slOrders.Count > 1;
            bool hasMultipleTP = tpOrders.Count > 1;

            if (hasMultipleSL)
                _logger.LogWarning(
                    "[SUPERVISOR][{symbol}][{side}] multiple SL detected → skip create",
                    symbol, side);

            if (hasMultipleTP)
                _logger.LogWarning(
                    "[SUPERVISOR][{symbol}][{side}] multiple TP detected → skip create",
                    symbol, side);

            // IMPORTANT: do NOT trust presence this tick
            var sl = slOrders.FirstOrDefault();
            var tp = tpOrders.FirstOrDefault();

            // ============================
            // 7) STABLE BASE QTY (GUARDS)
            // ============================
            var baseQtyForGuards =
                GetOrSetBaseQty(symbol, side, entry, qtyAbs);

            // ============================
            // 8) PROTECT FIRST (IDEMPOTENT)
            //    SL / TP restoration MUST NOT gate logic
            // ============================

            // SL intent
            if (sl == null && !hasMultipleSL)
            {
                await CreateEmergencySLAsync(
                    client,
                    symbol,
                    side,
                    qtyAbs,
                    entry,
                    signal,
                    ct);

                _logger.LogWarning(
                    "[SUPERVISOR][{symbol}][{side}] SL ensure requested",
                    symbol, side);
            }

            // TP intent
            if (tp == null && !hasMultipleTP)
            {
                await CreateEmergencyTPAsync(
                    client,
                    symbol,
                    side,
                    qtyAbs,
                    entry,
                    signal,
                    ct);

                _logger.LogWarning(
                    "[SUPERVISOR][{symbol}][{side}] TP ensure requested",
                    symbol, side);
            }


            // IMPORTANT: if we just created SL/TP, we do NOT assume order object exists in this tick.
            // Next supervisor tick will pick it up in allOrders.

            // ============================
            // 9) EARLY TP + BE (PROP SAFE)
            // ============================
            if (klines != null && klines.Count >= 20 && atr14 > 0m)
            {
                // EARLY TP — independent, async-safe
                TryEarlyPartialTake(
                    client,
                    symbol,
                    side,
                    baseQtyForGuards,
                    entry,
                    atr14,
                    signal,
                    klines,
                    ct);

                // BE logic must NOT depend on "sl object visibility"
                // Use protection intent, not order snapshot
                await TryMoveSlToBeAsync(
     client,
     symbol,
     side,
     baseQtyForGuards,
     entry,
     atr14,
     sl,      // may be null: TryMoveSlToBeAsync will resolve active SL from orders
     signal,
     klines,
     ct);
            }


            // ============================
            // 10) Profit Harvest (after protect/early/BE) — PROP 2026
            // - No hard 50-klines gate (use 20+)
            // - Edge is bounded, but more price-behaviour aware (silent trend)
            // - minUsd adapts to notional + volatility proxy with floor
            // ============================
            if (klines != null && klines.Count >= 20)
            {
                // drift proxy (helps "silent trend" & 2026 behaviour)
                var lastPx = pos.MarkPrice > 0m ? pos.MarkPrice : entry;
                var driftPct = entry > 0m ? Math.Abs(lastPx - entry) / entry : 0m;

                // quick ATR proxy (if you already have atr14 earlier in HandleSideAsync, pass it in instead)
                var atr = _marketData.CalculateAtr(klines, 14);
                if (atr <= 0m) atr = _marketData.CalculateAtr(klines); // fallback
                if (atr <= 0m) atr = Math.Max(entry * 0.0005m, 0.00000001m); // final fallback

                // atr% helps normalize "what is meaningful" move
                var atrPct = (lastPx > 0m) ? (atr / lastPx) : 0m;

                // --- aiEdgeScore: realistic, bounded, behaviour-aware ---
                decimal edge =
                    _regimeNow is MarketRegime.StrongUpTrend or MarketRegime.StrongDownTrend ? 0.64m :
                    _regimeNow is MarketRegime.UpTrend or MarketRegime.DownTrend ? 0.60m :
                    _regimeNow is MarketRegime.Range ? 0.56m :
                                                                                               0.58m;

                // add small signal contributions if present (bounded)
                if (signal != null)
                {
                    if (signal.Confidence.HasValue)
                        edge += Math.Clamp(signal.Confidence.Value - 0.52m, -0.05m, 0.10m);

                    // if AiQuality doesn't exist in your model — REMOVE this block safely
                    if (signal.AiQuality.HasValue)
                        edge += Math.Clamp(signal.AiQuality.Value - 0.52m, -0.04m, 0.08m);
                }

                // bonus for "silent trend move" (slow drift without obvious impulse)
                // widen band slightly; 2026 market often prints 0.2–1.6% controlled drift legs
                if (driftPct >= 0.0020m && driftPct <= 0.0180m)
                    edge += 0.04m;

                // small penalty in squeeze (avoid harvesting too early inside compression)
                if (_regimeNow == MarketRegime.Squeeze)
                    edge -= 0.03m;

                var aiEdgeScore = Math.Clamp(edge, 0.52m, 0.76m);

                // --- adaptive minUsd ---
                // Floor keeps it practical; notional-based keeps it proportional.
                // Also nudge minUsd up slightly when ATR% is high (more noise).
                var volNudge = atrPct >= 0.020m ? 2m : atrPct >= 0.012m ? 1m : 0m;

                var minUsd = Math.Clamp(
                    Math.Max(4m, positionNotional * 0.00020m) + volNudge, // 0.02% of notional
                    4m,
                    12m);
 

                await TryHarvestProfitAsync(
                 client,_engineState,symbol,side, pos, klines, aiEdgeScore,minUsd,ct);
                 
            }

            // ============================
            // 11) Trailing / Runner — ALWAYS ALLOWED (risk-reducing)
            // ============================

            // Funding guard MUST NOT block protection / trailing
            if (klines != null && klines.Count >= 20) // 20 достаточно
            {
                try
                {
                    await ManageRunnerTpAsync(
                        client,
                        symbol,
                        side,
                        qtyAbs,
                        entry,
                        orders,
                        signal,
                        klines,
                        ct);

                    await ManageRunnerTpExtensionAsync(
                        client,
                        symbol,
                        side,
                        qtyAbs,
                        entry,
                        signal,
                        orders,
                        klines,
                        ct);

                    await MultiLayerTrailingAsync(
                        client,
                        symbol,
                        side,
                        qtyAbs,
                        entry,
                        signal,
                        orders,
                        klines,
                        ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "[TRAILING][{symbol}][{side}] runner/trailing error",
                        symbol, side);
                }
            }

            //// ============================
            //// 11) Trailing / Runner — blocked only by funding guard
            //// ============================
            //if (!fundingBlocked && klines != null && klines.Count >= 50)
            //{
            //    await ManageRunnerTpAsync(client, symbol, side, qtyAbs, entry, orders, signal, klines, ct);
            //    await ManageRunnerTpExtensionAsync(client, symbol, side, qtyAbs, entry, signal, orders, klines, ct);
            //    await MultiLayerTrailingAsync(client, symbol, side, qtyAbs, entry, signal, orders, klines, ct);
            //}
        }
        bool realPnlIsNegative(string symbol, PositionSide side, decimal entry, decimal exit)
        {
            var pnl = side == PositionSide.Long
                ? exit - entry
                : entry - exit;

            // допускаем микрослип/комиссию
            return pnl < -Math.Abs(entry) * 0.0004m; // ~0.04%
        }

        private async Task HandlePositionClosedAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            BinancePositionDetailsUsdt pos,
            decimal prevEntry,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // Best-effort exit price (no extra REST here)
            var exitPrice =
                pos.MarkPrice > 0m ? pos.MarkPrice :
                (pos.EntryPrice > 0m ? pos.EntryPrice : prevEntry);

            var sigSide = side == PositionSide.Long ? SignalSide.Buy : SignalSide.Sell;

            // AI trade record only if we have a meaningful prevEntry
            if (prevEntry > 0m && exitPrice > 0m)
            {
                _aiLearning.RecordTrade(symbol, sigSide, entry: prevEntry, exit: exitPrice, regime: _regimeNow);

                _logger.LogWarning(
                    "[AI][{symbol}] POSITION CLOSED → saved to ai_learning.json | entry={entry} exit={exit}",
                    symbol, prevEntry, exitPrice);
            }

            // StopLoss detect → cooldown (simple, deterministic)
           
            var isStopLoss =
    prevEntry > 0m &&
    exitPrice > 0m &&
    realPnlIsNegative(symbol, side, prevEntry, exitPrice);

            if (isStopLoss)
            {
                _manualHandler.RegisterStop(symbol);
                StrategyEngine.RegisterStop(symbol, sigSide);

                _logger.LogWarning("[STOP][{symbol}] StopLoss detected → strategy cooldown registered", symbol);
            }

            // ============================
            // CLEANUP (all anti-spam/guards for symbol+side)
            // ============================
            ClearGuardsByPrefix(_earlyTpDone, $"{symbol}|{side}|");
            ClearGuardsByPrefix(_beMoved, $"{symbol}|{side}|");
            ClearBaseQty(symbol, side);

            _restoredEntries.TryRemove($"{symbol}_{side}", out _);

            // attached keys: "{symbol}:{side}..."
            ClearGuardsByPrefix(_attached, $"{symbol}:{side}");

            // ============================
            // PROFIT HARVEST CLEANUP (CRITICAL)
            // ============================
            ClearGuardsByPrefix(_recentPartialClose, $"{symbol}|{side}|");

            // cleanup harvest SymbolState(s) for this symbol+side
            var harvestPrefix = $"{EngineState.Key(symbol)}|{side}|";
            foreach (var k in _engineState.Symbols.Keys.Where(k => k.StartsWith(harvestPrefix)))
            {
                _engineState.Symbols.TryRemove(k, out _);
            }

            // Mark closed only if NO positions left on symbol (hedge-safe)
            try
            {
                var info = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                if (info.Success && info.Data != null)
                {
                    var anyLeft = info.Data.Any(p => p.Symbol == symbol && p.Quantity != 0m);
                    if (!anyLeft)
                        _openPos.MarkClosed(symbol);
                }
            }
            catch
            {
                // ignore: close tracking is best-effort
            }

            // engine-state flag (symbol-level)
            var sKey = EngineState.Key(symbol);
            if (_engineState.Symbols.TryGetValue(sKey, out var st))
                st.HasOpenPosition = false;
        }

        private async Task<decimal> ResolveEntrySafeAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            BinancePositionDetailsUsdt pos,
            decimal prevEntry,
            CancellationToken ct)
        {
            // 1) direct from position
            if (pos.EntryPrice > 0m)
                return pos.EntryPrice;

            // 2) cached base entry from previous cycles (fast path)
            // if (_posBaseEntry.TryGetValue($"{symbol}|{side}", out var cached) && cached > 0m)
            //    return cached;


            var cached = TryGetValidBaseEntry(symbol, side, prevEntry);
            if (cached > 0m)
                return cached;

            // 3) restoredEntries cache (per side)
            var key = $"{symbol}_{side}";
            if (_restoredEntries.TryGetValue(key, out var restoredCached) && restoredCached > 0m)
                return restoredCached;

            // 4) try exchange trades
            var restored = await ResolveEntryFromExchangeAsync(client, symbol, side, ct);
            if (restored.HasValue && restored.Value > 0m)
            {
                _restoredEntries[key] = restored.Value;

                _logger.LogWarning(
                    "[SUPERVISOR][{symbol}][{side}] Entry restored from exchange = {entry}",
                    symbol, side, restored.Value);

                return restored.Value;
            }

            // 5) fallback: previous entry if meaningful
            if (prevEntry > 0m)
                return prevEntry;

            return 0m;
        }
        private decimal TryGetValidBaseEntry(
    string symbol,
    PositionSide side,
    decimal posEntryPrice)
        {
            var key = $"{symbol}|{side}";

            if (!_posBaseEntry.TryGetValue(key, out var cached) || cached <= 0m)
                return 0m;

            var posEntry = NormalizeEntry(posEntryPrice);

            // If position entry is unknown — cached is still better than nothing
            if (posEntry <= 0m)
                return cached;

            // If drift is small — cached is valid
            if (IsEntryClose(cached, posEntry))
                return cached;

            // If position entry is WORSE than cached → cached is stale
            bool cachedInvalid =
                side == PositionSide.Long
                    ? cached > posEntry   // long entry should not move UP
                    : cached < posEntry;  // short entry should not move DOWN

            if (cachedInvalid)
                return 0m;

            return cached;
        }

        private decimal ResolveAtr14(TradeSignal? signal, IReadOnlyList<BinanceFuturesUsdtKline>? klines)
        {
            if (signal?.Atr != null && signal.Atr.Value > 0m)
                return signal.Atr.Value;

            if (klines != null && klines.Count >= 30)
                return _marketData.CalculateAtr(klines, 14);

            return 0m;
        }

        private static void ClearGuardsByPrefix<T>(
            ConcurrentDictionary<string, T> dict,
            string prefix)
        {
            // production-safe: avoids LINQ over Keys and repeated StartsWith allocations
            foreach (var kv in dict)
            {
                if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    dict.TryRemove(kv.Key, out _);
            }
        }

        public int GetOpenPositionsCount()
        {
            return _openPos.OpenSymbolsCount;
        }

        private async Task<decimal?> ResolveEntryFromExchangeAsync(
         BinanceRestClient client,
         string symbol,
         PositionSide side,
         CancellationToken ct)
        {
            var res = await client.UsdFuturesApi.Trading.GetUserTradesAsync(
                symbol: symbol,
                limit: 500,
                ct: ct);

            if (!res.Success || res.Data == null || !res.Data.Any())
                return null;

            var trades = res.Data
                .Where(t =>
                    t.Symbol == symbol &&
                    t.PositionSide == side &&
                    t.Quantity > 0m &&
                    t.Price > 0m)
                .OrderBy(t => t.Timestamp)
                .ToList();

            if (trades.Count == 0)
                return null;

            decimal posQty = 0m;
            decimal vwapQty = 0m;
            decimal vwapNotional = 0m;

            // ВАЖНО: логика направления, а не enum-сравнение
            for (int i = trades.Count - 1; i >= 0; i--)
            {
                var t = trades[i];

                bool increasesPosition =
                    side == PositionSide.Long
                        ? t.Side == OrderSide.Buy
                        : t.Side == OrderSide.Sell;

                if (increasesPosition)
                {
                    posQty += t.Quantity;
                    vwapQty += t.Quantity;
                    vwapNotional += t.Quantity * t.Price;
                }
                else
                {
                    posQty -= t.Quantity;
                }

                // дошли до момента, когда позиция была 0
                if (posQty <= 0m)
                    break;
            }

            if (vwapQty <= 0m)
                return null;

            return vwapNotional / vwapQty;
        }


        private void MarkProtection(string symbol)
        {
            var now = DateTime.UtcNow;

            var key = EngineState.Key(symbol);
            var st = _engineState.Symbols.GetOrAdd(key, _ => new SymbolState());

            // ⛔ anti-spam: ignore duplicate marks in same second
            if (st.LastProtectionUtc.HasValue &&
                (now - st.LastProtectionUtc.Value).TotalSeconds < 1)
            {
                return;
            }

            st.LastProtectionUtc = now;

            _logger.LogInformation(
                "[PROTECTION][{symbol}] protection marked @ {time}",
                symbol,
                now);
        }

        // =====================================================================
        // EARLY TP (Partial close) — ключевой фикс v8.2
        // =====================================================================
        // Force immediate or relaxed ROI-BE after Early-TP
        private readonly ConcurrentDictionary<string, long> _forceRoiBe = new();

        private void TryEarlyPartialTake(
       BinanceRestClient client,
       string symbol,
       PositionSide side,
       decimal qty,
       decimal entry,
       decimal atr,
       TradeSignal? signal,
       IReadOnlyList<BinanceFuturesUsdtKline> klines,
       CancellationToken ct)
        {
            // ==========================================================
            // BASIC GUARDS
            // ==========================================================
            if (atr <= 0m || qty <= 0m || entry <= 0m) return;
            if (string.IsNullOrWhiteSpace(symbol)) return;
            if (klines == null || klines.Count < 12) return;

            if (_liquidityGuard.LastDanger?.Block == true)
                return;

            // last CLOSED candle only
            var candle = klines[^2];
            var lastClose = candle.ClosePrice;

            // ==========================================================
            // CONFIDENCE PROFILE
            // ==========================================================
            decimal confidence = signal?.Confidence ?? _confidenceCfg.MinEntry;

            decimal earlyTpAtrMult = 0m;
            bool isHighConfidence = confidence >= _confidenceCfg.Bands.HighFrom;
            bool isMediumConfidence =
                confidence >= _confidenceCfg.MinEntry &&
                confidence < _confidenceCfg.Bands.HighFrom;

            if (isHighConfidence)
                earlyTpAtrMult = _confidenceCfg.EarlyTpAtr.High;
            else if (isMediumConfidence)
                earlyTpAtrMult = _confidenceCfg.EarlyTpAtr.Medium;
            else
                return; // LOW confidence → no early TP

            if (earlyTpAtrMult <= 0m)
                return;

            // ==========================================================
            // WICK / IMPULSE FILTER
            // ==========================================================
            var body = Math.Abs(candle.ClosePrice - candle.OpenPrice);
            var range = candle.HighPrice - candle.LowPrice;
            if (range <= 0m) return;

            var wickAgainst =
                side == PositionSide.Long
                    ? candle.HighPrice - candle.ClosePrice
                    : candle.ClosePrice - candle.LowPrice;

            if (wickAgainst > body * 1.8m && body / range < 0.45m)
                return;

            if (body / range < 0.35m)
                return;

            // ==========================================================
            // ATR HIT CHECK
            // ==========================================================
            decimal hit =
                side == PositionSide.Long
                    ? Math.Max(candle.ClosePrice, candle.HighPrice)
                    : Math.Min(candle.ClosePrice, candle.LowPrice);

            bool reached =
                side == PositionSide.Long
                    ? hit >= entry + atr * earlyTpAtrMult
                    : hit <= entry - atr * earlyTpAtrMult;

            if (!reached)
                return;

            // ==========================================================
            // STABLE POSITION GUARD
            // ==========================================================
            var baseQty = GetOrSetBaseQty(symbol, side, entry, qty);
            var guardKey = BuildPosGuardKey(symbol, side, entry, baseQty);

            if (!_earlyTpDone.TryAdd(guardKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                return;

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            // ==========================================================
            // EXECUTION (ASYNC)
            // ==========================================================
            _dispatcher.Enqueue(async token =>
            {
                try
                {
                    using var exec = _factory.CreateRestClient();

                    var posRes = await exec.UsdFuturesApi.Account.GetPositionInformationAsync(ct: token);
                    if (!posRes.Success || posRes.Data == null)
                    {
                        _earlyTpDone.TryRemove(guardKey, out _);
                        return;
                    }

                    var real = posRes.Data.FirstOrDefault(p =>
                        p.Symbol == symbol &&
                        p.PositionSide == side &&
                        Math.Abs(p.Quantity) > 0m);

                    if (real == null)
                    {
                        _earlyTpDone.TryRemove(guardKey, out _);
                        return;
                    }

                    var realQty = Math.Abs(real.Quantity);

                    var f = await _symbolInfo.GetFuturesFiltersAsync(
                        symbol,
                        SymbolInfoService.QtyRule.Market,
                        token);

                    var step = f.step;
                    if (step <= 0m)
                    {
                        _earlyTpDone.TryRemove(guardKey, out _);
                        return;
                    }

                    // fixed early TP size
                    var closeQty = Math.Min(baseQty * 0.35m, realQty);
                    closeQty = Math.Floor(closeQty / step) * step;

                    if (closeQty < f.minQty || closeQty <= 0m)
                    {
                        _earlyTpDone.TryRemove(guardKey, out _);
                        return;
                    }

                    var res = await exec.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: symbol,
                        side: closeSide,
                        type: FuturesOrderType.Market,
                        quantity: closeQty,
                        positionSide: side,
                        reduceOnly: null,
                        ct: token);

                    if (!res.Success)
                    {
                        _logger.LogWarning(
                            "[EARLY-TP][{symbol}][{side}] FAIL: {err}",
                            symbol, side, res.Error);

                        _earlyTpDone.TryRemove(guardKey, out _);
                        return;
                    }

                    // ======================================================
                    // POST-SUCCESS
                    // ======================================================
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    _recentPartialClose[$"{symbol}|{side}"] = now;
                    MarkProtection(symbol);
                    // 🔑 EARLY-TP → force ROI-BE (risk must be reduced)
                    _forceRoiBe[guardKey] = now;


                    _logger.LogWarning(
                        "[EARLY-TP][{symbol}][{side}] OK partial {closed}/{total} @price={price} (ATRx{atrx:0.00})",
                        symbol, side, closeQty, realQty, lastClose, earlyTpAtrMult);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[EARLY-TP][{symbol}][{side}] EXEC ERROR",
                        symbol, side);

                    _earlyTpDone.TryRemove(guardKey, out _);
                }
            });
        }


        // ROI->BE guard (separate from ATR staircase; do NOT block further BE stages)
        private readonly ConcurrentDictionary<string, long> _roiBeDone = new(StringComparer.OrdinalIgnoreCase);

        // =====================================================================
        // SL -> BE (soft staged trailing BE) — v8.2 FINAL
        // =====================================================================
        private async Task TryMoveSlToBeAsync(
          BinanceRestClient client,
          string symbol,
          PositionSide side,
          decimal qty,
          decimal entry,
          decimal atr,
          BinanceUsdFuturesOrder? slOrder,
          TradeSignal? signal,
          IReadOnlyList<BinanceFuturesUsdtKline> klines,
          CancellationToken ct)
            {
                if (atr <= 0 || qty <= 0)
                    return;

                if (klines == null || klines.Count < 3)
                    return;

                // last CLOSED candle only
                var c = klines[^2];

                // ===== STABLE GUARD KEY (POSITION IDENTITY) =====
                var baseQtyForGuards = GetOrSetBaseQty(symbol, side, entry, qty);
                var guardKey = BuildPosGuardKey(symbol, side, entry, baseQtyForGuards);

                // =========================
                // 0) RESOLVE ACTIVE SL ORDER (if null)
                // =========================
                if (slOrder == null)
                {
                    try
                    {
                        var ordRes = await client.UsdFuturesApi.Trading.GetOrdersAsync(symbol, ct: ct);
                        if (ordRes.Success && ordRes.Data != null)
                        {
                            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

                            slOrder = ordRes.Data
                                .Where(o =>
                                    o.PositionSide == side &&
                                    o.Side == closeSide &&
                                    o.Type == FuturesOrderType.StopMarket &&
                                    o.Status == OrderStatus.New)
                                .OrderByDescending(o => o.UpdateTime)
                                .FirstOrDefault();
                        }
                    }
                    catch { /* ignore */ }

                    if (slOrder == null)
                        return; // cannot UpdateSL_ProAsync without oldSl.Id
                }

                // ==========================================================
                // Resolve CURRENT SL price from orders (source of truth)
                // ==========================================================
                decimal oldSl = slOrder.StopPrice ?? slOrder.Price;
                if (oldSl <= 0m)
                    return;

                // ==========================================================
                // 1) ROI -> BE (IMMEDIATE PROFIT PROTECT)
                //    Trigger on candle CLOSE (stable), not wick.
                // ==========================================================
                const decimal roiTriggerPct = 2.0m;     // your example: ROI +2%
                const decimal lockPart = 0.25m;         // lock 25% of trigger ROI (2% -> 0.5% ROI locked)
                const decimal feeBufferPct = 0.0005m;   // 0.05% price buffer to cover fees/slip

                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // leverage: prefer signal leverage if present, else assume 1
                var lev = 1m;
                try
                {
                    var pi = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, ct: ct);
                    if (pi.Success && pi.Data != null)
                    {
                        var p = pi.Data.FirstOrDefault(x => x.PositionSide == side && Math.Abs(x.Quantity) > 0m);
                        if (p != null && p.Leverage > 0) lev = p.Leverage;
                    }
                }
                catch { }

                if (signal != null)
                {
                    // If your TradeSignal has Leverage property — use it. Otherwise keep 1.
                    // lev = Math.Max(1m, signal.Leverage);
                }

                // ROI trigger expressed as price delta
                var trigDp = (roiTriggerPct / (100m * lev));
                var lockRoiPct = roiTriggerPct * lockPart;
                var lockDp = (lockRoiPct / (100m * lev));

                // Use close for confirmation
                var pxClose = c.ClosePrice;

                bool roiReached =
                    side == PositionSide.Long
                        ? pxClose >= entry * (1m + trigDp)
                        : pxClose <= entry * (1m - trigDp);

            // execute once per position identity
            // ==========================================================
            // ROI -> BE (execute ONCE per position identity, only if SL really improves)
            // ==========================================================
            bool forceBe = _forceRoiBe.ContainsKey(guardKey);

            if (roiReached || forceBe) 
                {
                    // base BE+ (fee buffer + lock part)
                    var dp = Math.Max(feeBufferPct, lockDp);

                    decimal roiBeSl =
                        side == PositionSide.Long
                            ? entry * (1m + dp)
                            : entry * (1m - dp);

                    // normalize by tick and ensure not worse than entry directionally
                    var f = await _symbolInfo.GetFuturesFiltersAsync(symbol);

                    if (f.tickSize > 0m)
                    {
                        roiBeSl =
                            side == PositionSide.Long
                                ? Math.Floor(roiBeSl / f.tickSize) * f.tickSize
                                : Math.Ceiling(roiBeSl / f.tickSize) * f.tickSize;
                    }

                    if (side == PositionSide.Long && roiBeSl < entry)
                        roiBeSl = entry;

                    if (side == PositionSide.Short && roiBeSl > entry)
                        roiBeSl = entry;

                    // must STRICTLY improve current SL
                    bool improves =
                        (side == PositionSide.Long && roiBeSl > oldSl) ||
                        (side == PositionSide.Short && roiBeSl < oldSl);

                    if (!improves)
                        return;

                    // atomic guard: allow ROI-BE exactly once per position
                    if (!_roiBeDone.TryAdd(guardKey, nowMs))
                        return;

                    var ok = await UpdateSL_ProAsync(
                        client,
                        symbol,
                        side,
                        qty,
                        slOrder,
                        entry,
                        roiBeSl,
                        signal,
                        ct);

                    if (!ok)
                    {
                        // rollback guard if SL update failed
                        _roiBeDone.TryRemove(guardKey, out _);
                    _forceRoiBe.TryRemove(guardKey, out _);
                    return;
                    }

                    // keep state consistent for ATR staircase in this call
                    oldSl = roiBeSl;
                    MarkProtection(symbol);

                    _logger.LogWarning(
                        "[ROI-BE][{symbol}][{side}] ROI>={roi}% => SL->{sl} (lev={lev}, lockRoi={lock}%)",
                        symbol, side, roiTriggerPct, roiBeSl, lev, lockRoiPct);
                }


                // ==========================================================
                // 2) EXISTING ATR STAIRCASE (leave as-is)
                //    (your current impulse/anti-stop-hunt filters can stay here)
                // ==========================================================

                // ===== IMPULSE CONFIRMATION (ANTI STOP-HUNT) =====
                var body = Math.Abs(c.ClosePrice - c.OpenPrice);
                var range = c.HighPrice - c.LowPrice;
                if (range <= 0) return;
                if (body < atr * 0.18m) return;
                if (body / range < 0.30m) return;

                var lastStage = _beStage.TryGetValue(guardKey, out var s) ? s : -1;

                decimal hit =
                    side == PositionSide.Long ? c.HighPrice : c.LowPrice;

                for (int stage = lastStage + 1; stage < BeStages.Length; stage++)
                {
                    var (triggerAtr, bufferAtr) = BeStages[stage];

                    bool reached =
                        side == PositionSide.Long
                            ? hit >= entry + atr * triggerAtr
                            : hit <= entry - atr * triggerAtr;

                    if (!reached)
                        break;

                    decimal buffer = atr * bufferAtr;

                    if (_liquidityGuard.IsDangerRecent(TimeSpan.FromMinutes(6)))
                        buffer *= 0.5m;

                    var structKlines = klines
                        .Take(klines.Count - 1)
                        .TakeLast(5);

                    decimal structural =
                        side == PositionSide.Long
                            ? structKlines.Min(k => k.LowPrice)
                            : structKlines.Max(k => k.HighPrice);

                    decimal beBase =
                        side == PositionSide.Long
                            ? entry + buffer
                            : entry - buffer;

                    decimal newSl =
                        side == PositionSide.Long
                            ? Math.Max(beBase, Math.Max(structural, entry))
                            : Math.Min(beBase, Math.Min(structural, entry));

                    if (side == PositionSide.Long && newSl <= oldSl)
                        continue;
                    if (side == PositionSide.Short && newSl >= oldSl)
                        continue;

                    var ok2 = await UpdateSL_ProAsync(
                        client,
                        symbol,
                        side,
                        qty,
                        slOrder,
                        entry,
                        newSl,
                        signal,
                        ct);

                    if (!ok2)
                    {
                  
                        return;
                    }
                   

                    _beStage[guardKey] = stage;
                    MarkProtection(symbol);

                    _logger.LogWarning(
                        "[BE][{symbol}][{side}] STAGE={stage} SL->{sl} (buffer={buf}ATR)",
                        symbol, side, stage, newSl, bufferAtr);

                    return;
                }
            }



        private static string BuildPosGuardKey(string symbol, PositionSide side, decimal entry)
        {
            string E(decimal v) => v.ToString("0.########", CultureInfo.InvariantCulture);
            return $"{symbol}|{side}|e={E(entry)}";
        }

        private static string BuildPosGuardKey(string symbol, PositionSide side, decimal entry, decimal baseQty)
        {
            string E(decimal v) => v.ToString("0.########", CultureInfo.InvariantCulture);
            return $"{symbol}|{side}|e={E(entry)}|q={E(baseQty)}";
        }

        // =====================================================================
        // EMERGENCY SL  (TRY NORMAL → FALLBACK ALGO RAW on -4120)
        // =====================================================================
        private async Task CreateEmergencySLAsync(
        BinanceRestClient client, string symbol, PositionSide side, decimal qty, decimal entryPrice, TradeSignal? signal, CancellationToken ct)
        {
            if (IsColdStart)
            {
                _logger.LogWarning(
                    "[SUPERVISOR] Skip Emergency SL/TP during cold start ({symbol})",
                    symbol
                );
                return;
            }
            try
            {
                // ==========================================================
                // 1) ЖЁСТКАЯ ПРОВЕРКА ФАКТИЧЕСКОЙ ПОЗИЦИИ
                // ==========================================================
                var posInfo = await client.UsdFuturesApi.Account.GetPositionInformationAsync( ct: ct);
                if (!posInfo.Success || posInfo.Data == null)
                    return;

                var pos = posInfo.Data.FirstOrDefault(p =>
                    p.PositionSide == side &&
                    Math.Abs(p.Quantity) > 0);

                if (pos == null)
                {
                    _logger.LogWarning(
                        "[SUPERVISOR][{symbol}][{side}] SKIP SL → no open position",
                        symbol, side);
                    return;
                }

                // ==========================================================
                // 2) КОЛИЧЕСТВО ЗАКРЫТИЯ
                // ==========================================================
                var closeQty = Math.Min(Math.Abs(pos.Quantity), qty);
                if (closeQty <= 0)
                    return;

                // ==========================================================
                // 3) РАСЧЁТ SL
                // ==========================================================
                decimal rawSl;

                if (signal?.StopLoss > 0)
                {
                    rawSl = signal.StopLoss;
                }
                else
                {
                    // BOOTSTRAP SAFE: без klines
                    // База: процент от entry + лёгкая адаптация по режиму
                    decimal pct = _regimeNow switch
                    {
                        MarketRegime.Range => 0.010m,        // 1.0%
                        MarketRegime.Squeeze => 0.012m,      // 1.2%
                        MarketRegime.UpTrend or MarketRegime.DownTrend => 0.014m, // 1.4%
                        MarketRegime.VolatileChop => 0.016m, // 1.6%
                        _ => 0.018m                          // 1.8%
                    };

                    rawSl = side == PositionSide.Long
                        ? entryPrice * (1m - pct)
                        : entryPrice * (1m + pct);
                }

                // ==========================================================
                // 4) TICK + MARK PRICE SAFETY
                // ==========================================================
                var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
                var tick = filters.tickSize > 0 ? filters.tickSize : 0.0001m;

                decimal sl =
      side == PositionSide.Long
          ? Math.Floor(rawSl / tick) * tick   // ✅ LONG → вниз
          : Math.Ceiling(rawSl / tick) * tick; // ✅ SHORT → вверх

                decimal mark = await GetMarkPriceSafeAsync(client, symbol, entryPrice, ct);

                if (mark > 0)
                {
                    if (side == PositionSide.Long && sl >= mark)
                        sl = mark - tick;

                    if (side == PositionSide.Short && sl <= mark)
                        sl = mark + tick;
                }

                var orderSide = side == PositionSide.Long
                    ? OrderSide.Sell
                    : OrderSide.Buy;

                var f = await _symbolInfo.GetFuturesFiltersAsync(symbol);
                closeQty = Math.Floor(closeQty / f.step) * f.step;
                if (closeQty < f.minQty) return;
                // ==========================================================
                // 5) ОТПРАВКА (NORMAL → ALGO RAW)
                // ==========================================================
                _dispatcher.Enqueue(async ct =>
                {
                    using var c = _factory.CreateRestClient();

                    var posInfo = await c.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                    var real = posInfo.Data.FirstOrDefault(p =>
                        p.Symbol == symbol &&
                        p.PositionSide == side &&
                        Math.Abs(p.Quantity) > 0m);

                    if (real == null) return;

                    closeQty = Math.Min(closeQty, Math.Abs(real.Quantity));
                    closeQty = Math.Floor(closeQty / f.step) * f.step;
                    if (closeQty < f.minQty) return;

                    var res = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: symbol,
                        side: orderSide,
                        type: FuturesOrderType.StopMarket,
                        quantity: closeQty,
                        positionSide: side,
                        stopPrice: sl,
                        workingType: WorkingType.Mark,
                        reduceOnly: null,
                        ct: ct);

                    if (res.Success)
                    {
                        _logger.LogInformation(
                            "[SUPERVISOR] SL CREATED (NORMAL) {symbol} {side} sl={sl}",
                            symbol, side, sl);
                        return;
                    }

                    // ======================================================
                    // 6) FALLBACK → ALGO RAW (-4120)
                    // ======================================================
                    if (IsAlgoRequired(res.Error))
                    {
                        // === SAFE precision for ALGO-RAW ===

                        var (safeQty, safeTrig) = await NormalizeForAlgoAsync(symbol, closeQty, sl, ct);

                        // minQty gate
                        var filters2 = await _symbolInfo.GetFuturesFiltersAsync(symbol);
                        if (safeQty < filters2.minQty)
                        {
                            _logger.LogError("[ALGO-RAW][SL] SKIP {symbol} qty {q} < minQty {min}",
                                symbol, safeQty, filters2.minQty);
                            return;
                        }

                        _logger.LogWarning(
                            "[SUPERVISOR] SL requires ALGO endpoint (-4120) → RAW ALGO {symbol} {side}",
                            symbol, side);

                        var ok = await _algoRaw.PlaceConditionalAsync(
                        symbol: symbol,
                        side: orderSide,
                        positionSide: side,
                        type: "STOP_MARKET",
                        quantity: safeQty,        // ✅ ИСПОЛЬЗУЕМ SAFE
                        triggerPrice: safeTrig,   // ✅ ИСПОЛЬЗУЕМ SAFE
                        workingType: "MARK_PRICE",
                        reduceOnly: true,
                        ct: ct);

                        if (ok)
                        {
                            _logger.LogInformation(
                                "[SUPERVISOR] SL CREATED (ALGO-RAW) {symbol} {side} sl={sl}",
                                symbol, side, sl);
                            return;
                        }

                        _logger.LogWarning(
                            "[SUPERVISOR] SL skipped: ALGO-RAW failed {symbol} {side}",
                            symbol, side);
                        return;
                    }

                    _logger.LogError(
                        "[SUPERVISOR] ERROR SL create (NORMAL) {symbol}: {err}",
                        symbol, res.Error);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SUPERVISOR] EX SL create {symbol}", symbol);
            }
        }

        // =====================================================================
        // EMERGENCY TP  (TRY NORMAL → FALLBACK ALGO RAW on -4120)
        // =====================================================================
        private async Task CreateEmergencyTPAsync(BinanceRestClient client, string symbol, PositionSide side,
        decimal qty, decimal entryPrice, TradeSignal? signal, CancellationToken ct)
        {
            if (IsColdStart)
            {
                _logger.LogWarning(
                    "[SUPERVISOR] Skip Emergency SL/TP during cold start ({symbol})",
                    symbol
                );
                return;
            }
            try
            {
                var posInfo = await client.UsdFuturesApi.Account.GetPositionInformationAsync( ct: ct);
                if (!posInfo.Success || posInfo.Data == null) return;

                var pos = posInfo.Data.FirstOrDefault(p =>
                    p.PositionSide == side && Math.Abs(p.Quantity) > 0);

                if (pos == null) return;

                var closeQty = Math.Min(Math.Abs(pos.Quantity), qty);
                if (closeQty <= 0) return;

                decimal trigger;
                if (signal?.TakeProfits?.Count > 0)
                    trigger = signal.TakeProfits[0];
                else
                    trigger = side == PositionSide.Long
                        ? entryPrice * 1.016m
                        : entryPrice * 0.984m;

                var f = await _symbolInfo.GetFuturesFiltersAsync(symbol);

                var tick = f.tickSize > 0 ? f.tickSize : 0.0001m;

                trigger =
                    side == PositionSide.Long
                        ? Math.Ceiling(trigger / tick) * tick
                        : Math.Floor(trigger / tick) * tick;

              //  trigger = NormalizeToStep(trigger, f.tickSize > 0 ? f.tickSize : 0.0001m);

                if (side == PositionSide.Long && trigger <= entryPrice)
                    trigger = entryPrice + f.tickSize * 3;
                if (side == PositionSide.Short && trigger >= entryPrice)
                    trigger = entryPrice - f.tickSize * 3;

                var orderSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;



                _dispatcher.Enqueue(async token =>
                {
                    using var c = _factory.CreateRestClient();


                    var posInfo2 = await c.UsdFuturesApi.Account.GetPositionInformationAsync(ct: token);
                    var real = posInfo2.Data?.FirstOrDefault(p =>
                        p.Symbol == symbol &&
                        p.PositionSide == side &&
                        Math.Abs(p.Quantity) > 0m);

                    if (real == null) return;

                    closeQty = Math.Min(closeQty, Math.Abs(real.Quantity));
                    closeQty = Math.Floor(closeQty / f.step) * f.step;

                    if (closeQty < f.minQty) return;


                    var res = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: symbol,
                        side: orderSide,
                        type: FuturesOrderType.TakeProfitMarket,
                        quantity: closeQty,
                        positionSide: side,
                        stopPrice: trigger,
                        reduceOnly: null,
                        ct: token);

                    if (res.Success) return;

                    if (!IsAlgoRequired(res.Error)) return;

                    var safeQty = closeQty;

                    var tick = f.tickSize > 0 ? f.tickSize : 0.0001m;

                    var safeTrig =
                        side == PositionSide.Long
                            ? Math.Ceiling(trigger / tick) * tick
                            : Math.Floor(trigger / tick) * tick;


                    if (safeQty < f.minQty) return;

                    _logger.LogWarning(
                        "[ALGO-RAW][PRE][TP] {symbol} qty={qty} trig={trig}",
                        symbol, safeQty, safeTrig);

                    await _algoRaw.PlaceConditionalAsync(
                        symbol: symbol,
                        side: orderSide,
                        positionSide: side,
                        type: "TAKE_PROFIT_MARKET",
                        quantity: safeQty,
                        triggerPrice: safeTrig,
                        workingType: "CONTRACT_PRICE",
                        reduceOnly: true,
                        ct: token);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SUPERVISOR] EX CreateEmergencyTP {symbol}", symbol);
            }
        }


        private static int GetPrecisionFromStep(decimal step)
        {
            var s = step.ToString(CultureInfo.InvariantCulture);

            if (!s.Contains('.'))
                return 0;

            return s.TrimEnd('0').Split('.')[1].Length;
        }

        private static decimal Clamp(decimal value, int precision)
        {
            if (precision <= 0)
                return Math.Floor(value);

            var factor = (decimal)Math.Pow(10, precision);
            return Math.Floor(value * factor) / factor;
        }

        private static bool IsAlgoRequired(CryptoExchange.Net.Objects.Error? err)
        {
            if (err == null) return false;

            if (err.Code == -4120) return true;

            var msg = (err.Message ?? string.Empty).ToLowerInvariant();
            return msg.Contains("algo order api") || msg.Contains("algoorder") || msg.Contains("not supported for this endpoint");
        }

        // =====================================================================
        // PLACEHOLDERS: keep your existing methods below
        // =====================================================================
        private async Task ManageRunnerTpExtensionAsync(BinanceRestClient client, string symbol,
            PositionSide side, decimal qty, decimal entryPrice, TradeSignal? signal, List<BinanceUsdFuturesOrder> orders, IReadOnlyList<BinanceFuturesUsdtKline> klines,
            CancellationToken ct)
        {
            await Task.CompletedTask;
        }

        // =====================================================================
        // RUNNER (твоя логика) + SL update (v8.2 PRO)
        // =====================================================================
        private async Task ManageRunnerTpAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entryPrice,
            List<BinanceUsdFuturesOrder> orders,
            TradeSignal? signal,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            CancellationToken ct)
        {
            // ===== STRONG TREND RUNNER FIX =====
            bool strongTrend =
                _regimeNow == MarketRegime.StrongUpTrend ||
                _regimeNow == MarketRegime.StrongDownTrend;

            if (!strongTrend)
                return;


            if (signal?.TakeProfit is null || signal.TakeProfit <= 0)
                return;

            var priceRes = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(symbol, ct: ct);
            if (!priceRes.Success || priceRes.Data == null)
                return;

            var price = priceRes.Data.Price;

            bool tpHit =
                side == PositionSide.Long ? price >= signal.TakeProfit : price <= signal.TakeProfit;

            if (!tpHit) return;

            bool trendOk =
                side == PositionSide.Long
                    ? _regimeNow is MarketRegime.StrongUpTrend or MarketRegime.UpTrend
                    : _regimeNow is MarketRegime.StrongDownTrend or MarketRegime.DownTrend;

            if (!trendOk) return;

            var atr = signal.Atr ?? 0m;
            if (atr <= 0 || klines.Count < 2) return;

            var last = klines[^1];
            var body = Math.Abs(last.ClosePrice - last.OpenPrice);
            if (body < atr * 0.7m) return;

            if (_liquidityGuard.LastDanger?.Block == true) return;

            // 1) частичное закрытие (70%)
            var closeQty = Math.Round(qty * 0.7m, 8);
            var runnerQty = qty - closeQty;
            if (closeQty <= 0 || runnerQty <= 0) return;

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
            _dispatcher.Enqueue(async token =>
            {
                using var c = _factory.CreateRestClient();

                // Реальная позиция на момент исполнения
                var info = await c.UsdFuturesApi.Account.GetPositionInformationAsync(ct: token);
                if (!info.Success || info.Data == null) return;

                var real = info.Data.FirstOrDefault(p =>
                    p.Symbol == symbol &&
                    p.PositionSide == side &&
                    Math.Abs(p.Quantity) > 0m);

                if (real == null) return;

                var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
                var step = filters.step > 0 ? filters.step : 1m;

                decimal realQtyAbs = Math.Abs(real.Quantity);

                decimal wantClose = realQtyAbs * 0.70m;
                decimal closeQty = Math.Floor(wantClose / step) * step;

                if (closeQty < filters.minQty) return;

                decimal runnerQty = realQtyAbs - closeQty;
                runnerQty = Math.Floor(runnerQty / step) * step;

                if (runnerQty < filters.minQty)
                {
                    // если раннер слишком мал — тогда закрываем всё
                    closeQty = realQtyAbs;
                    runnerQty = 0m;
                }
                await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: closeSide,
                    type: FuturesOrderType.Market,
                    quantity: closeQty,
                    positionSide: side,
                    ct: token);

                _logger.LogInformation("[TP-EXT][{symbol}] Partial TP executed close={closed} real={real}, runner={runner}",
                    symbol, closeQty, realQtyAbs, runnerQty);

                if (runnerQty <= 0m) return;

                decimal newSl =
                    side == PositionSide.Long ? entryPrice + atr * 0.25m : entryPrice - atr * 0.25m;

                var open = await c.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: token);
                if (!open.Success || open.Data == null) return;

                var slOrder = open.Data.FirstOrDefault(o =>
                    o.Type == FuturesOrderType.StopMarket &&
                    o.PositionSide == side &&
                    o.Side == closeSide);

                if (slOrder != null)
                    await UpdateSL_ProAsync(c, symbol, side, runnerQty, slOrder, entryPrice, newSl, signal, token);

                MarkProtection(symbol);
            });
        }

        private async Task<bool> UpdateSL_ProAsync(
             BinanceRestClient client,
             string symbol,
             PositionSide side,
             decimal qty,
             BinanceUsdFuturesOrder oldSl,
             decimal entry,
             decimal newSl,
             TradeSignal? signal,
             CancellationToken ct)
        {
            if (newSl <= 0) return false;

            var f = await _symbolInfo.GetFuturesFiltersAsync(symbol);

            try
            {
                // === 1. Реальная позиция ===
                var posInfo = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                if (!posInfo.Success || posInfo.Data == null) return false;

                var realPos = posInfo.Data.FirstOrDefault(p =>
                    p.Symbol == symbol &&
                    p.PositionSide == side &&
                    Math.Abs(p.Quantity) > 0m);

                if (realPos == null) return false;

                // ==========================================================
                // 🔥 ГЛАВНЫЙ ФИКС — SL НИКОГДА БОЛЬШЕ ПОЗИЦИИ
                // ==========================================================
                var realQtyAbs = Math.Abs(realPos.Quantity);
                qty = Math.Min(Math.Abs(qty), realQtyAbs);
                if (qty <= 0m) return false;

                var safeQty = NormalizeToStep(qty, f.step > 0 ? f.step : 1m);
               // var safeTrig = NormalizeToStep(newSl, f.tickSize > 0 ? f.tickSize : 0.0001m);
                decimal safeTrig =
                side == PositionSide.Long
                 ? Math.Floor(newSl / f.tickSize) * f.tickSize
                    : Math.Ceiling(newSl / f.tickSize) * f.tickSize;

                if (side == PositionSide.Long && safeTrig < entry)
                    safeTrig = entry;
                if (side == PositionSide.Short && safeTrig > entry)
                    safeTrig = entry;

                if (safeQty < f.minQty) return false;

                var orderSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

                // === 2. СТАВИМ НОВЫЙ SL СНАЧАЛА ===
                var place = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: orderSide,
                    type: FuturesOrderType.StopMarket,
                    quantity: safeQty,
                    stopPrice: safeTrig,
                    positionSide: side,
                    workingType: WorkingType.Mark,
                    reduceOnly: null,
                    ct: ct);

                bool placedOk = place.Success;

                // fallback без WorkingType.Mark
                if (!placedOk)
                {
                    place = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: symbol,
                        side: orderSide,
                        type: FuturesOrderType.StopMarket,
                        quantity: safeQty,
                        stopPrice: safeTrig,
                        positionSide: side,
                        reduceOnly: null,
                        ct: ct);

                    placedOk = place.Success;
                }

                // fallback ALGO RAW
                if (!placedOk && IsAlgoRequired(place.Error))
                {
                    placedOk = await _algoRaw.PlaceConditionalAsync(
                        symbol: symbol,
                        side: orderSide,
                        positionSide: side,
                        type: "STOP_MARKET",
                        quantity: safeQty,
                        triggerPrice: safeTrig,
                        workingType: "MARK_PRICE",
                        reduceOnly: null,
                        ct: ct);
                }

                if (!placedOk)
                {
                    _logger.LogWarning("[SL][{symbol}][{side}] NEW SL FAILED — OLD KEPT", symbol, side);
                    return false;
                }

                // === 3. ТОЛЬКО ПОТОМ УДАЛЯЕМ СТАРЫЙ SL ===
                try
                {
                    await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, oldSl.Id, ct: ct);
                }
                catch { }

                _logger.LogWarning("[SL][{symbol}][{side}] SL UPDATED -> {sl} qty={qty}", symbol, side, safeTrig, safeQty);

                if (signal != null && !signal.IsManual)
                    HookAiLearningOnSlMove(signal, symbol, side, entry, safeTrig);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SL][{symbol}][{side}] UpdateSL EX");
                return false;
            }
        }

        // =====================================================================
        // RAW BINANCE ALGO ORDER (POST /fapi/v1/algoOrder)
        // =====================================================================
        private sealed class BinanceAlgoOrderRaw
        {
            private readonly HttpClient _http;
            private readonly ILogger _logger;
            private readonly string _apiKey;
            private readonly string _apiSecret;
            private readonly string _baseUrl;
            public BinanceAlgoOrderRaw(IConfiguration cfg, IHttpClientFactory httpFactory, ILogger logger)
            {
                _logger = logger;

                _apiKey = cfg["Binance:ApiKey"] ?? string.Empty;
                _apiSecret = cfg["Binance:SecretKey"] ?? string.Empty;
                _baseUrl = (cfg["Binance:FuturesBaseUrl"] ?? "https://fapi.binance.com").TrimEnd('/');

                _http = httpFactory.CreateClient("BinanceAlgoRaw");
                _http.Timeout = TimeSpan.FromSeconds(8);
            }

            public async Task<bool> PlaceConditionalAsync(string symbol, OrderSide side, PositionSide positionSide,
            string type, decimal quantity, decimal triggerPrice, string workingType, bool? reduceOnly, CancellationToken ct)
            {
                if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
                {
                    _logger.LogError("[ALGO-RAW] Missing Binance:ApiKey / Binance:ApiSecret in config");
                    return false;
                }

                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                static string D(decimal v)
                {
                    // fixed invariant, then trim trailing zeros
                    var s = v.ToString("0.############################", CultureInfo.InvariantCulture);
                    if (s.Contains('.'))
                        s = s.TrimEnd('0').TrimEnd('.');
                    return s;
                }

                var q = new List<KeyValuePair<string, string>>
                {
                    new("algoType", "CONDITIONAL"),
                    new("symbol", symbol),
                    new("side", side == OrderSide.Buy ? "BUY" : "SELL"),
                    new("type", type),
                    new("timestamp", ts.ToString(CultureInfo.InvariantCulture)),
                    new("workingType", workingType),
                    new("triggerPrice", D(triggerPrice)),
                    new("positionSide", positionSide.ToString().ToUpperInvariant()),
                    new("quantity", D(quantity))
                };

                // reduceOnly — только если positionSide == BOTH (в Hedge не шлём)
                if (reduceOnly.HasValue)
                    q.Add(new("reduceOnly", reduceOnly.Value ? "true" : "false"));

                var query = BuildQuery(q);
                var sig = Sign(query, _apiSecret);

                var url = $"{_baseUrl}/fapi/v1/algoOrder?{query}&signature={sig}";

                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.TryAddWithoutValidation("X-MBX-APIKEY", _apiKey);

                try
                {
                    using var resp = await _http.SendAsync(req, ct);
                    var body = await resp.Content.ReadAsStringAsync(ct);

                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogError("[ALGO-RAW] HTTP {code} body={body}", (int)resp.StatusCode, body);
                        return false;
                    }

                    _logger.LogInformation("[ALGO-RAW] OK {symbol} {type} posSide={ps} trig={tp} body={body}",
                        symbol, type, positionSide, triggerPrice, body);

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ALGO-RAW] EX PlaceConditionalAsync {symbol}", symbol);
                    return false;
                }
            }

            private static string BuildQuery(IEnumerable<KeyValuePair<string, string>> q)
            {
                var sb = new StringBuilder();
                foreach (var kv in q)
                {
                    if (sb.Length > 0) sb.Append('&');
                    sb.Append(Uri.EscapeDataString(kv.Key));
                    sb.Append('=');
                    sb.Append(Uri.EscapeDataString(kv.Value));
                }
                return sb.ToString();
            }

            private static string Sign(string queryString, string secret)
            {
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(queryString));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
        private void HookAiLearningOnSlMove(TradeSignal? signal, string symbol, PositionSide side, decimal entry, decimal newSl)
        {
            try
            {
                if (signal != null && !signal.IsManual)
                {
                    var sigSide = side == PositionSide.Long ? SignalSide.Buy : SignalSide.Sell;

                    _manualHandler.RegisterStop(symbol);
                    _aiLearning.RecordTrade(symbol, sigSide, entry, newSl, _regimeNow);
                }
            }
            catch { }
        }

        private static decimal NormalizeToStep(decimal value, decimal step)
        {
            if (step <= 0) return value;
            return Math.Floor(value / step) * step;
        }

        private async Task MultiLayerTrailingAsync(BinanceRestClient client, string symbol, PositionSide side, decimal qty, decimal entryPrice,
        TradeSignal? signal, List<BinanceUsdFuturesOrder> orders, IReadOnlyList<BinanceFuturesUsdtKline> klines, CancellationToken ct)
        {
            await Task.CompletedTask;
        }

        private static async Task<decimal> GetMarkPriceSafeAsync(
        BinanceRestClient client,
        string symbol,
        decimal fallback,
        CancellationToken ct)
        {
            try
            {
                // 1) Prefer MARK PRICE if available in your Binance.Net version
                try
                {
                    var mp = await client.UsdFuturesApi.ExchangeData.GetMarkPriceAsync(symbol, ct: ct);
                    if (mp.Success && mp.Data != null && mp.Data.MarkPrice > 0)
                        return mp.Data.MarkPrice;
                }
                catch
                {
                    // ignore: method not available in some versions
                }

                // 2) Fallback: last price
                var r = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(symbol, ct: ct);
                if (r.Success && r.Data != null && r.Data.Price > 0)
                    return r.Data.Price;
            }
            catch { }

            return fallback > 0 ? fallback : 0m;
        } 
        private async Task TryHarvestProfitAsync(
           BinanceRestClient client,
           EngineState state,
           string symbol,
           PositionSide side,
           BinancePositionDetailsUsdt pos,
           IReadOnlyList<BinanceFuturesUsdtKline> klines,
           decimal aiEdgeScore,
           decimal minUsd,
           CancellationToken ct)
        {

            // ==========================================================
            // 🔒 BLOCK HARVEST right after EARLY-TP (Binance sync lag)
            // ==========================================================
            var harvestKey = $"{symbol}|{side}";

            if (_recentPartialClose.TryGetValue(harvestKey, out var ts))
            {
                var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ts;
                if (ageMs < 8_000) // 8 seconds hard block
                {
                    _logger.LogInformation(
                        "[HARVEST][{symbol}][{side}] SKIP → recent EARLY-TP ({ms}ms)",
                        symbol, side, ageMs);
                    return;
                }

                _recentPartialClose.TryRemove(harvestKey, out _);
            }




            var sKey = EngineState.Key(symbol);
            var st = state.Symbols.GetOrAdd(sKey, _ => new SymbolState());

            // throttle
            if ((DateTime.UtcNow - st.LastHarvestUtc) < TimeSpan.FromSeconds(90))
                return;

            // ==========================================================
            // 🔒 ЖЁСТКАЯ ПРОВЕРКА ФАКТИЧЕСКОЙ ПОЗИЦИИ
            // ==========================================================
            var posInfo = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
            if (!posInfo.Success || posInfo.Data == null)
                return;

            var realPos = posInfo.Data.FirstOrDefault(p =>
                p.Symbol == symbol &&
                p.PositionSide == side &&
                Math.Abs(p.Quantity) > 0);

            if (realPos == null)
            {
                _logger.LogWarning("[HARVEST][{symbol}][{side}] SKIP → no open position", symbol, side);
                return;
            }

            decimal qty = Math.Abs(realPos.Quantity);
            if (qty <= 0) return;

            // ==========================================================
            // uPnL
            // ==========================================================
            decimal uPnl;
            try { uPnl = realPos.UnrealizedPnl; }
            catch { return; }

            if (uPnl <= 0m || uPnl < minUsd)
                return;

            decimal atr = _marketData.CalculateAtr(klines);
            if (atr <= 0) atr = 0.00000001m;

            decimal rr = Math.Abs(realPos.MarkPrice - realPos.EntryPrice) / atr;

            decimal harvestPct =
             (aiEdgeScore >= 0.75m && rr >= 1.10m) ? 0.22m :
             (aiEdgeScore >= 0.65m && rr >= 0.90m) ? 0.16m :
             (aiEdgeScore >= 0.55m && rr >= 0.70m) ? 0.12m :
             (rr >= 0.55m) ? 0.08m :
             0m;

            if (harvestPct <= 0m)
                return;

            //  decimal closeQty = Math.Round(qty * harvestPct, 8);
            // if (closeQty <= 0) return;

            // ==========================================================
            // 🔥 FULL vs PARTIAL CLOSE LOGIC (КЛЮЧЕВО)
            // ==========================================================

            decimal closeQty = qty * harvestPct;

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            var step = filters.step > 0 ? filters.step : 1m;

            // ❗ КЛЮЧЕВО: всегда вниз
            closeQty = Math.Floor(closeQty / step) * step;

            if (closeQty < filters.minQty)
            {
                _logger.LogInformation(
                    "[HARVEST][{symbol}][{side}] SKIP → rounded closeQty {q} < minQty {min}",
                    symbol, side, closeQty, filters.minQty);
                return;
            }


            bool isFullClose = closeQty >= qty;

            if (closeQty >= qty)
            {
                closeQty = qty;
                isFullClose = true;
            }
            else
            {
                isFullClose = false;
            }

            if (isFullClose)
            {
                _logger.LogInformation(
                    "[HARVEST][{symbol}][{side}] FULL CLOSE via harvest",
                    symbol, side);
            }


            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
     symbol: symbol,
     side: closeSide,
     type: FuturesOrderType.Market,
     quantity: closeQty,
     positionSide: side,
     ct: ct);


            if (!res.Success)
            {
                _logger.LogWarning("[HARVEST][{symbol}][{side}] FAIL: {err}", symbol, side, res.Error);
                return;
            }

            //decimal addToBucket = uPnl * harvestPct;
            decimal addToBucket = uPnl * (closeQty / qty);
            st.RealizedPnlBucketUsd += Math.Max(0m, addToBucket);

            if (closeQty > 0 && !isFullClose)
            {
                st.LastHarvestUtc = DateTime.UtcNow;
            }
            
            st.HarvestsToday++;

            _logger.LogInformation(
                "[HARVEST][{symbol}][{side}] OK closeQty={q} uPnl={pnl:F2} addBucket={b:F2} edge={e:F2} rr={rr:F2}",
                symbol, side, closeQty, uPnl, addToBucket, aiEdgeScore, rr);

            if (!isFullClose)
            {
                _recentPartialClose[$"{symbol}|{side}"] =
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

        }


        public async Task HandleUiActionAsync(PositionActionRequest req)
        {
            if (!UiSpamGuard(req.Symbol, req.Side, req.Action.ToString()))
            {
                _logger.LogInformation("[UI][GUARD] spam blocked {symbol} {side} {action}", req.Symbol, req.Side, req.Action);
                return;
            }

            var pos = GetTrackedPosition(req.Symbol, req.Side);
            if (pos == null) return;// NOTE: dummy object, real position resolved later via REST

            switch (req.Action)
            {
                case PositionActionType.CloseMarket:
                    await ClosePositionAsync(pos, CloseReason.ManualUi);
                    break;

                case PositionActionType.UpdateStopLoss:
                    await UpdateStopLossAsync(pos, req.Price!.Value, reason: "UI");
                    break;

                case PositionActionType.UpdateTakeProfit:
                    await UpdateTakeProfitAsync(pos, req.Price!.Value, reason: "UI");
                    break;
            }
        }

        private Task UpdateTakeProfitAsync(BinancePositionDetailsUsdt pos, decimal price, string reason)
        {
            _dispatcher.Enqueue(async ct =>
            {
                using var c = _factory.CreateRestClient();

                var info = await c.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                if (!info.Success || info.Data == null) return;

                var real = info.Data.FirstOrDefault(p =>
                    p.Symbol == pos.Symbol &&
                    p.PositionSide == pos.PositionSide &&
                    Math.Abs(p.Quantity) > 0);

                if (real == null) return;

                var filters = await _symbolInfo.GetFuturesFiltersAsync(real.Symbol);
                var tick = filters.tickSize > 0 ? filters.tickSize : 0.0001m;
                var tp = Math.Round(price / tick) * tick;

                var open = await c.UsdFuturesApi.Trading.GetOpenOrdersAsync(real.Symbol, ct: ct);
                if (!open.Success || open.Data == null) return;

                var orderSide = real.PositionSide == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

                // existing TP for this side
                var oldTp = open.Data
                    .Where(o => o.Type == FuturesOrderType.TakeProfitMarket && o.PositionSide == real.PositionSide && o.Side == orderSide)
                    .OrderByDescending(o => o.CreateTime)
                    .FirstOrDefault();

                // place new TP first
                var place = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: real.Symbol,
                    side: orderSide,
                    type: FuturesOrderType.TakeProfitMarket,
                    quantity: Math.Abs(real.Quantity),
                    positionSide: real.PositionSide,
                    stopPrice: tp,
                    reduceOnly: null,
                    workingType: WorkingType.Mark,
                    ct: ct);

                if (!place.Success)
                {
                    // fallback without WorkingType.Mark
                    place = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: real.Symbol,
                        side: orderSide,
                        type: FuturesOrderType.TakeProfitMarket,
                        quantity: Math.Abs(real.Quantity),
                        positionSide: real.PositionSide,
                        stopPrice: tp,
                        reduceOnly: null,
                        ct: ct);
                }

                if (!place.Success)
                {
                    // fallback to ALGO RAW if required
                    if (IsAlgoRequired(place.Error))
                    {
                        var safeQty = NormalizeToStep(Math.Abs(real.Quantity), filters.step > 0 ? filters.step : 1m);
                        var safeTrig = NormalizeToStep(tp, filters.tickSize > 0 ? filters.tickSize : 0.0001m);

                        if (safeQty >= filters.minQty)
                        {
                            await _algoRaw.PlaceConditionalAsync(
                                symbol: real.Symbol,
                                side: orderSide,
                                positionSide: real.PositionSide,
                                type: "TAKE_PROFIT_MARKET",
                                quantity: safeQty,
                                triggerPrice: safeTrig,
                                workingType: "CONTRACT_PRICE",
                                reduceOnly: true,
                                ct: ct);
                        }
                        else return;
                    }
                    else return;
                }

                // cancel old TP after new is placed
                if (oldTp != null)
                {
                    try { await c.UsdFuturesApi.Trading.CancelOrderAsync(real.Symbol, oldTp.Id, ct: ct); }
                    catch { /* ignore */ }
                }

                _logger.LogWarning("[UI][TP] {symbol} {side} -> {tp} ({reason})", real.Symbol, real.PositionSide, tp, reason);
                MarkProtection(real.Symbol);
            });

            return Task.CompletedTask;
        }

        private Task UpdateStopLossAsync(BinancePositionDetailsUsdt pos, decimal price, string reason)
        {
            _dispatcher.Enqueue(async ct =>
            {
                using var c = _factory.CreateRestClient();

                var info = await c.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                if (!info.Success || info.Data == null) return;

                var real = info.Data.FirstOrDefault(p =>
                    p.Symbol == pos.Symbol &&
                    p.PositionSide == pos.PositionSide &&
                    Math.Abs(p.Quantity) > 0);

                if (real == null) return;

                var filters = await _symbolInfo.GetFuturesFiltersAsync(real.Symbol);
                var tick = filters.tickSize > 0 ? filters.tickSize : 0.0001m;
                var sl = Math.Round(price / tick) * tick;

                var open = await c.UsdFuturesApi.Trading.GetOpenOrdersAsync(real.Symbol, ct: ct);
                if (!open.Success || open.Data == null) return;

                var closeSide = real.PositionSide == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

                // find existing SL for this side
                var oldSl = open.Data
                    .Where(o => o.Type == FuturesOrderType.StopMarket && o.PositionSide == real.PositionSide && o.Side == closeSide)
                    .OrderByDescending(o => o.CreateTime)
                    .FirstOrDefault();

                // if no SL -> just place emergency SL
                if (oldSl == null)
                {
                    await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: real.Symbol,
                        side: closeSide,
                        type: FuturesOrderType.StopMarket,
                        quantity: Math.Abs(real.Quantity),
                        positionSide: real.PositionSide,
                        stopPrice: sl,
                        reduceOnly: null,
                        workingType: WorkingType.Mark,
                        ct: ct);

                    _logger.LogWarning("[UI][SL] {symbol} {side} -> {sl} ({reason}) [created]", real.Symbol, real.PositionSide, sl, reason);
                    MarkProtection(real.Symbol);
                    return;
                }

                // NO-GAP: place new first, cancel old after confirm
                var ok = await UpdateSL_ProAsync(
                    c,
                    real.Symbol,
                    real.PositionSide,
                    Math.Abs(real.Quantity),
                    oldSl,
                    entry: real.EntryPrice > 0 ? real.EntryPrice : sl, // fallback (rare)
                    newSl: sl,
                    signal: null,
                    ct);

                if (ok)
                {
                    _logger.LogWarning("[UI][SL] {symbol} {side} -> {sl} ({reason}) [updated]", real.Symbol, real.PositionSide, sl, reason);
                    MarkProtection(real.Symbol);
                }
                else
                {
                    _logger.LogWarning("[UI][SL] update failed (kept old SL) {symbol} {side}", real.Symbol, real.PositionSide);
                }
            });

            return Task.CompletedTask;
        }

        private Task ClosePositionAsync(BinancePositionDetailsUsdt pos, CloseReason reason)
        {
            _dispatcher.Enqueue(async ct =>
            {
                using var c = _factory.CreateRestClient();

                var info = await c.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                if (!info.Success || info.Data == null)
                    return;

                var real = info.Data.FirstOrDefault(p =>
                    p.Symbol == pos.Symbol &&
                    p.PositionSide == pos.PositionSide &&
                    Math.Abs(p.Quantity) > 0);

                if (real == null)
                    return;

                var closeSide = real.PositionSide == PositionSide.Long
                    ? OrderSide.Sell
                    : OrderSide.Buy;

                await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: real.Symbol,
                    side: closeSide,
                    type: FuturesOrderType.Market,
                    quantity: Math.Abs(real.Quantity),
                    positionSide: real.PositionSide,
                    ct: ct);

                _logger.LogWarning(
                    "[UI][CLOSE] {symbol} {side} closed by {reason}",
                    real.Symbol, real.PositionSide, reason);
            });

            return Task.CompletedTask;
        }

        private async Task<(decimal qty, decimal trig)> NormalizeForAlgoAsync(string symbol, decimal qty, decimal trigger, CancellationToken ct)
        {
            var f = await _symbolInfo.GetFuturesFiltersAsync(symbol);

            var step = f.step > 0 ? f.step : 1m;
            var tick = f.tickSize > 0 ? f.tickSize : 0.0001m;

            // step/tick clamp
            var q1 = Math.Floor(qty / step) * step;
            var t1 = Math.Round(trigger / tick) * tick;


            // HARD precision clamp (🔥 ключ к -1111)           
            var qtyPrecision = GetPrecisionFromStep(f.step);
            var pricePrecision = GetPrecisionFromStep(f.tickSize);

            var safeQty = Clamp(q1, qtyPrecision);
            var safeTrig = Clamp(t1, pricePrecision);

            return (safeQty, safeTrig);
        }
        private BinancePositionDetailsUsdt? GetTrackedPosition(string symbol, PositionSide side)
        {
            var sKey = EngineState.Key(symbol);

            if (!_engineState.Symbols.TryGetValue(sKey, out _))
                return null;

            // Supervisor уже работает с фактической позицией через REST/WS,
            // UI-action не должен сам решать qty/entry
            // Поэтому просто возвращаем null как "gate"
            // Реальные действия делаются через существующие методы ниже
            return new BinancePositionDetailsUsdt
            {
                Symbol = symbol,
                PositionSide = side
            };
        }
    }

}
