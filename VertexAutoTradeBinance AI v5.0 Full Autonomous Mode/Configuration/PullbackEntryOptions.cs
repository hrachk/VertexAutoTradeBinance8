namespace VertexAutoTradeBinance8.Configuration
{
    /// <summary>
    /// Strongly-typed mirror of the "Strategy:PullbackEntry" appsettings.json
    /// section. Backs StrategyEngine.TryPullbackEma21 — the real, current
    /// pullback-entry implementation (previously hardcoded: EMA period 21,
    /// touch zone 0.5x ATR). The Settings page's older "Pullback Entry
    /// (legacy)" controls (PullbackType/EmaPeriod/EmaZonePercent) wrote to
    /// config keys nothing in the Engine actually read — this class and the
    /// two fields below are what TryPullbackEma21 reads now, via the same
    /// live-reloading IOptionsMonitor mechanism used elsewhere in v9 (no
    /// restart needed to take effect).
    /// </summary>
    public sealed class PullbackEntryOptions
    {
        /// <summary>EMA period used for both the trend-direction check and
        /// the pullback touch-zone center. Was hardcoded to 21.</summary>
        public int EmaPeriod { get; set; } = 21;

        /// <summary>Width of the "touched the EMA" zone, expressed as a
        /// multiple of ATR (NOT a percentage of price — ATR-normalized so
        /// it scales correctly across symbols of very different
        /// volatility). Was hardcoded to 0.5.</summary>
        public decimal ZoneAtrMultiplier { get; set; } = 0.5m;
    }
}
