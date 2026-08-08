
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services.Levels;
using VertexAutoTradeBinance8.Services.Storage;
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
        private readonly TradeLevelEngine _levels;
        private readonly LevelOptions _levelOpt;
        private readonly TradeDecisionJournal _journal;
        private readonly AiRiskScalerV2 _riskScaler;

        private readonly AiSelfLearningService _learn;
        private readonly AiModelSnapshotService _snapshot;

        private readonly Dictionary<string, DateTime> _lastTfRun = new();
        private readonly Dictionary<string, DateTime> _lastTrade = new();

        private readonly SymbolRegistryService _symbols;
        private readonly AiTimeframeSelectorService _tfSelector;
        private readonly EngineStateBuilder  _engineState;
        private readonly EngineStateSnapshotService  _engineStateSnapshot;

 

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
            TradeLevelEngine levels,
            LevelOptions levelOpt,
            TradeDecisionJournal journal,
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
            _levels = levels;
            _levelOpt = levelOpt;
            _journal = journal;
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

            // Черновик решения заполняется по мере прохождения воронки и
            // записывается ровно один раз — на том шаге, где сигнал остановился.
            var d = new TradeDecision
            {
                Symbol = symbol,
                Timeframe = tf.ToString()
            };

            IReadOnlyList<BinanceFuturesUsdtKline>? klines;
            try
            {
                klines = await _market.GetKlines(symbol, tf, 200);
            }
            catch (Exception ex)
            {
                Reject(d, DecisionStage.Data, "KlinesError",
                    $"Свечи не загрузились: {ex.GetType().Name}.");
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "❌ ERR", "GetKlines");
                return;
            }

            if (klines == null || klines.Count < 60)
            {
                Reject(d, DecisionStage.Data, "NotEnoughBars",
                    $"Получено {klines?.Count ?? 0} свечей, для анализа нужно минимум 60.");
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
                Reject(d, DecisionStage.Strategy, "StrategyError",
                    $"Стратегия упала с {ex.GetType().Name}.");
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "❌ ERR", ex.GetType().Name);
                return;
            }

            if (signal == null)
            {
                // Отсутствие сетапа — самый частый исход, и он не интересен
                // сам по себе. В журнал не пишем, иначе полезные отказы
                // утонут в шуме: это не решение, а его отсутствие.
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🔴 NO", "Нет сигнала");
                return;
            }

            d.Side = signal.Side;
            d.EntryPrice = signal.EntryPrice;
            d.Atr = signal.Atr;

            if (InCooldown(symbol))
            {
                Reject(d, DecisionStage.Cooldown, "Cooldown",
                    $"Символ на паузе после предыдущей сделки ({_options.CooldownMinutes} мин).");
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🕒 CD", "Cooldown");
                return;
            }

            // ------------------ 2. AI CONFIRM ------------------------
            var ai = _predict.Decide(symbol, tf, klines, signal);

            if (!ai.Allow)
            {
                Reject(d, DecisionStage.AiGate, "AiBlocked",
                    $"AI не подтвердил сетап: {ai.Reason}. Тренд {ai.Trend}, ATR {ai.AtrPct:P2}.");
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🟡 AI-BLK", ai.Reason);
                return;
            }

            decimal riskMult = _riskScaler.Scale(ai.Grade);

            if (riskMult <= 0)
            {
                Reject(d, DecisionStage.RiskGate, "RiskZero",
                    $"Оценка AI ({ai.Grade}) обнулила риск-множитель — качество сетапа ниже порога.");
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🟡 RISK", "AI block");
                return;
            }

            // ------------------ 3. Liquidity Guard -------------------
            var liq = _liq.Analyze(symbol, tf, klines, signal.Side, signal.IsSuperSignal);

            if (liq.Block)
            {
                Reject(d, DecisionStage.Liquidity, liq.Reason.ToString(),
                    $"Страж ликвидности остановил вход: {liq.Reason}. " +
                    "Вход в такой стакан даёт проскальзывание, которое съедает расчётный риск.");
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🟡 LIQ", liq.Reason.ToString());
                return;
            }

            // ------------------ 4. УРОВНИ SL / TP ---------------------
            // Строятся ДО расчёта объёма: RiskManager считает количество от
            // дистанции до стопа, поэтому стоп обязан быть окончательным.
            if (_levelOpt.Enabled)
            {
                var plan = _levels.Build(signal, klines);

                d.StopMethod = plan.StopMethod;
                d.StructureLevel = plan.StructureLevel;
                d.Atr = plan.Atr > 0 ? plan.Atr : d.Atr;

                if (!plan.Valid)
                {
                    Reject(d, DecisionStage.Levels, plan.Reason, plan.Explanation);
                    ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "❌ LVL", plan.Reason);
                    return;
                }

                signal.StopLoss = plan.StopLoss;
                signal.TakeProfits = plan.TakeProfits;
                signal.TakeProfit = plan.TakeProfits.FirstOrDefault();

                d.StopAtrMultiple = plan.StopAtrMultiple;
                d.StopPercent = plan.StopPercent;
                d.RiskReward = plan.RiskReward;
            }
            else
            {
                signal.StopLoss = _slOpt.OptimizeSlAndTp(symbol, klines, signal, ai);
                d.StopMethod = "LegacyOptimizer";
            }

            d.StopLoss = signal.StopLoss;
            d.TakeProfits = signal.TakeProfits?.ToList() ?? new();

            if (signal.StopLoss <= 0)
            {
                Reject(d, DecisionStage.Levels, "StopZero", "Стоп получился нулевым.");
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "❌ SL", "SL=0");
                return;
            }

            bool slSideOk = signal.Side == SignalSide.Buy
                ? signal.StopLoss < signal.EntryPrice
                : signal.StopLoss > signal.EntryPrice;

            if (!slSideOk)
            {
                Reject(d, DecisionStage.Levels, "StopWrongSide",
                    $"Стоп {signal.StopLoss} оказался не с той стороны от входа {signal.EntryPrice} " +
                    $"для {signal.Side}.");
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "❌ SL", "WRONG SIDE");
                return;
            }

            // ------------------ 5. RISK-MANAGER v6 --------------------
            decimal safety = signal.SafetyRiskMultiplier;

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

            d.FreeBalance = _risk.LastBalanceUsdt;
            d.Leverage = signal.Leverage;

            if (qty <= 0)
            {
                Reject(d, DecisionStage.Sizing, "QtyZero",
                    "RiskManager не смог собрать объём: не хватило баланса, маржи " +
                    "или позиция не дотянула до минимального номинала биржи. " +
                    "Точная причина — в журнале движка по этому символу.");
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "❌ QTY=0", "SIZE ZERO");
                return;
            }

            d.Quantity = qty;
            d.Notional = qty * signal.EntryPrice;
            d.RiskUsdt = Math.Abs(signal.EntryPrice - signal.StopLoss) * qty;

            // ------------------ 6. CLEANUP OLD ORDERS -----------------
            await _cleaner.CleanupOutdatedOrdersAsync(symbol, signal, ct);

            // ------------------ 7. EXECUTE ORDER ----------------------
            var result = await _executor.ExecuteAsync(signal, qty, ct);

            if (!result.Success)
            {
                Reject(d, DecisionStage.Execution,
                    string.IsNullOrWhiteSpace(result.Error) ? "OrderRejected" : result.Error,
                    $"Ордер не прошёл: {result.Error}. " +
                    "Позиция не открыта, защитные ордера не выставлялись.");
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "❌ ORD", "ORDER ERROR");
                return;
            }

            // ------------------ 8. ВХОД СОСТОЯЛСЯ ---------------------
            d.Stage = DecisionStage.Entered;
            d.Outcome = DecisionOutcome.Entered;
            d.Reason = "Entered";
            d.FilledPrice = result.EntryPrice;
            d.OrderId = result.OrderId;
            d.PlacedStop = signal.StopLoss;   // OrderExecutor уже подтвердил постановку
            d.Explanation =
                $"Вход выполнен: {qty} по {result.EntryPrice}, стоп {signal.StopLoss} " +
                $"({d.StopMethod}), риск {d.RiskUsdt:0.00} USDT.";

            FillMarketContext(d, ai);
            _journal.Record(d);

            MarkTrade(symbol);

            // ------------------ 9. SUPERVISOR -------------------------
            await _supervisor.SuperviseAsync(symbol, signal, ct);

            ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🟩 OK", $"qty={qty:F4}");
        }

        // ================================================================
        // ЖУРНАЛИРОВАНИЕ ОТКАЗА
        // ================================================================
        private void Reject(TradeDecision d, DecisionStage stage, string reason, string explanation)
        {
            d.Stage = stage;
            d.Outcome = DecisionOutcome.Rejected;
            d.Reason = reason;
            d.Explanation = explanation;

            _journal.Record(d);
        }

        /// <summary>
        /// Рыночный контекст для журнала. Берётся из решения AI, а не из
        /// повторного вызова детектора режима: детектор уже отработал внутри
        /// PredictiveEngine, и второй проход по тем же свечам только сжигал бы
        /// время торгового цикла ради тех же чисел.
        /// </summary>
        private static void FillMarketContext(TradeDecision d, AiDecision ai)
        {
            d.Regime = ai.Trend;
            d.Confidence = (int)(ai.Score * 100);
            d.Volatility = ai.AtrPct;
            d.Score = (int)(ai.Score * 100);
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
