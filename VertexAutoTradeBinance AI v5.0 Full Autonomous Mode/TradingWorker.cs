using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Threading.Channels;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.Bootstrap;
using VertexAutoTradeBinance8.Services.Engine;
using VertexAutoTradeBinance8.Services.Formatting;
using VertexAutoTradeBinance8.Strategy;
using static VertexAutoTradeBinance8.Services.AiTimeframeSelectorService;

namespace VertexAutoTradeBinance8
{
    public class TradingWorker : BackgroundService
    {
        private readonly ILogger<TradingWorker> _logger;
        private readonly TradingOptions _options;

        private readonly MarketDataService _market;
        private readonly MarketDataFacade _marketDataFacade;
        private readonly StrategyEngine _strategy;
        private readonly RiskManager _risk;
        private readonly OrderExecutor _executor;
        private readonly BinanceClientFactory _factory;
        private readonly LiquidityGuardService _liq;
        private readonly OrderCleanerService _cleaner;
        private readonly PredictiveEngineV4ConfirmationService _predict;
        private readonly PositionSupervisorService _supervisor;
        private readonly AiStopLossOptimizer _slOpt;
        private readonly AiRiskScalerV2 _riskScaler;
        private readonly AiSelfLearningService _learn;
        private readonly AiModelSnapshotService _snapshot;

        private readonly Dictionary<string, DateTime> _lastTrade = new();
        private readonly SymbolRegistryService _symbols;
        private readonly AiTimeframeSelectorService _tfSelector;
        private readonly EngineStateBuilder _engineStateBuilder;
        private readonly EngineStateSnapshotService _engineStateSnapshot;
        private readonly IBootGate _bootGate;
        private readonly IStrategyPreFilter _pre;
        private readonly MarketContextService _marketContext;
        private readonly SimulatedTradeService _sim;

        private DateTime _lastQuantTick = DateTime.UtcNow;


        // ===== Engine heartbeat / CPM =====
        private int _cycleCounter = 0;
        private DateTime _cycleWindowUtc = DateTime.UtcNow;

        // Optional: чтобы не писать слишком часто, но можно и без этого
        private DateTime _lastEngineTickUtc = DateTime.MinValue;
        private readonly AiMarketRegimeService _marketRegime;


        // ===============================
        // TRACKED SYMBOLS (positions-safe)
        // ===============================
        private readonly ConcurrentDictionary<string, byte> _tracked = new();
        private readonly ConcurrentDictionary<string, byte> _warm = new();

        // REST throttle for positions scan (avoid spam)
        private DateTime _lastPositionsScanUtc = DateTime.MinValue;
        private IReadOnlyList<string> _cachedPositionSymbols = Array.Empty<string>();

        // Safety: if symbol was tracked, keep it a bit even after close (prevent flapping)
        private readonly Dictionary<string, DateTime> _trackedUntilUtc = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan TrackedGrace = TimeSpan.FromMinutes(20);
        private volatile string _currentSymbol = "—";

        // ===============================
        // STRATEGY SIGNAL QUEUE (PRO)
        // ===============================
        private readonly Channel<TradeSignal> _signalChannel =
            Channel.CreateUnbounded<TradeSignal>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false
                });

        public int StartupSubscriptionCap { get; set; } = 8;
        public TradingWorker(
            ILogger<TradingWorker> logger,
            IOptions<TradingOptions> options,
            MarketDataService market,
            MarketDataFacade marketDataFacade,
            StrategyEngine strategy,
            RiskManager risk,
            OrderExecutor executor,
            BinanceClientFactory factory,
            LiquidityGuardService liq,
            OrderCleanerService cleaner,
            PredictiveEngineV4ConfirmationService predict,
            AiStopLossOptimizer slOpt,
            AiRiskScalerV2 riskScaler,
            PositionSupervisorService supervisor,
            AiSelfLearningService learn,
            AiModelSnapshotService snapshot,
            SymbolRegistryService symbols,
            AiTimeframeSelectorService tfSelector,
            EngineStateBuilder engineStateBuilder,
            EngineStateSnapshotService engineStateSnapshot,
            IBootGate bootGate,
            IStrategyPreFilter pre, MarketContextService marketContext, SimulatedTradeService sim, AiMarketRegimeService marketRegime)
        {
            _logger = logger;
            _options = options.Value;

            _market = market;
            _marketDataFacade = marketDataFacade;
            _strategy = strategy;
            _risk = risk;
            _executor = executor;
            _factory = factory;
            _liq = liq;
            _cleaner = cleaner;
            _predict = predict;
            _slOpt = slOpt;
            _riskScaler = riskScaler;
            _supervisor = supervisor;
            _learn = learn;
            _snapshot = snapshot;
            _symbols = symbols;
            _tfSelector = tfSelector;
            _engineStateBuilder = engineStateBuilder;
            _engineStateSnapshot = engineStateSnapshot;
            _bootGate = bootGate;
            _pre = pre;
            _marketContext = marketContext;

            learn.ForceSnapshot();
            _sim = sim;
            _marketRegime = marketRegime;
        }

        private int _lastCyclesPerMinute = 0;
        private DateTime _lastUniverseRefreshUtc = DateTime.MinValue;
        private static readonly TimeSpan UniverseRefreshPeriod = TimeSpan.FromSeconds(30);
        private readonly SemaphoreSlim _positionsScanLock = new(1, 1);

        private async Task RejectAsync(
       TradeSignal? signal,
       string symbol,
       KlineInterval tf,
       string stage,
       string reason,
       CancellationToken ct,
       int? sleepMs = null,
       string? extra = null)
        {
            _logger.LogWarning(
                "[PROC][{symbol}][{tf}] REJECT stage={stage} reason={reason}{extra}",
                symbol, tf, stage, reason,
                extra != null ? $" | {extra}" : string.Empty
            );

            if (signal != null)
            {
                // 1️⃣ ВСЕГДА пишем lifecycle (чистый факт)
                try
                {
                    await _sim.AppendLifecycleEventAsync(
                        signal,
                        stage: $"REJECT_{stage}",
                        reason: reason
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[SIM] AppendLifecycleEventAsync failed");
                }

                // 2️⃣ MISSED TRADE — ТОЛЬКО если это реально вход
                //    (а не prefilter / технический reject)
                bool isEntryRelated =
                    stage is "ENTRY" or "EXEC" or "RISK";

                if (isEntryRelated)
                {
                    try
                    {
                        await _sim.SimulateMissedTradeAsync(
                            signal,
                            $"{stage}:{reason}",
                            note: extra
                        );
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[SIM] SimulateMissedTradeAsync failed");
                    }
                }
            }

            if (sleepMs.HasValue && sleepMs.Value > 0)
                await Task.Delay(sleepMs.Value, ct);
        }


        private async Task WarmupHtfAsync(
           IReadOnlyList<string> symbols,
           CancellationToken ct)
        {
            foreach (var s in symbols)
            {
                await _marketDataFacade.GetKlinesAsync(s, KlineInterval.OneHour, 60, ct);
                await _marketDataFacade.GetKlinesAsync(s, KlineInterval.FourHour, 60, ct);
                await _marketDataFacade.GetKlinesAsync(s, KlineInterval.OneDay, 60, ct);
                await _marketDataFacade.GetKlinesAsync(s, KlineInterval.OneHour, 60, ct);

                _logger.LogInformation("[MD][HTF] warmup {symbol} 1H/4H/1D/1H", s);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            _logger.LogWarning("[WORKER] Waiting BootGate ...");
            await _bootGate.WaitReadyAsync(ct);
            _logger.LogWarning("[WORKER] BootGate READY");

            // 1) LOAD SYMBOLS (universe)
            if (DateTime.UtcNow - _lastUniverseRefreshUtc > UniverseRefreshPeriod)
            {
                await _symbols.LoadAsync(ct);
                _lastUniverseRefreshUtc = DateTime.UtcNow;
            }

            // 2) APPLY UNIVERSE → WS subscriptions (1m/5m/15m)
            _marketDataFacade.ApplyUniverse(_symbols.ActiveSymbols);

            _symbols.UniverseChanged += syms =>
            {
                _marketDataFacade.ApplyUniverse(syms);
            };

            // 3) RESTORE SNAPSHOT (authoritative if exists)
            try
            {
                await _marketDataFacade.RestoreSnapshotStateAsync(ct);
                // ❗ НЕ ВЫЗЫВАТЬ MarkSnapshotReady() вручную
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WORKER] Market snapshot restore failed");
            }

            // 4) BIND STRATEGY AFTER MARKETDATA READY
            _strategy.BindReactive(_marketDataFacade);


            // 🔥 STRATEGY → WORKER(PUSH - ONLY)
_strategy.OnSignalGenerated += signal =>
{
    if (signal == null) return;
    _signalChannel.Writer.TryWrite(signal);
};

            // 5) TRACK SYMBOLS (runtime set)
            var startupCap = _options.StartupSubscriptionCap > 0
                ? _options.StartupSubscriptionCap
                : 8;

            foreach (var s in _symbols.ActiveSymbols.Take(startupCap))
                TrackSymbol(s, keepAlive: true);

            // 6) 🔥 HTF WARMUP (CRITICAL)
            await WarmupHtfAsync(_symbols.ActiveSymbols, ct);

            // 7) WARMUP LTF (1m/5m buffers)
            await WarmupMarketDataForTrackedAsync(ct);

            // 8) ENABLE HEDGE
            await EnableHedgeMode();

            // 9) LOAD AI SNAPSHOT
            try
            {
                var snap = await _snapshot.LoadLatestAsync(ct);
                if (snap != null)
                    _learn.ImportState(snap);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WORKER] AI snapshot import failed");
            }

            // =======================================================
            // 🔒 INIT BASE DEPOSIT (ONCE, HARD ANCHOR)
            // =======================================================
            _engineStateSnapshot.EnsureDepositInitialized(_options.Deposit);


            _logger.LogWarning("TradingWorker QUANT-REALTIME STARTED");

            while (!ct.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;

                // ===== ENGINE HEARTBEAT =====
                _lastEngineTickUtc = now;
                _cycleCounter++;

                if ((now - _cycleWindowUtc).TotalSeconds >= 60)
                {
                    _lastCyclesPerMinute = _cycleCounter;
                    _cycleCounter = 0;
                    _cycleWindowUtc = now;
                }

                IReadOnlyList<string> trackedSymbols;

                try
                {
                    await RunQuantRealtimeTick(ct);
                    if (DateTime.UtcNow - _lastUniverseRefreshUtc > UniverseRefreshPeriod)
                    {
                        await _symbols.LoadAsync(ct);
                        _lastUniverseRefreshUtc = DateTime.UtcNow;
                    }
                    trackedSymbols = await BuildTrackedSymbolsAsync(ct);
                    await WarmupMarketDataForTrackedAsync(ct);
                }
                catch
                {
                    await Task.Delay(200, ct);
                    continue;
                }

                // ===========================================
                // STRATEGY SIGNAL CONSUMER (PRO)
                // ===========================================
                while (_signalChannel.Reader.TryRead(out var signal))
                {
                    try
                    {
                        await HandleStrategySignalAsync(signal, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "[WORKER][SIGNAL] fatal handling {symbol}",
                            signal.Symbol);
                    }
                }

                // ===== ROTATION: ПО КАЖДОМУ СИМВОЛУ =====
                foreach (var symbol in trackedSymbols)
                {
                    _currentSymbol = symbol;

                    var selectedTf = await ResolveTimeframeSafeAsync(symbol, ct);  // можно для аналитики/логов
                    var tradeTf = KlineInterval.FiveMinutes; // жестко
                                                             // либо маппинг из _options.TimeframeMinutes -> KlineInterval
                    var ctx = await _marketContext.GetContextAsync(symbol, ct);

                    var state = _engineStateBuilder.Build(
                        symbol: symbol,
                        timeframe: _options.TimeframeMinutes.ToString()
                    );

                    state.LastEngineTick = DateTime.UtcNow;
                    state.LastUpdate = state.LastEngineTick;
                    state.CyclesPerMinute = _lastCyclesPerMinute;
                    state.UniverseSize = _symbols.ActiveSymbols.Count;
                    state.TrackedSymbols = trackedSymbols.Count;
                    state.OpenPositions = _supervisor.GetOpenPositionsCount();


                    _engineStateSnapshot.Save(state);

                    if (ctx.Allows(SignalSide.Buy))
                        await ProcessSymbolWithUniverseSide(symbol, tradeTf, SignalSide.Buy, ct);

                    if (ctx.Allows(SignalSide.Sell))
                        await ProcessSymbolWithUniverseSide(symbol, tradeTf, SignalSide.Sell, ct);

                    ConsoleSymbolTableFormatter.UpdateTf(symbol, tradeTf, $"TF={tradeTf} (sel={selectedTf})", "...");


                    await _supervisor.SuperviseAsync(symbol, null, ct);

                    // КОРОТКАЯ ПАУЗА ДЛЯ UI-КАРУСЕЛИ
                    await Task.Delay(25, ct);
                }

                await PeriodicSnapshot(ct);
                await Task.Delay(80, ct);
            }
        }

        private async Task HandleStrategySignalAsync(
    TradeSignal signal,
    CancellationToken ct)
        {
            var symbol = signal.Symbol.Trim().ToUpperInvariant();
            var tf = Enum.Parse<KlineInterval>(signal.Timeframe);

            ConsoleSymbolTableFormatter.UpdateTf(
                symbol, tf, "▶ STRAT", signal.Side.ToString());

            // =====================================================
            // 1) CONTEXT / UNIVERSE SIDE CHECK
            // =====================================================
            var ctx = await _marketContext.GetContextAsync(symbol, ct);
            if (!ctx.Allows(signal.Side))
            {
                await RejectAsync(
                    signal, symbol, tf,
                    "UNIVERSE_SIDE",
                    "SIDE_NOT_ALLOWED",
                    ct);
                return;
            }

            // =====================================================
            // 2) COOLDOWN
            // =====================================================
            if (InCooldown(symbol))
            {
                await RejectAsync(
                    signal, symbol, tf,
                    "COOLDOWN",
                    "COOLDOWN_ACTIVE",
                    ct);
                return;
            }

            // =====================================================
            // 3) AI CONFIRMATION
            // =====================================================
            AiDecision ai;
            try
            {
                var klines = await _marketDataFacade
                    .GetKlinesAsync(symbol, tf, 200, ct)
                    .ConfigureAwait(false);

                ai = _predict.Decide(symbol, tf, klines, signal);
            }
            catch (Exception ex)
            {
                await RejectAsync(
                    signal, symbol, tf,
                    "AI",
                    "AI_ERROR",
                    ct,
                    extra: ex.Message);
                return;
            }

            if (!ai.Allow)
            {
                await RejectAsync(
                    signal, symbol, tf,
                    "AI",
                    "AI_BLOCK",
                    ct,
                    extra: ai.Reason);
                return;
            }

            // =====================================================
            // 4) RISK SCALING
            // =====================================================
            var riskMult = _riskScaler.Scale(ai.Grade);
            if (riskMult <= 0)
            {
                await RejectAsync(
                    signal, symbol, tf,
                    "RISK",
                    "RISK_MULT_ZERO",
                    ct);
                return;
            }

            // =====================================================
            // 5) LIQUIDITY GUARD (FINAL)
            // =====================================================
            LiquidityGuardResult liq;
            try
            {
                var klines = await _marketDataFacade
                    .GetKlinesAsync(symbol, tf, 120, ct)
                    .ConfigureAwait(false);

                liq = _liq.Analyze(
                    symbol, tf, klines,
                    signal.Side,
                    signal.IsSuperSignal);
            }
            catch (Exception ex)
            {
                await RejectAsync(
                    signal, symbol, tf,
                    "LIQUIDITY",
                    "LIQ_SERVICE_ERROR",
                    ct,
                    extra: ex.Message);
                return;
            }

            if (liq.Block)
            {
                await RejectAsync(
                    signal, symbol, tf,
                    "LIQUIDITY",
                    $"LIQUIDITY_{liq.Reason}",
                    ct);
                return;
            }

            // =====================================================
            // 6) QTY
            // =====================================================
            var qty = await _risk.CalculateSafeQty(
                signal,
                symbol,
                signal.EntryPrice,
                signal.StopLoss,
                riskMult,
                signal.SafetyRiskMultiplier,
                signal.Leverage ?? 1m,
                signal.Side,
                signal.TakeProfits,
                ct).ConfigureAwait(false);

            if (qty <= 0)
            {
                await RejectAsync(
                    signal, symbol, tf,
                    "RISK",
                    "NO_BALANCE_OR_MIN_NOTIONAL",
                    ct);
                return;
            }

            // =====================================================
            // 7) SL / TP OPTIMIZATION
            // =====================================================
            try
            {
                var klines = await _marketDataFacade
                    .GetKlinesAsync(symbol, tf, 120, ct)
                    .ConfigureAwait(false);

                signal.StopLoss =
                    _slOpt.OptimizeSlAndTp(symbol, klines, signal, ai);

                await _cleaner
                    .CleanupOutdatedOrdersAsync(symbol, signal, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await RejectAsync(
                    signal, symbol, tf,
                    "PROTECTION",
                    "PROTECTION_ERROR",
                    ct,
                    extra: ex.Message);
                return;
            }

            // =====================================================
            // 8) EXECUTION
            // =====================================================
            var result = await _executor
                .ExecuteAsync(signal, qty, ct)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                await RejectAsync(
                    signal, symbol, tf,
                    "EXEC",
                    "EXECUTION_FAILED",
                    ct,
                    extra: result.Error);
                return;
            }

            // =====================================================
            // 9) SUCCESS
            // =====================================================
            MarkTrade(symbol);
            TrackSymbol(symbol, keepAlive: true);

            await _supervisor
                .SuperviseAsync(symbol, signal, ct)
                .ConfigureAwait(false);

            ConsoleSymbolTableFormatter.UpdateTf(
                symbol, tf,
                "🟩 OK",
                $"{signal.Side} qty={qty:F4}");
        }


        // ===========================================
        // TRACKED SYMBOLS CORE
        // ===========================================
        private void TrackSymbol(string symbol, bool keepAlive)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return;

            symbol = symbol.Trim().ToUpperInvariant();

            _tracked.TryAdd(symbol, 0);

            if (keepAlive)
                _trackedUntilUtc[symbol] = DateTime.UtcNow.Add(TrackedGrace);
        }

        private async Task<IReadOnlyList<string>> BuildTrackedSymbolsAsync(CancellationToken ct)
        {
            var pinnedSet = new HashSet<string>(
                _symbols.PinnedSymbols ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            // 1) universe — всегда отслеживаем
            foreach (var s in _symbols.ActiveSymbols)
                TrackSymbol(s, keepAlive: false);

            // 2) позиции — всегда keepAlive
            var posSymbols = await GetPositionSymbolsThrottledAsync(ct);
            foreach (var s in posSymbols)
                TrackSymbol(s, keepAlive: true);

            var now = DateTime.UtcNow;

            var posSet = new HashSet<string>(posSymbols, StringComparer.OrdinalIgnoreCase);
            var uniSet = new HashSet<string>(_symbols.ActiveSymbols, StringComparer.OrdinalIgnoreCase);

            foreach (var sym in _tracked.Keys.ToArray())
            {
                if (uniSet.Contains(sym))
                    continue;

                if (posSet.Contains(sym))
                    continue;

                if (pinnedSet.Contains(sym))
                    continue;

                if (_trackedUntilUtc.TryGetValue(sym, out var until) && until > now)
                    continue;

                // DROP
                _tracked.TryRemove(sym, out _);
                _trackedUntilUtc.Remove(sym);

                _logger.LogInformation(
                    "[TRACKED] drop {sym} (not in universe, no position, not pinned, grace expired)",
                    sym);
            }

            var list = _tracked.Keys
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _logger.LogInformation(
                "[TRACKED] total={tot} universe={uni} positions={pos}",
                list.Count,
                _symbols.ActiveSymbols.Count,
                posSymbols.Count);

            return list;
        }

        private async Task<IReadOnlyList<string>> GetPositionSymbolsThrottledAsync(CancellationToken ct)
        {
            if ((DateTime.UtcNow - _lastPositionsScanUtc) < TimeSpan.FromSeconds(30))
                return _cachedPositionSymbols;

            await _positionsScanLock.WaitAsync(ct);
            try
            {
                if ((DateTime.UtcNow - _lastPositionsScanUtc) < TimeSpan.FromSeconds(30))
                    return _cachedPositionSymbols;

                _lastPositionsScanUtc = DateTime.UtcNow;

                using var client = _factory.CreateRestClient();

                var res = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                if (!res.Success || res.Data == null)
                    return _cachedPositionSymbols;

                var symbols = res.Data
                    .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Symbol) && p.Quantity != 0m)
                    .Select(p => p.Symbol.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                _cachedPositionSymbols = symbols;
                return symbols;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[TRACKED] position scan failed");
                return _cachedPositionSymbols;
            }
            finally
            {
                _positionsScanLock.Release();
            }
        }

        private async Task WarmupMarketDataForTrackedAsync(CancellationToken ct)
        {
            // Warm only NEW symbols
            // =====================================================
            // WARMUP BATCH LIMIT (ANTI-STORM)
            // =====================================================
            var toWarm = _tracked.Keys
            .Except(_warm.Keys, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

            if (toWarm.Count == 0)
                return;

            foreach (var s in toWarm)
            {
                try
                {
                    // WS warmup (safe, sequential)
                    await _marketDataFacade.GetKlinesAsync(s, KlineInterval.OneMinute, 20, ct);
                    await _marketDataFacade.GetKlinesAsync(s, KlineInterval.FiveMinutes, 20, ct);

                    _warm.TryAdd(s, 0);
                    _logger.LogInformation("[BOOT][MD] warmup ok {sym}", s);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[BOOT][MD] warmup failed {sym}", s);
                    // do not add to _warm -> will retry later
                }
            }
        }

        // ===========================================
        // Existing logic (kept)
        // ===========================================

        private async Task<KlineInterval> ResolveTimeframeSafeAsync(string symbol, CancellationToken ct)
        {
            var fallback = KlineInterval.FiveMinutes;

            try
            {
                var m1Task = _market.GetMarketSnapshot(symbol, KlineInterval.OneMinute, ct);
                var m5Task = _market.GetMarketSnapshot(symbol, KlineInterval.FiveMinutes, ct);

                var m1 = await m1Task;
                var m5 = await m5Task;

                if (m1 != null && m5 != null)
                {
                    var d = _tfSelector.SelectTF(m1, m5);
                    return d switch
                    {
                        DominantTF.OneMinute => KlineInterval.OneMinute,
                        DominantTF.FiveMinutes => KlineInterval.FiveMinutes,
                        DominantTF.Both => KlineInterval.FiveMinutes,
                        _ => fallback
                    };
                }
            }
            catch { }

            return fallback;
        }

        private async Task ProcessSymbolWithUniverseSide(string symbol, KlineInterval tf, SignalSide desiredSide, CancellationToken ct)
        {
            symbol = symbol.Trim().ToUpperInvariant();

            bool allowed =
                desiredSide == SignalSide.Buy
                    ? _symbols.ActiveLongSymbols.Contains(symbol, StringComparer.OrdinalIgnoreCase)
                    : _symbols.ActiveShortSymbols.Contains(symbol, StringComparer.OrdinalIgnoreCase);

            if (!allowed)
            {
                await RejectAsync(
                    signal: null,
                    symbol: symbol,
                    tf: tf,
                    stage: "UNIVERSE_SIDE",
                    reason: desiredSide == SignalSide.Buy ? "NOT_IN_ACTIVE_LONG" : "NOT_IN_ACTIVE_SHORT",
                    ct: ct);

                return;
            }

            await ProcessSymbol(symbol, tf, desiredSide, ct);
        }


        //    private async Task ProcessSymbol(
        //  string symbol,
        //  KlineInterval tf,
        //  SignalSide desiredSide,
        //  CancellationToken ct)
        //    {
        //        ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "⏳ RUN", desiredSide.ToString());

        //        // =====================================================
        //        // 0) PREFILTER
        //        // =====================================================
        //        PreFilterResult pre;
        //        try
        //        {
        //            pre = await _pre.EvaluateAsync(symbol, tf, ct).ConfigureAwait(false);
        //        }
        //        catch (OperationCanceledException) { throw; }
        //        catch (Exception ex)
        //        {
        //            _logger.LogWarning(ex, "[PROC][{symbol}][{tf}] PREFILTER threw", symbol, tf);
        //            await RejectAsync(null, symbol, tf, "PREFILTER", "PREFILTER_ERROR", ct, extra: ex.Message)
        //                .ConfigureAwait(false);
        //            return;
        //        }

        //        if (!pre.Allow)
        //        {
        //            await RejectAsync(
        //                signal: null,
        //                symbol: symbol,
        //                tf: tf,
        //                stage: "PREFILTER",
        //                reason: pre.Reason ?? "NOT_ALLOWED",
        //                ct: ct,
        //                sleepMs: pre.SleepMs
        //            ).ConfigureAwait(false);
        //            return;
        //        }

        //        // =====================================================
        //        // 1) LOAD MARKET DATA
        //        // =====================================================
        //        IReadOnlyList<BinanceFuturesUsdtKline> klines;
        //        try
        //        {
        //            klines = await _marketDataFacade.GetKlinesAsync(symbol, tf, 200, ct).ConfigureAwait(false);
        //        }
        //        catch (OperationCanceledException) { throw; }
        //        catch (Exception ex)
        //        {
        //            _logger.LogWarning(ex, "[PROC][{symbol}][{tf}] MARKET_DATA fetch failed", symbol, tf);
        //            await RejectAsync(null, symbol, tf, "MARKET_DATA", "FETCH_FAILED", ct, extra: ex.Message)
        //                .ConfigureAwait(false);
        //            return;
        //        }

        //        var cnt = klines?.Count ?? 0;
        //        if (cnt < 60)
        //        {
        //            await RejectAsync(null, symbol, tf, "MARKET_DATA", "INSUFFICIENT_KLINES", ct, extra: $"cnt={cnt}")
        //                .ConfigureAwait(false);
        //            return;
        //        }

        //        // =====================================================
        //        // 2) STRATEGY SIGNAL
        //        // =====================================================
        //        TradeSignal? signal;
        //        try
        //        {
        //            signal = _strategy.GenerateSignal(symbol, tf, klines);
        //        }
        //        catch (OperationCanceledException) { throw; }
        //        catch (Exception ex)
        //        {
        //            _logger.LogError(ex, "[PROC][{symbol}][{tf}] STRATEGY threw", symbol, tf);
        //            await RejectAsync(null, symbol, tf, "STRATEGY", "STRATEGY_EXCEPTION", ct, extra: ex.Message)
        //                .ConfigureAwait(false);
        //            return;
        //        }

        //        if (signal == null)
        //        {
        //            // ✅ ВОТ ЭТОГО ТЕБЕ НЕ ХВАТАЛО: фиксируем “BLOCK” даже без сигнала
        //            await RejectAsync(null, symbol, tf, "STRATEGY", "NO_SIGNAL", ct)
        //                .ConfigureAwait(false);
        //            return;
        //        }
        //        if (signal.Side != desiredSide)
        //        {
        //            var confidence = signal.Confidence ?? 0m;

        //            //Если   мягче:
        //            //bool allowCounterTrend =
        //            //confidence >= 0.60m &&
        //            //!_marketRegime.IsStrongTrend(symbol) && !_liq.IsExtreme();


        //            //bool allowCounterTrend =
        //            //confidence >= 0.60m &&
        //            //_marketRegime.IsRange(symbol) &&  !_liq.IsExtreme();

        //            bool allowCounterTrend =
        //confidence >= 0.60m &&
        //!_marketRegime.IsStrongTrend(symbol) &&
        //!_liq.IsExtreme();


        //            if (!allowCounterTrend)
        //            {
        //                await RejectAsync(
        //                    signal,
        //                    symbol,
        //                    tf,
        //                    "STRATEGY",
        //                    "SIDE_MISMATCH",
        //                    ct,
        //                    extra: $"conf={confidence:F2}"
        //                ).ConfigureAwait(false);
        //                return;
        //            }

        //            // ✅ ЯВНО фиксируем override
        //            _learn.RecordMarketStateTriggered(
        //                reason: "COUNTER_TREND_OVERRIDE",
        //                symbol: symbol,
        //                timeframe: tf.ToString(),
        //                regime: MarketRegime.Unknown,
        //                slope: 0m,
        //                volatility: 0m,
        //                atr: signal.Atr ?? 0m,
        //                confidence: confidence
        //            );

        //            _logger.LogWarning(
        //                "[PROC][{symbol}][{tf}] COUNTER-TREND OVERRIDE allowed conf={conf}",
        //                symbol, tf, confidence);
        //        }

        //        // ✅ микро-телеметрия (норм)
        //        _learn.RecordMarketStateTriggered(
        //            reason: "MICRO_SIGNAL",
        //            symbol: symbol,
        //            timeframe: tf.ToString(),
        //            regime: MarketRegime.Unknown,
        //            slope: 0m,
        //            volatility: 0m,
        //            atr: signal.Atr ?? 0m,
        //            confidence: signal.Confidence ?? 0.3m
        //        );

        //        // =====================================================
        //        // 3) COOLDOWN
        //        // =====================================================
        //        if (InCooldown(symbol))
        //        {
        //            await RejectAsync(signal, symbol, tf, "COOLDOWN", "COOLDOWN_ACTIVE", ct)
        //                .ConfigureAwait(false);
        //            return;
        //        }

        //        // =====================================================
        //        // 4) AI CONFIRMATION
        //        // =====================================================
        //        AiDecision ai;
        //        try
        //        {
        //            ai = _predict.Decide(symbol, tf, klines, signal);
        //        }
        //        catch (OperationCanceledException) { throw; }
        //        catch (Exception ex)
        //        {
        //            _logger.LogError(ex, "[PROC][{symbol}][{tf}] AI Decide threw", symbol, tf);
        //            await RejectAsync(signal, symbol, tf, "AI", "AI_ERROR", ct, extra: ex.Message)
        //                .ConfigureAwait(false);
        //            return;
        //        }

        //        if (!ai.Allow)
        //        {
        //            _learn.RecordMarketStateTriggered(
        //                reason: "AI_PATTERN_BLOCK",
        //                symbol: symbol,
        //                timeframe: tf.ToString(),
        //                regime: MarketRegime.Unknown,
        //                slope: 0m,
        //                volatility: 0m,
        //                atr: signal.Atr ?? 0m,
        //                confidence: signal.Confidence ?? 0.25m
        //            );

        //            await RejectAsync(signal, symbol, tf, "AI", "AI_BLOCK", ct, extra: ai.Reason)
        //                .ConfigureAwait(false);
        //            return;
        //        }

        //        // =====================================================
        //        // 5) RISK SCALING
        //        // =====================================================
        //        var riskMult = _riskScaler.Scale(ai.Grade);
        //        if (riskMult <= 0)
        //        {
        //            await RejectAsync(signal, symbol, tf, "RISK", "RISK_MULT_ZERO", ct)
        //                .ConfigureAwait(false);
        //            return;
        //        }

        //        // =====================================================
        //        // 6) LIQUIDITY GUARD
        //        // =====================================================
        //        LiquidityGuardResult liq;
        //        try
        //        {
        //            liq = _liq.Analyze(symbol, tf, klines, signal.Side, signal.IsSuperSignal);
        //        }
        //        catch (OperationCanceledException) { throw; }
        //        catch (Exception ex)
        //        {
        //            _logger.LogError(ex, "[PROC][{symbol}][{tf}] LiquidityGuard Analyze threw", symbol, tf);
        //            // fail-safe: не блокируем из-за падения сервиса, но фиксируем
        //            await RejectAsync(signal, symbol, tf, "LIQUIDITY", "LIQ_SERVICE_ERROR", ct, extra: ex.Message)
        //                .ConfigureAwait(false);
        //            return;
        //        }

        //        if (liq.Block)
        //        {
        //            _learn.RecordMarketStateTriggered(
        //                reason: "LIQUIDITY_DANGER",
        //                symbol: symbol,
        //                timeframe: tf.ToString(),
        //                regime: MarketRegime.Unknown,
        //                slope: 0m,
        //                volatility: 0m,
        //                atr: signal.Atr ?? 0m,
        //                confidence: signal.Confidence ?? 0.25m
        //            );

        //            await RejectAsync(signal, symbol, tf, "LIQUIDITY", $"LIQUIDITY_{liq.Reason}", ct)
        //                .ConfigureAwait(false);
        //            return;
        //        }

        //        // =====================================================
        //        // 7) QTY / BALANCE / MIN NOTIONAL
        //        // =====================================================
        //        var qty = await _risk.CalculateSafeQty(
        //            signal,
        //            symbol,
        //            signal.EntryPrice,
        //            signal.StopLoss,
        //            riskMult,
        //            signal.SafetyRiskMultiplier,
        //            signal.Leverage ?? 1m,
        //            signal.Side,
        //            signal.TakeProfits,
        //            ct
        //        ).ConfigureAwait(false);

        //        if (qty <= 0)
        //        {
        //            await RejectAsync(signal, symbol, tf, "RISK", "NO_BALANCE_OR_MIN_NOTIONAL", ct)
        //                .ConfigureAwait(false);
        //            return;
        //        }

        //        // =====================================================
        //        // 8) SL / TP OPTIMIZATION
        //        // =====================================================
        //        try
        //        {
        //            signal.StopLoss = _slOpt.OptimizeSlAndTp(symbol, klines, signal, ai);
        //            await _cleaner.CleanupOutdatedOrdersAsync(symbol, signal, ct).ConfigureAwait(false);
        //        }
        //        catch (OperationCanceledException) { throw; }
        //        catch (Exception ex)
        //        {
        //            _logger.LogError(ex, "[PROC][{symbol}][{tf}] SL/TP optimize or cleanup failed", symbol, tf);
        //            await RejectAsync(signal, symbol, tf, "PROTECTION", "PROTECTION_ERROR", ct, extra: ex.Message)
        //                .ConfigureAwait(false);
        //            return;
        //        }

        //        // =====================================================
        //        // 9) EXECUTION
        //        // =====================================================
        //        var result = await _executor.ExecuteAsync(signal, qty, ct).ConfigureAwait(false);
        //        if (!result.Success)
        //        {
        //            await RejectAsync(
        //                signal,
        //                symbol,
        //                tf,
        //                "EXEC",
        //                "EXECUTION_FAILED",
        //                ct,
        //                extra: result.Error
        //            ).ConfigureAwait(false);
        //            return;
        //        }

        //        // lifecycle: EXEC_OK
        //        try
        //        {
        //            await _sim.AppendLifecycleEventAsync(signal, "EXEC_OK");
        //        }
        //        catch (Exception ex)
        //        {
        //            _logger.LogWarning(ex, "[SIM] lifecycle EXEC_OK failed");
        //        }

        //        // =====================================================
        //        // 10) SUCCESS PATH
        //        // =====================================================
        //        MarkTrade(symbol);
        //        TrackSymbol(symbol, keepAlive: true);

        //        // lifecycle: ENTRY_OK
        //        try
        //        {
        //            await _sim.AppendLifecycleEventAsync(signal, "ENTRY_OK");
        //        }
        //        catch (Exception ex)
        //        {
        //            _logger.LogWarning(ex, "[SIM] lifecycle ENTRY_OK failed");
        //        }

        //        await _supervisor.SuperviseAsync(symbol, signal, ct).ConfigureAwait(false);

        //        ConsoleSymbolTableFormatter.UpdateTf(
        //            symbol,
        //            tf,
        //            "🟩 OK",
        //            $"{signal.Side} qty={qty:F4}"
        //        );
        //    }

        private async Task ProcessSymbol(
    string symbol,
    KlineInterval tf,
    SignalSide desiredSide,
    CancellationToken ct)
        {
            // ❗❗❗
            // ENTRY PATH DISABLED
            // StrategyEngine is the ONLY signal source.
            // This method is kept ONLY to avoid breaking legacy call sites.
            // ❗❗❗

            _logger.LogDebug(
                "[PROC][{symbol}][{tf}] ProcessSymbol skipped — entry handled by StrategyEngine",
                symbol, tf);

            // НИЧЕГО НЕ ДЕЛАЕМ
            // ❌ никаких сигналов
            // ❌ никакого AI
            // ❌ никакого execution

            await Task.CompletedTask;
        }


        private bool InCooldown(string symbol)
        {
            if (_options.CooldownMinutes <= 0)
                return false;

            return _lastTrade.TryGetValue(symbol, out var last)
                   && DateTime.UtcNow - last < TimeSpan.FromMinutes(_options.CooldownMinutes);
        }

        private void MarkTrade(string symbol) =>
            _lastTrade[symbol] = DateTime.UtcNow;

        private async Task RunQuantRealtimeTick(CancellationToken ct)
        {
            if ((DateTime.UtcNow - _lastQuantTick).TotalSeconds < 60)
                return;

            _lastQuantTick = DateTime.UtcNow;

            try
            {
                await _snapshot.SaveSnapshotAsync(_learn.ExportState(), ct);
            }
            catch { }
        }
        private async Task PeriodicSnapshot(CancellationToken ct)
        {
            try
            {
                if (DateTime.UtcNow.Second < 2)
                    await _snapshot.SaveSnapshotAsync(_learn.ExportState(), ct);
            }
            catch { }
        }

        private async Task EnableHedgeMode()
        {
            using var client = _factory.CreateRestClient();

            var current = await client.UsdFuturesApi.Account.GetPositionModeAsync();
            if (current.Success && current.Data?.IsHedgeMode == true)
                return;

            await client.UsdFuturesApi.Account.ModifyPositionModeAsync(true);
        }
    }
}
