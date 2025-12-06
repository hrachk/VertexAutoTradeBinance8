using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
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
            AiModelSnapshotService snapshot)
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
        }

        // ================================================================
        // MAIN LOOP v6 — QUANT REALTIME ENGINE
        // ================================================================
        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            _logger.LogWarning("TradingWorker v6 QUANT-REALTIME started");

            await EnableHedgeMode();

            // Load AI-snapshot
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

                foreach (var tf in TFS)
                {
                    foreach (var symbol in _binance.Symbols)
                    {
                        if (!ShouldRun(symbol, tf))
                            continue;

                        try
                        {
                            await ProcessSymbol(symbol, tf, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing {symbol} {tf}", symbol, tf);
                            ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "❌ ERR", "Ошибка");
                        }

                        await Task.Delay(35, ct);
                    }
                }

                await PeriodicSnapshot(ct);
                await Task.Delay(80, ct);
            }
        }

        // ================================================================
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

        // ================================================================
        // SCHEDULER
        // ================================================================
        private bool ShouldRun(string symbol, KlineInterval tf)
        {
            string key = symbol + "_" + tf;

            if (!_lastTfRun.TryGetValue(key, out var last))
            {
                _lastTfRun[key] = DateTime.UtcNow;
                return true;
            }

            var min = _min.TryGetValue(tf, out var v) ? v : TimeSpan.FromSeconds(30);

            if (DateTime.UtcNow - last >= min)
            {
                _lastTfRun[key] = DateTime.UtcNow;
                return true;
            }

            return false;
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

           
            decimal qty = await _risk.CalculateSafeQty(
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
            signal.StopLoss = _slOpt.OptimizeSl(symbol, klines, signal, ai);

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
