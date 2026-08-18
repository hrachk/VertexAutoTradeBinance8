
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.Formatting;
using VertexAutoTradeBinance8.Strategy;
using static VertexAutoTradeBinance8.Services.AiTimeframeSelectorService;

namespace VertexAutoTradeBinance8
{
    public class TradingWorker : BackgroundService
    {
        private readonly ILogger<TradingWorker> _logger;

        private readonly BinanceOptions _binance;
        private readonly TradingOptions _options;

        private readonly MarketDataService _market;
        private readonly StrategyEngine _strategy;
        private readonly RiskManager _risk;
        private readonly OrderExecutor _executor;
        private readonly BinanceClientFactory _factory;
        private readonly LiquidityGuardService _liq;
        private readonly PositionGuardService _guard;
        private readonly PositionProtectorService _protector;
        private readonly OrderCleanerService _cleaner;
        private readonly PredictiveEngineV4ConfirmationService _predict;
        private readonly PositionSupervisorService _supervisor;

        private readonly AiStopLossOptimizer _slOpt;
        private readonly AiRiskScalerV2 _riskScaler;

        private readonly AiSelfLearningService _learn;
        private readonly AiModelSnapshotService _snapshot;

        private readonly Dictionary<string, DateTime> _lastTfRun = new();
        private readonly Dictionary<string, DateTime> _lastTrade = new();

        private readonly SymbolRegistryService _symbols;
        private readonly AiTimeframeSelectorService _tfSelector;
        private readonly EngineStateBuilder  _engineState;
        private readonly EngineStateSnapshotService  _engineStateSnapshot;

        private readonly MarketDataFacade _marketDataFacade;

        private DateTime _lastQuantTick = DateTime.UtcNow;

        public TradingWorker(
            ILogger<TradingWorker> logger,
            IOptions<BinanceOptions> binance,
            IOptions<TradingOptions> options,
            MarketDataService market,
            MarketDataFacade marketDataFacade,
            StrategyEngine strategy,
            RiskManager risk,
            OrderExecutor executor,
            BinanceClientFactory factory,
            LiquidityGuardService liq,
            PositionGuardService guard,
            PositionProtectorService protector,
            OrderCleanerService cleaner,
            PredictiveEngineV4ConfirmationService predict,
            AiStopLossOptimizer slOpt,
            AiRiskScalerV2 riskScaler,
            PositionSupervisorService supervisor,
            AiSelfLearningService learn,
            AiModelSnapshotService snapshot,
            SymbolRegistryService symbols,
            AiTimeframeSelectorService tfSelector, EngineStateBuilder engineState, EngineStateSnapshotService engineStateSnapshot)
        {
            _logger = logger;

            _binance = binance.Value;
            _options = options.Value;

            _market = market;
            _marketDataFacade = marketDataFacade;
            _strategy = strategy;
            _risk = risk;
            _executor = executor;
            _factory = factory;
            _liq = liq;
            _guard = guard;
            _protector = protector;
            _cleaner = cleaner;
            _predict = predict;
            _slOpt = slOpt;
            _riskScaler = riskScaler;
            _supervisor = supervisor;

            _learn = learn;
            _snapshot = snapshot;
            _symbols = symbols;
            _tfSelector = tfSelector;
            _engineState = engineState;
            _engineStateSnapshot = engineStateSnapshot;
            learn.ForceSnapshot();
        }

         // ================================================================
        // MAIN LOOP v6 — QUANT REALTIME ENGINE
        // ================================================================
        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            await _symbols.LoadAsync(ct);

            // 🔥 BOOTSTRAP PUSH → StrategyEngine
            _strategy.BindReactive(_marketDataFacade);

            _logger.LogWarning("TradingWorker v6 QUANT-REALTIME started");

            await EnableHedgeMode();

            // Загрузка AI-снапшота
            try
            {
                var state = await _snapshot.LoadLatestAsync(ct);
                if (state != null)
                    _learn.ImportState(state);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI snapshot load failed");
            }

            while (!ct.IsCancellationRequested)
            {
                await RunQuantRealtimeTick(ct);

                var active = _symbols.ActiveSymbols?.ToList() ?? new List<string>();
                if (active.Count == 0)
                {
                    _logger.LogWarning("[WORKER] ActiveSymbols is EMPTY — no analysis this tick");
                }

                var anySymbolProcessed = false;
                var skipNoSnapshot = 0;
                var skipTfNone = 0;

                foreach (var symbol in active)
                {
                    var m1 = await _market.GetMarketSnapshot(symbol, KlineInterval.OneMinute, ct);
                    var m5 = await _market.GetMarketSnapshot(symbol, KlineInterval.FiveMinutes, ct);

                    if (m1 == null || m5 == null)
                    {
                        skipNoSnapshot++;
                        continue;
                    }

                    var decision = _tfSelector.SelectTF(m1, m5);

                    KlineInterval? finalTf = decision switch
                    {
                        AiTimeframeSelectorService.DominantTF.OneMinute => KlineInterval.OneMinute,
                        AiTimeframeSelectorService.DominantTF.FiveMinutes => KlineInterval.FiveMinutes,
                        AiTimeframeSelectorService.DominantTF.Both => KlineInterval.FiveMinutes,
                        _ => null
                    };

                    if (finalTf == null)
                    {
                        skipTfNone++;
                        continue;
                    }

                    // --- 1) SIGNAL PIPELINE starts here (GenerateSignal inside ProcessSymbol)
                    await ProcessSymbol(symbol, finalTf.Value, ct);

                    // --- 2) SUPERVISOR
                    await _supervisor.SuperviseAsync(symbol, null, ct);

                    // --- 3) Engine state for UI
                    var engineState = _engineState.Build(symbol, finalTf.Value.ToString());
                    engineState.Status = "Running";
                    _engineStateSnapshot.Save(engineState);
                    anySymbolProcessed = true;

                    await Task.Delay(25, ct);
                }

                // One summary line per loop — so you see WHY signals are not generating
                if (!anySymbolProcessed)
                {
                    _logger.LogWarning(
                        "[WORKER] No ProcessSymbol this tick | active={Active} skipNoSnapshot(m1/m5)={NoSnap} skipTfNone={TfNone}. Signals start only after m1+m5 ready AND SelectTF≠None",
                        active.Count, skipNoSnapshot, skipTfNone);

                    _engineStateSnapshot.State.Status = "Running";
                    _engineStateSnapshot.State.LastUpdate = DateTime.UtcNow;
                    if (string.IsNullOrWhiteSpace(_engineStateSnapshot.State.Mode))
                        _engineStateSnapshot.State.Mode = "WaitingMarketData";
                    _engineStateSnapshot.PersistLiveState();
                }
                else
                {
                    _logger.LogInformation(
                        "[WORKER] Processed {Ok} symbols | skipped snapshot={NoSnap} tfNone={TfNone}",
                        active.Count - skipNoSnapshot - skipTfNone, skipNoSnapshot, skipTfNone);
                }

                await PeriodicSnapshot(ct);
                await Task.Delay(80, ct);
            }
        }


        // QUANT REALTIME TICK — каждые 60 секунд
        // ================================================================
        private async Task RunQuantRealtimeTick(CancellationToken ct)
        {
            if ((DateTime.UtcNow - _lastQuantTick).TotalSeconds < 60)
                return;

            _lastQuantTick = DateTime.UtcNow;

            try
            {
                _logger.LogInformation("QUANT-REALTIME update: AI-learning refresh");

                var state = _learn.ExportState();
                await _snapshot.SaveSnapshotAsync(state, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[QUANT] snapshot save failed");
            }
        }
         

        private bool InCooldown(string symbol)
        {
            var cd = _options.CooldownMinutes;
            if (cd <= 0) return false;

            if (!_lastTrade.TryGetValue(symbol, out var last))
                return false;

            return DateTime.UtcNow - last < TimeSpan.FromMinutes(cd);
        }

        private void MarkTrade(string symbol) =>
            _lastTrade[symbol] = DateTime.UtcNow;

        // ================================================================
        // MAIN PER-SYMBOL PROCESSOR v6
        // ================================================================
        private async Task ProcessSymbol(string symbol, KlineInterval tf, CancellationToken ct)
        {
            ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "⏳ RUN", "Получение");

            IReadOnlyList<BinanceFuturesUsdtKline>? klines;
            try
            {
                klines = await _market.GetKlines(symbol, tf, 200);
            }
            catch
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "❌ ERR", "GetKlines");
                return;
            }

            if (klines == null || klines.Count < 60)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "⏸ HOLD", "Мало свечей");
                return;
            }

            // ------------------ 1. STRATEGY ------------------------
            TradeSignal? signal;
            try
            {
                signal = _strategy.GenerateSignal(symbol, tf, klines);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[STRATEGY ERROR] {symbol} {tf}", symbol, tf);
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "❌ ERR", ex.GetType().Name);
                return;
            }

            if (signal == null)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🔴 NO", "Нет сигнала");
                return;
            }

            if (InCooldown(symbol))
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🕒 CD", "Cooldown");
                return;
            }

            // ------------------ 2. AI CONFIRM ------------------------
            var ai = _predict.Decide(symbol, tf, klines, signal);
            if (!ai.Allow)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🟡 AI-BLK", ai.Reason);
                return;
            }

            decimal riskMult = _riskScaler.Scale(ai.Grade);
            if (riskMult <= 0)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🟡 RISK", "AI block");
                return;
            }

            // ------------------ 3. Liquidity Guard -------------------
            var liq = _liq.Analyze(symbol, tf, klines, signal.Side, signal.IsSuperSignal);
            if (liq.Block)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🟡 LIQ", liq.Reason.ToString());
                return;
            }

            // ------------------ 4. RISK-MANAGER v6 --------------------
            decimal safety = signal?.SafetyRiskMultiplier ?? 1.0m;


            decimal qty = await _risk.CalculateSafeQty(signal,
                signal.Symbol,
                signal.EntryPrice,
                signal.StopLoss,
                riskMult,
                safety,
                signal.Leverage ?? 1m,
                signal.Side,
                signal.TakeProfits,
                ct);


            if (qty <= 0)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "❌ QTY=0", "SIZE ZERO");
                return;
            }

            // ------------------ 5. SL OPTIMIZATION -------------------
            signal.StopLoss = _slOpt.OptimizeSlAndTp(symbol, klines, signal, ai);

            // ------------------ 6. CLEANUP OLD ORDERS -----------------
            await _cleaner.CleanupOutdatedOrdersAsync(symbol, signal, ct);

            // ------------------ 7. EXECUTE ORDER ----------------------
            var result = await _executor.ExecuteAsync(signal, qty, ct);
            if (!result.Success)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "❌ ORD", "ORDER ERROR");
                return;
            }

            // ------------------ 8. COOLDOWN ---------------------------
            MarkTrade(symbol);

            // ------------------ 9. SUPERVISOR -------------------------
            await _supervisor.SuperviseAsync(symbol, signal, ct);

            // ------------------ 10. UI -------------------------------
            ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🟩 OK", $"qty={qty:F4}");
 
        }
       
        // ================================================================
        // HEDGE MODE (once)
        // ================================================================
        private async Task EnableHedgeMode()
        {
            using var client = _factory.CreateRestClient();

            var current = await client.UsdFuturesApi.Account.GetPositionModeAsync();
            if (current.Success && current.Data?.IsHedgeMode == true)
            {
                _logger.LogInformation("Hedge Mode already active.");
                return;
            }

            var set = await client.UsdFuturesApi.Account.ModifyPositionModeAsync(true);
            _logger.LogInformation("HedgeMode Set: {res}", set.Success);
        }

        // ================================================================
        // SNAPSHOT SYNC
        // ================================================================
        private async Task PeriodicSnapshot(CancellationToken ct)
        {
            try
            {
                if (DateTime.UtcNow.Second < 2)
                {
                    var state = _learn.ExportState();
                    await _snapshot.SaveSnapshotAsync(state, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Snapshot save error");
            }
        }
    }
}
