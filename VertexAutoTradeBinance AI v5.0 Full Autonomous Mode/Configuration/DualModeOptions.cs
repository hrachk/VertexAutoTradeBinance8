namespace VertexAutoTradeBinance8.Configuration;

/// <summary>
/// Dual-mode trading foundation:
/// TREND  → 15–18 liquid names (not only top majors), flow required, full gear
/// RANGE  → spread on non-core: liquid mid-tier + thinner alts; NO pure majors; lev 3–5x
/// CHAOS  → no new entries
/// </summary>
public sealed class DualModeOptions
{
    public const string SectionName = "DualMode";

    /// <summary>Master switch. When false, policy is fail-open (legacy CORE path).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Pure majors — NEVER traded in RANGE/spread (avoid chopping BTC/ETH/etc. in sideways).
    /// Still allowed in TREND with capital-flow confirmation.
    /// </summary>
    public string[] CoreMajors { get; set; } =
    {
        "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT"
    };

    /// <summary>
    /// Full TREND universe: ~15–18 liquid names (includes CoreMajors + liquid mid).
    /// Only these may open in TREND mode.
    /// </summary>
    public string[] TrendUniverse { get; set; } =
    {
        "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT",
        "DOGEUSDT", "ADAUSDT", "AVAXUSDT", "LINKUSDT", "LTCUSDT",
        "DOTUSDT", "NEARUSDT", "ATOMUSDT", "UNIUSDT", "APTUSDT",
        "ARBUSDT", "OPUSDT", "SUIUSDT"
    };

    /// <summary>
    /// Optional explicit list of liquid/popular names preferred for RANGE spread
    /// (in addition to thinner alts). Empty = any non-CoreMajor is RANGE-eligible.
    /// When non-empty: RANGE only if symbol is in this list OR treated as thin alt
    /// (not in TrendUniverse) — see policy.
    /// </summary>
    public string[] SpreadLiquidUniverse { get; set; } =
    {
        "DOGEUSDT", "ADAUSDT", "AVAXUSDT", "LINKUSDT", "LTCUSDT",
        "DOTUSDT", "NEARUSDT", "ATOMUSDT", "UNIUSDT", "APTUSDT",
        "ARBUSDT", "OPUSDT", "SUIUSDT", "FILUSDT", "INJUSDT",
        "AAVEUSDT", "RENDERUSDT", "FETUSDT"
    };

    /// <summary>
    /// Legacy alias: if set non-empty, used as extra RANGE allow-list together with SpreadLiquid.
    /// Prefer SpreadLiquidUniverse.
    /// </summary>
    public string[] SpreadUniverse { get; set; } = Array.Empty<string>();

    /// <summary>Hard-exclude from all auto modes (ETFs, junk).</summary>
    public string[] Blacklist { get; set; } =
    {
        "SOXLUSDT", "SOXSUSDT", "EWYUSDT", "KORUUSDT", "TSLAUSDT", "MSTRUSDT"
    };

    /// <summary>
    /// When true, any non-core / non-blacklist symbol may trade RANGE
    /// (thin alts + liquid mid). When false, only SpreadLiquidUniverse (+ SpreadUniverse).
    /// </summary>
    public bool AllowThinAltsInRange { get; set; } = true;

    // --- Regime thresholds (BTC 1H efficiency / ATR) ---
    public decimal TrendEfficiencyMin { get; set; } = 0.28m;
    public decimal ChaosAtrRatioMin { get; set; } = 1.85m;
    public decimal ChaosBtcMove15mPct { get; set; } = 1.8m;

    // --- Leverage by mode ---
    public int RangeLeverageMin { get; set; } = 3;
    public int RangeLeverageMax { get; set; } = 5;
    public int TrendLeverageMin { get; set; } = 5;
    public int TrendLeverageMax { get; set; } = 10;

    // --- Size multipliers ---
    public decimal RangeSizeMult { get; set; } = 0.35m;
    /// <summary>Slightly higher size for liquid mid-tier spread vs thin junk.</summary>
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
