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
        private readonly ExecutedSignalService _executedSignals;
        private readonly TradingSessionGate _sessionGate;
        private readonly Dictionary<string, DateTime> _lastClose = new(StringComparer.OrdinalIgnoreCase);


        private static readonly KlineInterval[] TFS = {
            KlineInterval.OneMinute,
            KlineInterval.FiveMinutes,
            KlineInterval.FifteenMinutes,
            KlineInterval.OneHour,
            KlineInterval.OneDay
        };

        private readonly Dictionary<KlineInterval, TimeSpan> _min = new()
        {
            {KlineInterval.OneMinute, TimeSpan.FromSeconds(2)},
            {KlineInterval.FiveMinutes, TimeSpan.FromSeconds(25)},
            {KlineInterval.FifteenMinutes, TimeSpan.FromSeconds(60)},
            {KlineInterval.OneHour, TimeSpan.FromMinutes(5)},
            {KlineInterval.OneDay, TimeSpan.FromMinutes(30)}
        };

        private DateTime _lastQuantTick = DateTime.UtcNow;

        public TradingWorker(
            ILogger<TradingWorker> logger,
            IOptions<BinanceOptions> binance,
            IOptions<TradingOptions> options,
            MarketDataService market,
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
            AiTimeframeSelectorService tfSelector,
            EngineStateBuilder engineState,
            EngineStateSnapshotService engineStateSnapshot,
            ExecutedSignalService executedSignals,
            TradingSessionGate sessionGate)
        {
            _logger = logger;

            _binance = binance.Value;
            _options = options.Value;

            _market = market;
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
            _executedSignals = executedSignals;
            _sessionGate = sessionGate;
            learn.ForceSnapshot();
        }

        // ================================================================
        // MAIN LOOP v6 — QUANT REALTIME ENGINE
        // ================================================================
        // ================================================================
        // MAIN LOOP v6 — QUANT REALTIME ENGINE
        // ================================================================
        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            await _symbols.LoadAsync(ct);

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
                _sessionGate.LogStatusThrottled();

                foreach (var symbol in _symbols.ActiveSymbols)
                {
                    var m1 = await _market.GetMarketSnapshot(symbol, KlineInterval.OneMinute, ct);
                    var m5 = await _market.GetMarketSnapshot(symbol, KlineInterval.FiveMinutes, ct);

                    if (m1 == null || m5 == null)
                        continue;

                    var decision = _tfSelector.SelectTF(m1, m5);

                    KlineInterval? finalTf = decision switch
                    {
                        AiTimeframeSelectorService.DominantTF.OneMinute => KlineInterval.OneMinute,
                        AiTimeframeSelectorService.DominantTF.FiveMinutes => KlineInterval.FiveMinutes,
                        AiTimeframeSelectorService.DominantTF.Both => KlineInterval.FiveMinutes,
                        _ => null
                    };

                    if (finalTf == null)
                        continue;

                    // --- 1) Обработка сигнала
                    await ProcessSymbol(symbol, finalTf.Value, ct);

                    // --- 2) SUPERVISOR (ставит SL/TP на ВСЕ открытые позиции)
                    await _supervisor.SuperviseAsync(symbol, null, ct);

                    // --- 3) Engine state (UI)
                    var engineState = _engineState.Build(symbol, finalTf.Value.ToString());
                    _engineStateSnapshot.Save(engineState);

                    await Task.Delay(25, ct);
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
         

        /// <summary>
        /// Re-entry policy (не «слепой» повтор стороны):
        ///  1) Strategy каждый цикл заново считает side по текущим klines/trend.
        ///  2) Короткий rest после close на ЛЮБОЙ вход (остыть).
        ///  3) Длинный rest только на ТУ ЖЕ сторону что был last close.
        ///  4) Противоположная сторона — можно раньше, если анализ дал сигнал.
        ///  5) Короткий anti-dupe после open.
        /// </summary>
        private bool InCooldown(string symbol, SignalSide? proposedSide = null)
        {
            var now = DateTime.UtcNow;

            int anyMin = _options.PostCloseCooldownMinutes > 0
                ? _options.PostCloseCooldownMinutes
                : 45;
            int sameMin = _options.SameSideCooldownMinutes > 0
                ? _options.SameSideCooldownMinutes
                : 240;

            // --- post-close: side-aware ---
            if (proposedSide.HasValue)
            {
                if (_executedSignals.ShouldBlockReentry(
                        symbol, proposedSide.Value, anyMin, sameMin, out var why))
                {
                    _logger.LogInformation(
                        "[COOLDOWN][{symbol}] skip re-entry side={side}: {why}",
                        symbol, proposedSide.Value, why);
                    return true;
                }
            }
            else
            {
                // fallback без side — только короткий any-side rest
                if (_executedSignals.IsInPostCloseCooldown(symbol, anyMin))
                    return true;
            }

            // --- short cooldown after successful OPEN (anti duplicate orders) ---
            int openCd = _options.CooldownMinutes > 0
                ? _options.CooldownMinutes
                : Math.Max(1, _options.CooldownSeconds / 60);

            if (_lastTrade.TryGetValue(symbol, out var lastOpen) &&
                now - lastOpen < TimeSpan.FromMinutes(openCd))
            {
                return true;
            }

            return false;
        }

        private void MarkTrade(string symbol) =>
            _lastTrade[symbol] = DateTime.UtcNow;

        private void MarkClose(string symbol) =>
            _lastClose[symbol] = DateTime.UtcNow;

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

            // Cooldown AFTER signal: side уже от стратегии (текущий тренд), не от прошлой сделки
            if (InCooldown(symbol, signal.Side))
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🕒 CD", "Re-entry hold");
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

            // ------------------ 3b. POSITION GUARD (max 2 entries / symbol) ----------
            bool wantLong = signal.Side == SignalSide.Buy;
            var entryDecision = await _guard.EvaluateAsync(symbol, wantLong, signal.IsSuperSignal);
            if (entryDecision.Block)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🛡 GUARD", entryDecision.Reason);
                _logger.LogInformation("[GUARD][{symbol}] skip: {reason}", symbol, entryDecision.Reason);
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

            // Добор: чуть больше объём (×1.25 / ×1.40 super), но не бесконечные мелкие входы
            if (entryDecision.IsAdd && entryDecision.QtyMultiplier > 1m)
            {
                qty = qty * entryDecision.QtyMultiplier;
                _logger.LogInformation("[GUARD][{symbol}] ADD size boost ×{m} → qty={q}",
                    symbol, entryDecision.QtyMultiplier, qty);
            }

            // ------------------ 5. SL OPTIMIZATION -------------------
            signal.StopLoss = _slOpt.OptimizeSlAndTp(symbol, klines, signal, ai);

            // ------------------ 6. SESSION GATE (London + NY + early start) ----------
            // Вне сессий: анализ уже прошёл выше; новый вход запрещён. Supervisor снаружи цикла жив.
            if (!_sessionGate.IsTradingAllowed(out var sessionReason, out var activeSession))
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "👁 OBS", "Session off");
                _logger.LogInformation(
                    "[SESSION][{symbol}] signal ready but OBSERVE ONLY — {reason}",
                    symbol, sessionReason);
                return;
            }

            // ------------------ 7. CLEANUP OLD ORDERS -----------------
            await _cleaner.CleanupOutdatedOrdersAsync(symbol, signal, ct);

            // ------------------ 8. EXECUTE ORDER ----------------------
            _logger.LogInformation(
                "[SESSION][{symbol}] TRADING window active={sess} — executing",
                symbol, activeSession);
            var result = await _executor.ExecuteAsync(signal, qty, ct);
            if (!result.Success)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "❌ ORD", "ORDER ERROR");
                return;
            }

            // ------------------ 8. COOLDOWN + entry count -------------
            MarkTrade(symbol);
            _guard.RegisterSuccessfulEntry(symbol);

            // ------------------ 9. SUPERVISOR -------------------------
            await _supervisor.SuperviseAsync(symbol, signal, ct);

            // ------------------ 10. UI -------------------------------
            var tag = entryDecision.IsAdd ? "ADD" : "NEW";
            ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🟩 OK", $"{tag} qty={qty:F4}");
 
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
