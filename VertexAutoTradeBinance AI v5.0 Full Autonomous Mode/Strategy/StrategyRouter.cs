using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.MarketData;
using VertexAutoTradeBinance8.Strategy.MeanReversion;
using VertexAutoTradeBinance8.Strategy.StrategyCore;

namespace VertexAutoTradeBinance8.Strategy
{
    /// <summary>
    /// v1.3 routing:
    ///   StrategyCoreOnly → CORE only
    ///   Auto → CORE + legacy trend (no dead silence if CORE quiet)
    ///   TrendOnly / MeanReversionOnly → explicit legacy
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
            _coreEngine.BindReactive(_marketData);
            _coreEngine.OnSignalGenerated += OnCoreSignal;

            _trendEngine.BindReactive(_marketData);
            _trendEngine.OnSignalGenerated += OnTrendSignal;

            _meanReversionEngine.BindReactive(_marketData);
            _meanReversionEngine.OnSignalGenerated += OnMeanRevSignal;

            _logger.LogInformation(
                "[ROUTER] Bound Core+Legacy | mode={mode} (Auto=CORE+Trend)",
                _modeState.Current);
        }

        public void UnbindAll()
        {
            try { _coreEngine.UnbindReactive(); } catch { }
            try { _trendEngine.UnbindReactive(); } catch { }
            try { _meanReversionEngine.UnbindReactive(); } catch { }
            _coreEngine.OnSignalGenerated -= OnCoreSignal;
            _trendEngine.OnSignalGenerated -= OnTrendSignal;
            _meanReversionEngine.OnSignalGenerated -= OnMeanRevSignal;
        }

        private void OnCoreSignal(TradeSignal signal)
        {
            if (signal == null) return;
            var mode = _modeState.Current;
            if (mode == StrategyMode.TrendOnly || mode == StrategyMode.MeanReversionOnly)
            {
                _logger.LogDebug("[ROUTER] CORE suppressed mode={mode}", mode);
                return;
            }
            _logger.LogInformation("[ROUTER] CORE → channel {sym} {side}", signal.Symbol, signal.Side);
            OnSignalGenerated?.Invoke(signal);
        }

        private void OnTrendSignal(TradeSignal signal)
        {
            if (signal == null) return;
            var mode = _modeState.Current;
            // Auto: allow trend as fallback so UI/live_signals never go fully dead
            // StrategyCoreOnly: block trend
            // TrendOnly: allow
            if (mode == StrategyMode.StrategyCoreOnly || mode == StrategyMode.MeanReversionOnly)
            {
                _logger.LogDebug("[ROUTER] Trend suppressed mode={mode}", mode);
                return;
            }
            _logger.LogInformation("[ROUTER] TREND → channel {sym} {side}", signal.Symbol, signal.Side);
            OnSignalGenerated?.Invoke(signal);
        }

        private void OnMeanRevSignal(TradeSignal signal)
        {
            if (signal == null) return;
            var mode = _modeState.Current;
            if (mode != StrategyMode.MeanReversionOnly && mode != StrategyMode.Auto)
            {
                return;
            }
            // Auto: also allow mean-rev (optional breadth). Keep for Auto only.
            if (mode == StrategyMode.Auto || mode == StrategyMode.MeanReversionOnly)
            {
                _logger.LogInformation("[ROUTER] MEANREV → channel {sym} {side}", signal.Symbol, signal.Side);
                OnSignalGenerated?.Invoke(signal);
            }
        }
    }
}
