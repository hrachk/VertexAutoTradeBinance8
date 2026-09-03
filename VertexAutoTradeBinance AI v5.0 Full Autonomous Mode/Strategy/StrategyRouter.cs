using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.MarketData;
using VertexAutoTradeBinance8.Strategy.MeanReversion;
using VertexAutoTradeBinance8.Strategy.StrategyCore;

namespace VertexAutoTradeBinance8.Strategy
{
    /// <summary>
    /// LIVE = DEMO policy: only CORE_* signals enter the execution channel.
    /// Trend/MeanRev stay bound for diagnostics but are NOT forwarded
    /// (no illusions — same filter as Parallel DEMO).
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

            // Legacy engines remain bound so their internal state stays warm,
            // but signals are NOT forwarded to LIVE/DEMO execution.
            _trendEngine.BindReactive(_marketData);
            _trendEngine.OnSignalGenerated += OnTrendSignal;
            _meanReversionEngine.BindReactive(_marketData);
            _meanReversionEngine.OnSignalGenerated += OnMeanRevSignal;

            _logger.LogInformation(
                "[ROUTER] CORE-only execution (LIVE=DEMO) | mode={mode}",
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
            try { _ = _liveSig.AppendAsync(signal, CancellationToken.None); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ROUTER] live_signals write failed ({src})", source);
            }
            _logger.LogInformation(
                "[ROUTER] {src} → channel+file {sym} {side} conf={c:F2}",
                source, signal.Symbol, signal.Side, signal.Confidence ?? 0m);
            OnSignalGenerated?.Invoke(signal);
        }

        private void OnCoreSignal(TradeSignal signal)
        {
            if (signal == null) return;
            // CRITICAL: CORE is the ONLY execution source (LIVE=DEMO).
            // Legacy mode TrendOnly/MeanReversionOnly must NOT silence CORE —
            // those modes only applied to old engines which we already ignore.
            // Suppressing CORE here = zero signals, zero entries.
            Forward(signal, "CORE");
        }

        private void OnTrendSignal(TradeSignal signal)
        {
            // LIVE=DEMO: never execute/write legacy trend as trade signal
            _logger.LogDebug("[ROUTER] TREND ignored (CORE-only policy) {sym}", signal?.Symbol);
        }

        private void OnMeanRevSignal(TradeSignal signal)
        {
            _logger.LogDebug("[ROUTER] MEANREV ignored (CORE-only policy) {sym}", signal?.Symbol);
        }
    }
}
