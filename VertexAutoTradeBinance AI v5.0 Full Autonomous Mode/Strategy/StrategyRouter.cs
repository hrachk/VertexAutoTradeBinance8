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
    ///
    /// FIX: mandatory Setup before forward; CHAOS hard-block; no empty-setup leaks.
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

            // ── Mandatory Setup ──────────────────────────────────────────
            signal.EnsureSetup();
            if (string.IsNullOrWhiteSpace(signal.Setup))
            {
                _logger.LogWarning(
                    "[ROUTER] NO_SETUP block {src} {sym} reason={r}",
                    source, signal.Symbol, signal.Reason);
                return;
            }
            if (!ExecutableStrategyPolicy.IsLiveExecutable(signal.Setup))
            {
                _logger.LogWarning(
                    "[ROUTER] BAD_SETUP block {src} {sym} setup={s}",
                    source, signal.Symbol, signal.Setup);
                return;
            }

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
                "[ROUTER] {src} → channel+file {sym} {side} conf={c:F2} setup={s} reason={r}",
                source, signal.Symbol, signal.Side, signal.Confidence ?? 0m, signal.Setup, signal.Reason);
            OnSignalGenerated?.Invoke(signal);
        }

        /// <summary>TREND leg: structure/pullback only when market is trending (or dual disabled).</summary>
        private void OnCoreSignal(TradeSignal signal)
        {
            if (signal == null) return;

            signal.EnsureSetup();
            // Force CORE prefix if engine forgot
            if (string.IsNullOrWhiteSpace(signal.Setup) && !string.IsNullOrWhiteSpace(signal.Reason))
            {
                var r = signal.Reason.Trim();
                int pipe = r.IndexOf('|');
                if (pipe > 0) r = r.Substring(0, pipe);
                if (!r.StartsWith("CORE_", StringComparison.OrdinalIgnoreCase)
                    && !r.StartsWith("TREND_", StringComparison.OrdinalIgnoreCase)
                    && !r.StartsWith("INST_", StringComparison.OrdinalIgnoreCase))
                {
                    signal.Setup = "CORE_" + r;
                    signal.Reason = signal.Setup + (signal.Reason.Contains('|') ? signal.Reason[signal.Reason.IndexOf('|')..] : "");
                }
                else
                    signal.Setup = r;
            }

            var uiMode = _modeState.Current;
            if (uiMode == StrategyMode.MeanReversionOnly)
            {
                _logger.LogDebug("[ROUTER] CORE suppressed UI=MeanReversionOnly");
                return;
            }

            var regime = Regime();
            if (regime == DualMarketMode.Chaos)
            {
                _logger.LogInformation("[ROUTER] CORE held {sym} — regime=CHAOS", signal.Symbol);
                return;
            }
            if (regime == DualMarketMode.Range)
            {
                signal.SizeMultiplier = Math.Clamp(signal.SizeMultiplier * 0.55m, 0.20m, 0.70m);
                if (signal.Leverage is null or > 7)
                    signal.Leverage = 6;
                _logger.LogInformation(
                    "[ROUTER] CORE in RANGE {sym} size×{sz:F2} (parallel with scalp, reduced)",
                    signal.Symbol, signal.SizeMultiplier);
            }
            else if (regime == DualMarketMode.Unknown)
            {
                signal.SizeMultiplier = Math.Clamp(signal.SizeMultiplier * 0.70m, 0.25m, 1m);
                _logger.LogInformation(
                    "[ROUTER] CORE in UNKNOWN {sym} size×{sz:F2} (no longer hard-hold)",
                    signal.Symbol, signal.SizeMultiplier);
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
            if (_dualMode != null)
            {
                if (regime == DualMarketMode.Chaos)
                {
                    _logger.LogInformation("[ROUTER] SPREAD held {sym} — regime=CHAOS", signal.Symbol);
                    return;
                }
                if (regime == DualMarketMode.Trend)
                {
                    // Parallel micro-scalp during trend (small size only)
                    signal.SizeMultiplier = Math.Clamp(signal.SizeMultiplier * 0.28m, 0.15m, 0.40m);
                    if (signal.Leverage is null or > 4)
                        signal.Leverage = 3;
                    _logger.LogInformation(
                        "[ROUTER] SPREAD parallel TREND {sym} micro size×{sz:F2}",
                        signal.Symbol, signal.SizeMultiplier);
                }
                else if (regime == DualMarketMode.Unknown)
                {
                    signal.SizeMultiplier = Math.Clamp(signal.SizeMultiplier * 0.40m, 0.18m, 0.55m);
                    if (signal.Leverage is null or > 4)
                        signal.Leverage = 4;
                }
            }

            if (ExecutableStrategyPolicy.IsCoreMajorSymbol(signal.Symbol))
            {
                if (_modeState.Current != StrategyMode.MeanReversionOnly)
                {
                    _logger.LogDebug("[ROUTER] SPREAD skip major {sym}", signal.Symbol);
                    return;
                }
            }

            bool isLong = signal.Side == SignalSide.Buy;
            string tag = regime == DualMarketMode.Trend ? "RANGE_SCALP" : "RANGE_SPREAD";
            signal.Reason = isLong ? $"{tag}_LONG" : $"{tag}_SHORT";
            signal.Setup = signal.Reason; // explicit Setup for History
            if (signal.Leverage is null or > 5)
                signal.Leverage = 4;

            if (!ScalpPreTradeGate.Allow(signal, _marketData, out var scalpWhy))
            {
                _logger.LogInformation(
                    "[ROUTER] SPREAD pre-trade skip {sym}: {why}", signal.Symbol, scalpWhy);
                return;
            }

            Forward(signal, "SCALP/SPREAD");
        }
    }
}
