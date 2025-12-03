using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using CryptoExchange.Net.Requests;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.Formatting;
using VertexAutoTradeBinance8.Strategy;

namespace VertexAutoTradeBinance8
{
    public class TradingWorker : BackgroundService
    {
        private readonly ILogger<TradingWorker> _logger;
        private readonly BinanceOptions _binanceOptions;
        private readonly TradingOptions _tradingOptions;
        private readonly MarketDataService _marketData;
        private readonly StrategyEngine _strategy;
        private readonly RiskManager _risk;
        private readonly OrderExecutor _executor;
        private readonly BinanceClientFactory _factory;
        private readonly LiquidityGuardService _liquidity;
        private readonly PositionGuardService _positionGuard;
        private readonly PositionProtectorService _protector;
        private readonly OrderCleanerService _orderCleaner;
        private readonly PredictiveEngineV4ConfirmationService _ai;
        private readonly PositionSupervisorService _positionSupervisorService;
        private readonly AiStopLossOptimizer _slOptimizer;
        private readonly AiRiskScalerV2 _riskScaler;

        private readonly AiSelfLearningService _aiLearning;
        private readonly AiModelSnapshotService _aiSnapshot;

        private readonly Dictionary<string, DateTime> _lastTfRun = new();
        private readonly Dictionary<string, DateTime> _lastTradeTime = new();

        private static readonly KlineInterval[] _tfs = new[]
        {
            KlineInterval.OneMinute,
            KlineInterval.FiveMinutes,
            KlineInterval.FifteenMinutes,
            KlineInterval.OneHour,
            KlineInterval.OneDay
        };

        private readonly Dictionary<KlineInterval, TimeSpan> _tfMinIntervals =
            new()
            {
                {KlineInterval.OneMinute, TimeSpan.FromSeconds(2)},
                {KlineInterval.FiveMinutes, TimeSpan.FromSeconds(25)},
                {KlineInterval.FifteenMinutes, TimeSpan.FromSeconds(60)},
                {KlineInterval.ThirtyMinutes, TimeSpan.FromMinutes(2)},   // <-- ДОБАВИЛ
                {KlineInterval.OneHour, TimeSpan.FromMinutes(5)},
                {KlineInterval.FourHour, TimeSpan.FromMinutes(240)},
                {KlineInterval.OneDay, TimeSpan.FromMinutes(30)},
            };

        public TradingWorker(
            ILogger<TradingWorker> logger,
            IOptions<BinanceOptions> binanceOptions,
            IOptions<TradingOptions> tradingOptions,
            MarketDataService marketData,
            StrategyEngine strategy,
            RiskManager risk,
            OrderExecutor executor,
            BinanceClientFactory factory,
            LiquidityGuardService liquidity,
            PositionGuardService positionGuard,
            PositionProtectorService protector,
            OrderCleanerService orderCleaner,
            PredictiveEngineV4ConfirmationService ai,
            AiStopLossOptimizer slOptimizer,
            AiRiskScalerV2 riskScaler,
            PositionSupervisorService positionSupervisorService,
            AiSelfLearningService aiLearning,
            AiModelSnapshotService aiSnapshot)
        {
            _logger = logger;
            _binanceOptions = binanceOptions.Value;
            _tradingOptions = tradingOptions.Value;
            _marketData = marketData;
            _strategy = strategy;
            _risk = risk;
            _executor = executor;
            _factory = factory;
            _liquidity = liquidity;
            _positionGuard = positionGuard;
            _protector = protector;
            _orderCleaner = orderCleaner;
            _ai = ai;
            _slOptimizer = slOptimizer;
            _riskScaler = riskScaler;
            _positionSupervisorService = positionSupervisorService;

            _aiLearning = aiLearning;
            _aiSnapshot = aiSnapshot;
        }

        // ================================================================
        // MAIN LOOP — ENTERPRISE ROUND ROBIN
        // ================================================================
        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            _logger.LogInformation("TradingWorker Enterprise started. Symbols: {symbols}",
                string.Join(",", _binanceOptions.Symbols));

            await EnableHedgeMode();

            // Загружаем AI Snapshot
            try
            {
                var state = await _aiSnapshot.LoadLatestAsync(ct);
                if (state != null)
                    _aiLearning.ImportState(state);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI snapshot load failed");
            }

            // MAIN ENTERPRISE SCHEDULER
            while (!ct.IsCancellationRequested)
            {
                foreach (var timeframe in _tfs)
                {
                    foreach (var symbol in _binanceOptions.Symbols)
                    {
                        if (!ShouldRun(symbol, timeframe))
                            continue;

                        try
                        {
                            await ProcessSymbolAsync(symbol, timeframe, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing {symbol} {tf}", symbol, timeframe);
                            ConsoleSymbolTableFormatter.UpdateTf(symbol, timeframe, "❌ ERR", "Ошибка");
                        }

                        await Task.Delay(35, ct); // равномерность нагрузки
                    }
                }

                await SaveSnapshotPeriodic(ct);

                await Task.Delay(80, ct); // задержка между раундами (важно!)
            }
        }

        // ================================================================
        // ENTERPRISE SCHEDULER LOGIC
        // ================================================================
        private bool ShouldRun(string symbol, KlineInterval tf)
        {
            string key = symbol + "_" + tf;

            if (!_lastTfRun.TryGetValue(key, out var last))
            {
                _lastTfRun[key] = DateTime.UtcNow;
                return true;
            }

            var min = _tfMinIntervals.ContainsKey(tf)
                ? _tfMinIntervals[tf]
                : TimeSpan.FromSeconds(30);

            if (DateTime.UtcNow - last >= min)
            {
                _lastTfRun[key] = DateTime.UtcNow;
                return true;
            }

            return false;
        }

        // ================================================================
        // SYMBOL PROCESSOR
        // ================================================================
        private async Task ProcessSymbolAsync(string symbol, KlineInterval timeframe, CancellationToken ct)
        {
            ConsoleSymbolTableFormatter.UpdateTf(symbol, timeframe, "⏳ RUN", "Получение данных...");

            IReadOnlyList<BinanceFuturesUsdtKline>? klines;
            try
            {
                klines = await _marketData.GetKlines(symbol, timeframe, 200);
            }
            catch
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, timeframe, "❌ ERR", "Ошибка GetKlines");
                return;
            }

            if (klines == null || klines.Count < 60)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, timeframe, "⏸ HOLD", "Мало свечей");
                return;
            }

            TradeSignal? signal;
            try
            {
                signal = _strategy.GenerateSignal(symbol, timeframe, klines);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[STRATEGY ERROR] {symbol} {tf}", symbol, timeframe);
                ConsoleSymbolTableFormatter.UpdateTf(symbol, timeframe, "❌ ERR", ex.GetType().Name);
                return;
            }

            if (signal == null)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, timeframe, "🔴 NO", "Нет сигнала");
                return;
            }

            ConsoleSymbolTableFormatter.UpdateTf(symbol, timeframe, "🟢 SIG",
                $"{(signal.Side == SignalSide.Buy ? "LONG" : "SHORT")} @ {signal.EntryPrice:F4}");

            var aiDecision = _ai.Decide(symbol, timeframe, klines, signal);
            if (!aiDecision.Allow)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, timeframe, "🟡 AI-BLK", aiDecision.Reason);
                return;
            }

            decimal riskMultiplier = _riskScaler.Scale(aiDecision.Grade);
            if (riskMultiplier <= 0)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, timeframe, "🟡 RISK", "AI block");
                return;
            }

            var guard = _liquidity.Analyze(symbol, timeframe, klines, signal.Side, signal.IsSuperSignal);
            if (guard.Block)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, timeframe, "🟡 LIQ", guard.Reason.ToString());
                return;
            }

            var safety = signal?.SafetyRiskMultiplier ?? 1.0m;
            var qty = await _risk.CalculateSafeQty(
                 symbol,
                 signal.EntryPrice,
                 signal.StopLoss,
                 riskMultiplier,
                 safety,   // ✔ ПРАВИЛЬНО
                 signal.Leverage ?? 1m,
                 ct);


            if (qty <= 0)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, timeframe, "❌ QTY=0", "Размер позиции 0");
                return;
            }

            var optimizedSl = _slOptimizer.OptimizeSl(symbol, klines, signal, aiDecision);
            signal.StopLoss = optimizedSl;

            await _orderCleaner.CleanupOutdatedOrdersAsync(symbol, signal, ct);

            await _executor.ExecuteAsync(signal, qty, ct);
            await _positionSupervisorService.SuperviseAsync(symbol, signal, ct);


//            _aiLearning.LearnFromOrder(
//    symbol,
//    win: signal.TakeProfits.Count > 0, // упрощённо: если TP был сработан
//    atr: signal.Atr ?? 0.01m,
//    slDist: Math.Abs(signal.EntryPrice - signal.StopLoss)
//);

            ConsoleSymbolTableFormatter.UpdateTf(symbol, timeframe, "🟩 OK",
                $"Вход qty={qty:F4}");
        }

        // ================================================================
        // MISC
        // ================================================================
        private async Task EnableHedgeMode()
        {
            using var client = _factory.CreateRestClient();
            var current = await client.UsdFuturesApi.Account.GetPositionModeAsync();

            if (!current.Success || current.Data?.IsHedgeMode != false)
            {
                _logger.LogInformation("Hedge Mode already active.");
                return;
            }

            var result = await client.UsdFuturesApi.Account.ModifyPositionModeAsync(true);
            _logger.LogInformation("Hedge Mode Set: {result}", result.Success);
        }

        private async Task SaveSnapshotPeriodic(CancellationToken ct)
        {
            try
            {
                var now = DateTime.UtcNow;
                if (now.Second is >= 0 and < 2) // 1 раз в минуту
                {
                    var state = _aiLearning.ExportState();
                    await _aiSnapshot.SaveSnapshotAsync(state, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Snapshot save error");
            }
        }
    }
}
