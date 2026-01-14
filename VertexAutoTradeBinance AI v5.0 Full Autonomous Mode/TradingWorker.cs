using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Models.HTF;
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
         

        private DateTime _lastQuantTick = DateTime.UtcNow;


        // ===== Engine heartbeat / CPM =====
        private int _cycleCounter = 0;
        private DateTime _cycleWindowUtc = DateTime.UtcNow;

        // Optional: чтобы не писать слишком часто, но можно и без этого
        private DateTime _lastEngineTickUtc = DateTime.MinValue;

        

        // ===============================
        // TRACKED SYMBOLS (positions-safe)
        // ===============================
        private readonly HashSet<string> _tracked = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _warm = new(StringComparer.OrdinalIgnoreCase);

        // REST throttle for positions scan (avoid spam)
        private DateTime _lastPositionsScanUtc = DateTime.MinValue;
        private IReadOnlyList<string> _cachedPositionSymbols = Array.Empty<string>();

        // Safety: if symbol was tracked, keep it a bit even after close (prevent flapping)
        private readonly Dictionary<string, DateTime> _trackedUntilUtc = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan TrackedGrace = TimeSpan.FromMinutes(20);
        private volatile string _currentSymbol = "—";


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
            IStrategyPreFilter pre, MarketContextService marketContext)
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
        }

        private int _lastCyclesPerMinute = 0;


        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            _logger.LogWarning("[WORKER] Waiting BootGate ...");
            await _bootGate.WaitReadyAsync(ct);
            _logger.LogWarning("[WORKER] BootGate READY");

            await _symbols.LoadAsync(ct);


            // APPLY UNIVERSE TO MARKET DATA
            _marketDataFacade.ApplyUniverse(_symbols.ActiveSymbols);

            // REACT TO FUTURE CHANGES
            _symbols.UniverseChanged += syms =>
            {
                _marketDataFacade.ApplyUniverse(syms);
            };

            try
            {
                await _marketDataFacade.RestoreSnapshotStateAsync(ct);
                _marketDataFacade.MarkSnapshotReady();
            }
            catch { }

            _strategy.BindReactive(_marketDataFacade);

            var startupCap = _options.StartupSubscriptionCap > 0
                ? _options.StartupSubscriptionCap
                : 8;

            foreach (var s in _symbols.ActiveSymbols.Take(startupCap))
                TrackSymbol(s, keepAlive: true);

            await WarmupMarketDataForTrackedAsync(ct);
            await EnableHedgeMode();

            try
            {
                var snap = await _snapshot.LoadLatestAsync(ct);
                if (snap != null)
                    _learn.ImportState(snap);
            }
            catch { }

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
                    await _symbols.LoadAsync(ct);
                    trackedSymbols = await BuildTrackedSymbolsAsync(ct);
                    await WarmupMarketDataForTrackedAsync(ct);
                }
                catch
                {
                    await Task.Delay(200, ct);
                    continue;
                }

                // ===== ROTATION: ПО КАЖДОМУ СИМВОЛУ =====
                foreach (var symbol in trackedSymbols)
                {
                    _currentSymbol = symbol;

                    var tf = await ResolveTimeframeSafeAsync(symbol, ct);
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
                        await ProcessSymbolWithUniverseSide(symbol, tf, SignalSide.Buy, ct);

                    if (ctx.Allows(SignalSide.Sell))
                        await ProcessSymbolWithUniverseSide(symbol, tf, SignalSide.Sell, ct);

                    await _supervisor.SuperviseAsync(symbol, null, ct);

                    // КОРОТКАЯ ПАУЗА ДЛЯ UI-КАРУСЕЛИ
                    await Task.Delay(25, ct);
                }

                await PeriodicSnapshot(ct);
                await Task.Delay(80, ct);
            }
        }

        // ===========================================
        // TRACKED SYMBOLS CORE
        // ===========================================

        private void TrackSymbol(string symbol, bool keepAlive)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return;

            symbol = symbol.Trim().ToUpperInvariant();

            // hard block forever
            if (string.Equals(symbol, "QQQQUSDT", StringComparison.OrdinalIgnoreCase))
                return;

            _tracked.Add(symbol);

            if (keepAlive)
                _trackedUntilUtc[symbol] = DateTime.UtcNow.Add(TrackedGrace);
        }

        private async Task<IReadOnlyList<string>> BuildTrackedSymbolsAsync(CancellationToken ct)
        {
            // Always include current universe + pinned (already included in ActiveSymbols in your registry)
            var subCap = _options.StartupSubscriptionCap > 0
    ? _options.StartupSubscriptionCap
    : 8;

            foreach (var s in _symbols.ActiveSymbols.Take(subCap))
                TrackSymbol(s, keepAlive: true);
          

            // Add all symbols that currently have open positions (safe throttle)
            var posSymbols = await GetPositionSymbolsThrottledAsync(ct);
            foreach (var s in posSymbols)
                TrackSymbol(s, keepAlive: true);

            // Cleanup: remove symbols that are no longer in universe and grace expired and no longer in positions
            var now = DateTime.UtcNow;
            var posSet = new HashSet<string>(posSymbols, StringComparer.OrdinalIgnoreCase);
            var uniSet = new HashSet<string>(_symbols.ActiveSymbols, StringComparer.OrdinalIgnoreCase);

            foreach (var sym in _tracked.ToArray())
            {
                if (uniSet.Contains(sym)) continue;
                if (posSet.Contains(sym)) continue;

                if (_trackedUntilUtc.TryGetValue(sym, out var until) && until > now)
                    continue;

                _tracked.Remove(sym);
                _trackedUntilUtc.Remove(sym);

                // We do NOT attempt "unsubscribe" here (MarketDataFacade can handle evictions via its own cache/TTL if any)
                _logger.LogInformation("[TRACKED] drop {sym} (no universe, no position, grace expired)", sym);
            }

            var list = _tracked
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // observability
            _logger.LogInformation(
                "[TRACKED] total={tot} universe={uni} positions={pos}",
                list.Count,
                _symbols.ActiveSymbols.Count,
                posSymbols.Count);

            return list;
        }

        private async Task<IReadOnlyList<string>> GetPositionSymbolsThrottledAsync(CancellationToken ct)
        {
            // REST scan at most once per 30 seconds (production-safe)
            if ((DateTime.UtcNow - _lastPositionsScanUtc) < TimeSpan.FromSeconds(30))
                return _cachedPositionSymbols;

            _lastPositionsScanUtc = DateTime.UtcNow;

            try
            {
                using var client = _factory.CreateRestClient();

                // One call, no symbol filter (Binance bug-safe)
                var res = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                if (!res.Success || res.Data == null)
                    return _cachedPositionSymbols;

                var symbols = res.Data
                    .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Symbol) && p.Quantity != 0m)
                    .Select(p => p.Symbol.Trim().ToUpperInvariant())
                   // .Where(s => !string.Equals(s, "AIAUSDT", StringComparison.OrdinalIgnoreCase))
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
        }

        private async Task WarmupMarketDataForTrackedAsync(CancellationToken ct)
        {
            // Warm only NEW symbols
            // =====================================================
            // WARMUP BATCH LIMIT (ANTI-STORM)
            // =====================================================
            var toWarm = _tracked
                .Except(_warm, StringComparer.OrdinalIgnoreCase)
                .Take(3) // 🔥 НЕ БОЛЕЕ 3 СИМВОЛОВ ЗА ПРОХОД
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

                    _warm.Add(s);
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

        private async Task ProcessSymbolWithUniverseSide(
            string symbol,
            KlineInterval tf,
            SignalSide desiredSide,
            CancellationToken ct)
        {
            // entries strictly gated by universe sides
            if (desiredSide == SignalSide.Buy && !_symbols.ActiveLongSymbols.Contains(symbol))
                return;

            if (desiredSide == SignalSide.Sell && !_symbols.ActiveShortSymbols.Contains(symbol))
                return;

            await ProcessSymbol(symbol, tf, desiredSide, ct);
        }

        private async Task ProcessSymbol(
            string symbol,
            KlineInterval tf,
            SignalSide desiredSide,
            CancellationToken ct)
        {
            ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "⏳ RUN", desiredSide.ToString());

            var pre = await _pre.EvaluateAsync(symbol, tf, ct);
            if (!pre.Allow)
            {
                if (pre.SleepMs.HasValue)
                    await Task.Delay(pre.SleepMs.Value, ct);
                return;
            }

            IReadOnlyList<BinanceFuturesUsdtKline>? klines;
            try
            {
                klines = await _marketDataFacade.GetKlinesAsync(symbol, tf, 200, ct);
            }
            catch
            {
                return;
            }

            if (klines == null || klines.Count < 60)
                return;

            TradeSignal? signal;
            try
            {
                signal = _strategy.GenerateSignal(symbol, tf, klines);
            }
            catch
            {
                return;
            }

            // side-strict: this pass serves ONLY desiredSide
            if (signal == null || signal.Side != desiredSide)
                return;

            if (InCooldown(symbol))
                return;

            var ai = _predict.Decide(symbol, tf, klines, signal);
            if (!ai.Allow)
                return;

            var riskMult = _riskScaler.Scale(ai.Grade);
            if (riskMult <= 0)
                return;

            var liq = _liq.Analyze(symbol, tf, klines, signal.Side, signal.IsSuperSignal);
            if (liq.Block)
                return;

            var qty = await _risk.CalculateSafeQty(
                signal,
                signal.Symbol,
                signal.EntryPrice,
                signal.StopLoss,
                riskMult,
                signal.SafetyRiskMultiplier,
                signal.Leverage ?? 1m,
                signal.Side,
                signal.TakeProfits,
                ct);

            if (qty <= 0)
                return;

            signal.StopLoss = _slOpt.OptimizeSlAndTp(symbol, klines, signal, ai);

            await _cleaner.CleanupOutdatedOrdersAsync(symbol, signal, ct);

            var result = await _executor.ExecuteAsync(signal, qty, ct);
            if (!result.Success)
                return;

            MarkTrade(symbol);

            // IMPORTANT: after entry -> ensure symbol stays tracked
            TrackSymbol(symbol, keepAlive: true);

            await _supervisor.SuperviseAsync(symbol, signal, ct);

            ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🟩 OK", $"{signal.Side} qty={qty:F4}");
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
