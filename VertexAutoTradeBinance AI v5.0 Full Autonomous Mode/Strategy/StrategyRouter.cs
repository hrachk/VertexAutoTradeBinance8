using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.MarketData;
using VertexAutoTradeBinance8.Strategy.MeanReversion;
using VertexAutoTradeBinance8.Strategy.StrategyCore;

namespace VertexAutoTradeBinance8.Strategy
{
    /// <summary>
    /// Routes strategy signals. Also writes live_signals.json DIRECTLY so the
    /// Market UI cannot go dark if TradingWorker is slow/blocked.
    /// Auto = CORE + Trend (+ MeanRev).
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
        private readonly LiveSignalService _liveSig;

        public event Action<TradeSignal>? OnSignalGenerated;

        public StrategyRouter(
            ILogger<StrategyRouter> logger,
            StrategyEngine trendEngine,
            MeanReversionEngine meanReversionEngine,
            StrategyCoreEngine coreEngine,
            SmartRegimeService smartRegimeService,
            MarketDataFacade marketData,
            StrategyModeState modeState,
            LiveSignalService liveSig)
        {
            _logger = logger;
            _trendEngine = trendEngine;
            _meanReversionEngine = meanReversionEngine;
            _coreEngine = coreEngine;
            _smartRegimeService = smartRegimeService;
            _marketData = marketData;
            _modeState = modeState;
            _liveSig = liveSig;
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
                "[ROUTER] Bound Core+Legacy | mode={mode} | live_signals write=direct",
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

        private void Forward(TradeSignal signal, string source)
        {
            if (signal == null) return;

            // 1) UI file FIRST — independent of worker channel / AI / cooldown
            try
            {
                _ = _liveSig.AppendAsync(signal, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ROUTER] live_signals write failed ({src})", source);
            }

            _logger.LogInformation(
                "[ROUTER] {src} → channel+file {sym} {side} conf={c:F2}",
                source, signal.Symbol, signal.Side, signal.Confidence ?? 0m);

            // 2) Worker pipeline (execution / demo downstream)
            OnSignalGenerated?.Invoke(signal);
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
            Forward(signal, "CORE");
        }

        private void OnTrendSignal(TradeSignal signal)
        {
            if (signal == null) return;
            var mode = _modeState.Current;
            if (mode == StrategyMode.StrategyCoreOnly || mode == StrategyMode.MeanReversionOnly)
            {
                _logger.LogDebug("[ROUTER] Trend suppressed mode={mode}", mode);
                return;
            }
            Forward(signal, "TREND");
        }

        private void OnMeanRevSignal(TradeSignal signal)
        {
            if (signal == null) return;
            var mode = _modeState.Current;
            if (mode != StrategyMode.MeanReversionOnly && mode != StrategyMode.Auto)
                return;
            Forward(signal, "MEANREV");
        }
    }
}
