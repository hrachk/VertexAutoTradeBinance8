using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.MarketData;
using VertexAutoTradeBinance8.Strategy.MeanReversion;

namespace VertexAutoTradeBinance8.Strategy
{
    /// <summary>
    /// Single integration point between the two strategy engines and the
    /// rest of the pipeline (TradingWorker -> signal channel -> order
    /// placement -> PositionSupervisor). Neither StrategyEngine nor
    /// MeanReversionEngine is modified to know about the other; this class
    /// simply subscribes to both engines' OnSignalGenerated events and
    /// decides, per incoming signal, whether to forward it onward.
    ///
    /// Routing rules (StrategyModeState.Current):
    ///   - TrendOnly:          only signals from StrategyEngine pass.
    ///   - MeanReversionOnly:  only signals from MeanReversionEngine pass.
    ///   - Auto (default):     a signal passes only if the symbol's CURRENT
    ///                         regime (re-evaluated at routing time, not
    ///                         trusted from whenever the originating engine
    ///                         last computed it) matches that engine's
    ///                         natural habitat — Range/Squeeze for
    ///                         mean-reversion, Trend/StrongTrend for the
    ///                         trend engine. This re-check matters because
    ///                         a signal can sit in the channel for a moment
    ///                         and the regime can flip in between.
    ///
    /// TrendOnly is the safe rollback path: if anything about the new
    /// strategy misbehaves, flipping the mode (no restart needed) restores
    /// 100% pre-v9 behavior since StrategyEngine itself was never touched.
    /// </summary>
    public sealed class StrategyRouter
    {
        private readonly ILogger<StrategyRouter> _logger;
        private readonly StrategyEngine _trendEngine;
        private readonly MeanReversionEngine _meanReversionEngine;
        private readonly SmartRegimeService _smartRegimeService;
        private readonly MarketDataFacade _marketData;
        private readonly StrategyModeState _modeState;

        public event Action<TradeSignal>? OnSignalGenerated;

        public StrategyRouter(
            ILogger<StrategyRouter> logger,
            StrategyEngine trendEngine,
            MeanReversionEngine meanReversionEngine,
            SmartRegimeService smartRegimeService,
            MarketDataFacade marketData,
            StrategyModeState modeState)
        {
            _logger = logger;
            _trendEngine = trendEngine;
            _meanReversionEngine = meanReversionEngine;
            _smartRegimeService = smartRegimeService;
            _marketData = marketData;
            _modeState = modeState;
        }

        /// <summary>
        /// Binds both engines to live market data and starts listening to
        /// both signal streams. Call this once at startup instead of
        /// calling trendEngine.BindReactive directly.
        /// </summary>
        public void BindAll()
        {
            _trendEngine.BindReactive(_marketData);
            _meanReversionEngine.BindReactive(_marketData);

            _trendEngine.OnSignalGenerated += OnTrendSignal;
            _meanReversionEngine.OnSignalGenerated += OnMeanReversionSignal;

            _modeState.ModeChanged += mode =>
                _logger.LogWarning("[ROUTER] Strategy mode changed -> {mode}", mode);

            _logger.LogInformation(
                "[ROUTER] Bound trend + mean-reversion engines. Initial mode={mode}",
                _modeState.Current);
        }

        private void OnTrendSignal(TradeSignal signal) => SafeFireAndForget(() => HandleTrendSignalAsync(signal));
        private void OnMeanReversionSignal(TradeSignal signal) => SafeFireAndForget(() => HandleMeanReversionSignalAsync(signal));

        private void SafeFireAndForget(Func<Task> action)
        {
            _ = action().ContinueWith(t =>
            {
                if (t.Exception != null)
                    _logger.LogError(t.Exception, "[ROUTER] async error");
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private async Task HandleTrendSignalAsync(TradeSignal signal)
        {
            var mode = _modeState.Current;

            if (mode == StrategyMode.MeanReversionOnly)
            {
                _logger.LogDebug(
                    "[ROUTER][{symbol}] Trend signal suppressed — mode=MeanReversionOnly",
                    signal.Symbol);
                return;
            }

            if (mode == StrategyMode.Auto && !await IsTrendRegimeNowAsync(signal.Symbol))
            {
                _logger.LogDebug(
                    "[ROUTER][{symbol}] Trend signal suppressed — regime no longer trend-like at routing time",
                    signal.Symbol);
                return;
            }

            OnSignalGenerated?.Invoke(signal);
        }

        private async Task HandleMeanReversionSignalAsync(TradeSignal signal)
        {
            var mode = _modeState.Current;

            if (mode == StrategyMode.TrendOnly)
            {
                _logger.LogDebug(
                    "[ROUTER][{symbol}] Mean-reversion signal suppressed — mode=TrendOnly",
                    signal.Symbol);
                return;
            }

            if (mode == StrategyMode.Auto && !await IsRangeRegimeNowAsync(signal.Symbol))
            {
                _logger.LogDebug(
                    "[ROUTER][{symbol}] Mean-reversion signal suppressed — regime no longer range-like at routing time",
                    signal.Symbol);
                return;
            }

            OnSignalGenerated?.Invoke(signal);
        }

        private async Task<bool> IsTrendRegimeNowAsync(string symbol)
        {
            var smart = await EvaluateCurrentRegimeAsync(symbol);
            if (smart == null) return true; // fail-open: don't block on a data hiccup

            return smart.BaseRegime is MarketRegime.UpTrend or MarketRegime.DownTrend
                                     or MarketRegime.StrongUpTrend or MarketRegime.StrongDownTrend
                || smart.SmartType is SmartRegimeType.SmartTrend or SmartRegimeType.SmartStrongTrend;
        }

        private async Task<bool> IsRangeRegimeNowAsync(string symbol)
        {
            var smart = await EvaluateCurrentRegimeAsync(symbol);
            if (smart == null) return true; // fail-open: don't block on a data hiccup

            return smart.BaseRegime == MarketRegime.Range
                || smart.SmartType is SmartRegimeType.SmartRange or SmartRegimeType.SmartSqueeze;
        }

        private async Task<SmartRegimeInfo?> EvaluateCurrentRegimeAsync(string symbol)
        {
            try
            {
                // Use the same default reactive timeframe the trend engine
                // operates on for regime classification consistency.
                var klines = await _marketData.GetKlinesAsync(symbol, KlineInterval.FiveMinutes, need: 80);

                if (klines == null || klines.Count < 60) return null;

                return _smartRegimeService.Evaluate(symbol, KlineInterval.FiveMinutes, klines);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[ROUTER][{symbol}] regime re-check failed, failing open", symbol);
                return null;
            }
        }
    }
}
