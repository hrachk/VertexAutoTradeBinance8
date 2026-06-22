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

        /// <summary>When true, requires rolling-window VWAP to agree with
        /// the signal's direction (price above VWAP for longs, below for
        /// shorts) before the signal is accepted — an additional,
        /// optional confirmation layer on top of the existing EMA/
        /// rejection-candle logic. Defaults to false: a new addition to a
        /// proven system should be an explicit opt-in, not a silent
        /// change to live behavior.</summary>
        public bool VwapConfirmationEnabled { get; set; } = false;

        /// <summary>Rolling window size (in bars) for the VWAP
        /// calculation when VwapConfirmationEnabled is true.</summary>
        public int VwapPeriod { get; set; } = 20;

        /// <summary>When true, requires the SAME symbol's higher-timeframe
        /// EMA structure to agree with the signal's direction before it's
        /// accepted (e.g. a long signal on 15m needs the 4h EMA21/EMA55
        /// relationship to also be bullish). Defaults to false, same
        /// explicit-opt-in reasoning as VwapConfirmationEnabled.</summary>
        public bool HtfConfirmationEnabled { get; set; } = false;

        /// <summary>Higher timeframe to confirm against, as a label
        /// ("15m"/"30m"/"1h"/"4h"/"1d"). Defaults to "4h".</summary>
        public string HtfConfirmationTimeframe { get; set; } = "4h";
    }
}
