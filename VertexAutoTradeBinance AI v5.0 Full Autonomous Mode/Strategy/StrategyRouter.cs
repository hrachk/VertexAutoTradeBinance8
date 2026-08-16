using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.MarketData;
using VertexAutoTradeBinance8.Strategy.MeanReversion;
using VertexAutoTradeBinance8.Strategy.StrategyCore;

namespace VertexAutoTradeBinance8.Strategy
{
    /// <summary>
    /// Routes strategy signals into TradingWorker.
    ///
    /// DEFAULT (Auto / StrategyCoreOnly): ONLY StrategyCore v1 passes.
    /// Legacy StrategyEngine + MeanReversionEngine remain available via
    /// TrendOnly / MeanReversionOnly for emergency rollback — they are
    /// NOT used in Auto anymore (legacy produced deposit-draining noise).
    /// </summary>
    public sealed class StrategyRouter
    {
        private readonly ILogger<StrategyRouter> _logger;
        private readonly StrategyEngine _trendEngine;
        private readonly MeanReversionEngine _meanReversionEngine;
        private readonly StrategyCoreEngine _coreEngine;
        private readonly SmartRegimeService _smartRegimeService;
        private readonly MarketDataFacade _marketData;
        private readonly StrategyModeState _modeState;

        public event Action<TradeSignal>? OnSignalGenerated;

        public StrategyRouter(
            ILogger<StrategyRouter> logger,
            StrategyEngine trendEngine,
            MeanReversionEngine meanReversionEngine,
            StrategyCoreEngine coreEngine,
            SmartRegimeService smartRegimeService,
            MarketDataFacade marketData,
            StrategyModeState modeState)
        {
            _logger = logger;
            _trendEngine = trendEngine;
            _meanReversionEngine = meanReversionEngine;
            _coreEngine = coreEngine;
            _smartRegimeService = smartRegimeService;
            _marketData = marketData;
            _modeState = modeState;
        }

        public void BindAll()
        {
            // Core is always bound — primary engine
            _coreEngine.BindReactive(_marketData);
            _coreEngine.OnSignalGenerated += OnCoreSignal;

            // Legacy still bound only so TrendOnly / MeanReversionOnly rollback works
            _trendEngine.BindReactive(_marketData);
            _trendEngine.OnSignalGenerated += s => SafeFire(() => HandleLegacyTrendAsync(s));

            _meanReversionEngine.BindReactive(_marketData);
            _meanReversionEngine.OnSignalGenerated += s => SafeFire(() => HandleLegacyMeanRevAsync(s));

            _logger.LogInformation(
                "[ROUTER] Bound Core+Legacy | mode={mode} (Auto/CoreOnly => CORE only)",
                _modeState.Current);
        }

        public void UnbindAll()
        {
            try { _coreEngine.UnbindReactive(); } catch { }
            try { _trendEngine.UnbindReactive(); } catch { }
            try { _meanReversionEngine.UnbindReactive(); } catch { }
            _coreEngine.OnSignalGenerated -= OnCoreSignal;
        }

        private void OnCoreSignal(TradeSignal signal)
        {
            if (signal == null) return;
            var mode = _modeState.Current;
            // Core passes in Auto and StrategyCoreOnly
            if (mode is StrategyMode.Auto or StrategyMode.StrategyCoreOnly or StrategyMode.TrendOnly)
            {
                // In TrendOnly we still prefer not mixing — skip core when TrendOnly
                if (mode == StrategyMode.TrendOnly)
                {
                    _logger.LogDebug("[ROUTER] Core suppressed — mode=TrendOnly");
                    return;
                }
                if (mode == StrategyMode.MeanReversionOnly)
                {
                    _logger.LogDebug("[ROUTER] Core suppressed — mode=MeanReversionOnly");
                    return;
                }
                OnSignalGenerated?.Invoke(signal);
            }
        }

        private void SafeFire(Func<Task> action)
        {
            _ = action().ContinueWith(t =>
            {
                if (t.Exception != null)
                    _logger.LogError(t.Exception, "[ROUTER] async error");
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private Task HandleLegacyTrendAsync(TradeSignal signal)
        {
            var mode = _modeState.Current;
            // Legacy trend ONLY when explicitly TrendOnly (rollback)
            if (mode != StrategyMode.TrendOnly)
            {
                _logger.LogDebug(
                    "[ROUTER][{sym}] Legacy trend suppressed — mode={mode} (use StrategyCore)",
                    signal.Symbol, mode);
                return Task.CompletedTask;
            }
            OnSignalGenerated?.Invoke(signal);
            return Task.CompletedTask;
        }

        private Task HandleLegacyMeanRevAsync(TradeSignal signal)
        {
            var mode = _modeState.Current;
            if (mode != StrategyMode.MeanReversionOnly)
            {
                _logger.LogDebug(
                    "[ROUTER][{sym}] Legacy mean-rev suppressed — mode={mode} (use StrategyCore)",
                    signal.Symbol, mode);
                return Task.CompletedTask;
            }
            OnSignalGenerated?.Invoke(signal);
            return Task.CompletedTask;
        }
    }
}
