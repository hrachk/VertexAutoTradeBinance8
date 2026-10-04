using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.DualMode;
using VertexAutoTradeBinance8.Services.MarketData;
using VertexAutoTradeBinance8.Strategy.MeanReversion;
using VertexAutoTradeBinance8.Strategy.StrategyCore;

namespace VertexAutoTradeBinance8.Strategy
{
    /// <summary>
    /// Dual strategy router (replaces CORE-only policy):
    ///   TREND regime  → CORE structure/pullback/breakout (trend leg)
    ///   RANGE regime  → Mean-reversion / spread on non-majors (range leg)
    ///   CHAOS         → no execution forwards
    /// LIVE and DEMO share the same channel after this router.
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
        private readonly DualModeTradingPolicy? _dualMode;

        public event Action<TradeSignal>? OnSignalGenerated;

        public StrategyRouter(
            ILogger<StrategyRouter> logger,
            StrategyEngine trendEngine,
            MeanReversionEngine meanReversionEngine,
            StrategyCoreEngine coreEngine,
            SmartRegimeService smartRegimeService,
            MarketDataFacade marketData,
            StrategyModeState modeState,
            LiveSignalService liveSig,
            DualModeTradingPolicy? dualMode = null)
        {
            _logger = logger;
            _trendEngine = trendEngine;
            _meanReversionEngine = meanReversionEngine;
            _coreEngine = coreEngine;
            _smartRegimeService = smartRegimeService;
            _marketData = marketData;
            _modeState = modeState;
            _liveSig = liveSig;
            _dualMode = dualMode;
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
                "[ROUTER] Dual strategy ACTIVE | TREND→CORE | RANGE→SPREAD(MR) | UI mode={mode}",
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

        private DualMarketMode Regime()
        {
            if (_dualMode == null) return DualMarketMode.Unknown;
            return _dualMode.CurrentBtcMode;
        }

        private void Forward(TradeSignal signal, string source)
        {
            if (signal == null) return;

            // Best-effort universal gate using cached bars (Worker re-checks hard)
            try
            {
                var m15 = _marketData.GetCachedKlines(signal.Symbol, Binance.Net.Enums.KlineInterval.FifteenMinutes)
                          ?? _marketData.GetBufferedKlines(signal.Symbol, Binance.Net.Enums.KlineInterval.FifteenMinutes);
                var h1 = _marketData.GetCachedKlines(signal.Symbol, Binance.Net.Enums.KlineInterval.OneHour)
                         ?? _marketData.GetBufferedKlines(signal.Symbol, Binance.Net.Enums.KlineInterval.OneHour);
                if (!EntrySanityGate.Allow(signal, m15, h1, out var sanity))
                {
                    _logger.LogWarning(
                        "[ROUTER] SANITY BLOCK {src} {sym} {side}: {reason}",
                        source, signal.Symbol, signal.Side, sanity);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[ROUTER] sanity skip (cache) {sym}", signal.Symbol);
            }

            try { _ = _liveSig.AppendAsync(signal, CancellationToken.None); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ROUTER] live_signals write failed ({src})", source);
            }
            _logger.LogInformation(
                "[ROUTER] {src} → channel+file {sym} {side} conf={c:F2} reason={r}",
                source, signal.Symbol, signal.Side, signal.Confidence ?? 0m, signal.Reason);
            OnSignalGenerated?.Invoke(signal);
        }

        /// <summary>TREND leg: structure/pullback only when market is trending (or dual disabled).</summary>
        private void OnCoreSignal(TradeSignal signal)
        {
            if (signal == null) return;

            var uiMode = _modeState.Current;
            if (uiMode == StrategyMode.MeanReversionOnly)
            {
                _logger.LogDebug("[ROUTER] CORE suppressed UI=MeanReversionOnly");
                return;
            }

            var regime = Regime();
            if (regime == DualMarketMode.Range)
            {
                _logger.LogInformation(
                    "[ROUTER] CORE held {sym} — regime=RANGE (use spread leg, not local trend)",
                    signal.Symbol);
                return;
            }
            if (regime == DualMarketMode.Chaos)
            {
                _logger.LogInformation("[ROUTER] CORE held {sym} — regime=CHAOS", signal.Symbol);
                return;
            }

            // Tag as TREND leg for journal clarity (keep CORE_ prefix executable)
            if (!string.IsNullOrEmpty(signal.Reason) &&
                signal.Reason.StartsWith("CORE_", StringComparison.OrdinalIgnoreCase) &&
                !signal.Reason.Contains("TREND", StringComparison.OrdinalIgnoreCase))
            {
                // e.g. CORE_STRUCT_LONG → still CORE_* for filters; dual tag in worker
            }

            Forward(signal, "TREND/CORE");
        }

        private void OnTrendSignal(TradeSignal signal)
        {
            // Legacy StrategyEngine stays diagnostic-only (duplicate of CORE in practice)
            _logger.LogDebug("[ROUTER] legacy TREND engine ignored {sym}", signal?.Symbol);
        }

        /// <summary>RANGE leg: Z-score mean-reversion / spread on non-majors only.</summary>
        private void OnMeanRevSignal(TradeSignal signal)
        {
            if (signal == null) return;

            var uiMode = _modeState.Current;
            if (uiMode == StrategyMode.TrendOnly)
            {
                _logger.LogDebug("[ROUTER] MEANREV suppressed UI=TrendOnly");
                return;
            }

            var regime = Regime();
            // When DualMode disabled / Unknown: allow MR only if UI is Auto or MeanReversionOnly
            if (_dualMode != null)
            {
                if (regime == DualMarketMode.Trend)
                {
                    _logger.LogInformation(
                        "[ROUTER] SPREAD held {sym} — regime=TREND (spread only in RANGE)",
                        signal.Symbol);
                    return;
                }
                if (regime == DualMarketMode.Chaos)
                {
                    _logger.LogInformation("[ROUTER] SPREAD held {sym} — regime=CHAOS", signal.Symbol);
                    return;
                }
                // Range or Unknown → allow spread (Unknown = treat as range-friendly for MR)
            }

            if (ExecutableStrategyPolicy.IsCoreMajorSymbol(signal.Symbol))
            {
                _logger.LogInformation(
                    "[ROUTER] SPREAD skip major {sym} — majors not used for range/spread",
                    signal.Symbol);
                return;
            }

            // Canonical reason for DualMode + journal
            bool isLong = signal.Side == SignalSide.Buy;
            signal.Reason = isLong ? "RANGE_SPREAD_LONG" : "RANGE_SPREAD_SHORT";
            // Prefer small leverage hint before DualMode caps
            if (signal.Leverage is null or > 5)
                signal.Leverage = 4;

            Forward(signal, "RANGE/SPREAD");
        }
    }
}
