namespace VertexAutoTradeBinance8.Strategy
{
    public enum StrategyMode
    {
        /// <summary>
        /// Default mode: both CORE and mean-reversion engines may emit.
        /// Each engine applies its own market-regime and setup filters; the
        /// router enforces shared CHAOS and entry-sanity checks.
        /// </summary>
        Auto = 0,

        /// <summary>Backward-compatible alias for the CORE-only route.</summary>
        TrendOnly = 1,

        /// <summary>Only MeanReversionEngine signals are forwarded; its internal regime gates still apply.</summary>
        MeanReversionOnly = 2,

        /// <summary>
        /// CORE strategy only. Legacy trend signals are diagnostic-only;
        /// mean-reversion signals are discarded by the router.
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
