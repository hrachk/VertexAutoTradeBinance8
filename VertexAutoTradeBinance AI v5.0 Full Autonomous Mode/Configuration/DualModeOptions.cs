namespace VertexAutoTradeBinance8.Configuration;

/// <summary>
/// Dual-mode foundation. Universe tiers are built dynamically from Binance
/// 24h quote volume (SymbolLiquidityScanner) — not hardcoded symbol lists.
/// </summary>
public sealed class DualModeOptions
{
    public const string SectionName = "DualMode";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Top-N by 24h quote volume = Core majors (TREND only, never RANGE/spread).
    /// Typical: BTC/ETH/SOL… as the market ranks them.
    /// </summary>
    public int CoreMajorCount { get; set; } = 5;

    /// <summary>
    /// Top-N by 24h volume allowed in TREND mode (includes core).
    /// </summary>
    public int TrendUniverseCount { get; set; } = 18;

    /// <summary>
    /// Additional liquid names after core, used as preferred RANGE spread size tier
    /// (rank CoreMajorCount+1 .. CoreMajorCount+SpreadLiquidCount).
    /// Also any symbol inside TrendUniverse but outside core is "TrendLiquid".
    /// </summary>
    public int SpreadLiquidCount { get; set; } = 25;

    /// <summary>
    /// Minimum 24h quote volume (USDT) to allow as thin-alt RANGE candidate.
    /// Below this → blocked (too illiquid).
    /// </summary>
    public decimal MinQuoteVolume24hForRange { get; set; } = 5_000_000m;

    /// <summary>
    /// When true, symbols ranked below TrendUniverse but above MinQuoteVolume24h
    /// may trade RANGE (small size). When false, only top (Core+SpreadLiquid) band.
    /// </summary>
    public bool AllowThinAltsInRange { get; set; } = true;

    /// <summary>
    /// Optional exclusions (ETF / equity perps / known junk). Not a trading universe.
    /// </summary>
    public string[] Blacklist { get; set; } =
    {
        "SOXLUSDT", "SOXSUSDT", "EWYUSDT", "KORUUSDT", "TSLAUSDT", "MSTRUSDT"
    };

    /// <summary>
    /// Refresh ranked universe from tickers at most this often (seconds).
    /// Scanner has its own cache; this is policy-side refresh cadence.
    /// </summary>
    public int UniverseRefreshSeconds { get; set; } = 300;

    // --- Regime ---
    public decimal TrendEfficiencyMin { get; set; } = 0.28m;
    public decimal ChaosAtrRatioMin { get; set; } = 1.85m;
    public decimal ChaosBtcMove15mPct { get; set; } = 1.8m;

    // --- Leverage ---
    public int RangeLeverageMin { get; set; } = 3;
    public int RangeLeverageMax { get; set; } = 5;
    public int TrendLeverageMin { get; set; } = 5;
    public int TrendLeverageMax { get; set; } = 10;

    // --- Size ---
    public decimal RangeSizeMult { get; set; } = 0.35m;
    public decimal RangeLiquidSizeMult { get; set; } = 0.45m;
    public decimal TrendSizeMult { get; set; } = 1.0m;
    public decimal TrendNoFlowSizeMult { get; set; } = 0.0m;

    public bool RequireFlowOnTrend { get; set; } = true;
    public bool RequireFlowOnRange { get; set; } = false;

    public decimal MinVolumeRatio { get; set; } = 0.85m;
    public decimal MinOiDeltaAbs { get; set; } = 0.0015m;
    public decimal MinBookAlign { get; set; } = 0.08m;
    public int RegimeCacheSeconds { get; set; } = 60;
}
