namespace VertexAutoTradeBinance8.Strategy
{
    public enum StrategyMode
    {
        /// <summary>
        /// Regime-driven routing (the "option 2" behavior): Range/Squeeze
        /// regimes route to MeanReversionEngine, Trend/StrongTrend regimes
        /// route to the existing trend-following StrategyEngine. This is
        /// the default — both engines run, the router picks per symbol
        /// per evaluation based on the live SmartRegimeInfo reading.
        /// </summary>
        Auto = 0,

        /// <summary>Only the existing trend-following StrategyEngine fires,
        /// regardless of regime. Behaves identically to pre-v9.</summary>
        TrendOnly = 1,

        /// <summary>Only MeanReversionEngine fires, regardless of regime.</summary>
        MeanReversionOnly = 2,

        /// <summary>
        /// StrategyCore v1 only — professional minimal engine (default).
        /// Legacy trend / mean-reversion engines stay bound but their signals
        /// are discarded by the router.
        /// </summary>
        StrategyCoreOnly = 3
    }

    /// <summary>
    /// Thread-safe, in-memory, live-toggleable strategy mode (the "option 3"
    /// manual-override layer). Deliberately NOT bound via IOptions/appsettings
    /// hot-reload — the Web UI flips this directly at runtime through a REST
    /// endpoint, and StrategyRouter reads it on every single evaluation with
    /// zero restart required. The appsettings.json "Strategy:Mode" value is
    /// only the STARTUP default; after that, this is the single source of
    /// truth for the rest of the process lifetime.
    /// </summary>
    public sealed class StrategyModeState
    {
        private volatile StrategyMode _mode;

        public StrategyModeState(StrategyMode initial = StrategyMode.Auto)
        {
            _mode = initial;
        }

        public StrategyMode Current => _mode;

        public event Action<StrategyMode>? ModeChanged;

        public void Set(StrategyMode mode)
        {
            if (_mode == mode) return;
            _mode = mode;
            ModeChanged?.Invoke(mode);
        }
    }
}
