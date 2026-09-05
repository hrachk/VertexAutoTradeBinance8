namespace VertexAutoTradeBinance8.Configuration;

/// <summary>
/// Live microstructure protection layer (order-book / tape proxy / funding).
/// Additive only: does not change CORE signal generation. Fail-open on data errors.
/// </summary>
public sealed class SmartFlowOptions
{
    /// <summary>Master switch. false = layer is a no-op (Allow, SizeMult=1).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Hard-block only on extreme adverse flow. Soft cases only reduce size.</summary>
    public bool AllowHardBlock { get; set; } = true;

    /// <summary>
    /// If true, may WIDEN stop (never tighten) using book clusters.
    /// Safe for CORE: still structure-based, only extra breathing room.
    /// </summary>
    public bool EnableSlWiden { get; set; } = true;

    /// <summary>Also call existing AiLiquidityClusterService soft adjust (confidence/size).</summary>
    public bool UseClusterService { get; set; } = true;

    /// <summary>Max spread (ask-bid)/mid to allow full size. Above → size cut; far above → block.</summary>
    public decimal MaxSpreadPct { get; set; } = 0.0008m; // 0.08%

    /// <summary>Spread above this → hard block (illiquid / stressed book).</summary>
    public decimal BlockSpreadPct { get; set; } = 0.0025m; // 0.25%

    /// <summary>|bidNotional-askNotional|/total adverse threshold for soft reduce.</summary>
    public decimal SoftImbalance { get; set; } = 0.45m;

    /// <summary>Adverse imbalance for hard block.</summary>
    public decimal HardImbalance { get; set; } = 0.72m;

    /// <summary>Bars used for taker-buy delta proxy (needs TakerBuyBaseVolume on klines).</summary>
    public int DeltaBars { get; set; } = 8;

    /// <summary>Adverse delta ratio (sell-heavy for longs) soft threshold 0..1.</summary>
    public decimal SoftAdverseDelta { get; set; } = 0.58m;

    /// <summary>Adverse delta hard threshold.</summary>
    public decimal HardAdverseDelta { get; set; } = 0.72m;

    /// <summary>Size multiplier when soft adverse (clamped in service).</summary>
    public decimal SoftSizeMult { get; set; } = 0.70m;

    /// <summary>Extra size cut when funding is crowded same-side.</summary>
    public decimal FundingSizeMult { get; set; } = 0.75m;

    /// <summary>Honor FundingRateService CanEnterLong/Short as hard block.</summary>
    public bool BlockOnFunding { get; set; } = true;

    /// <summary>Minimum top-of-book notional (bid+ask level0) in USDT; below → soft cut.</summary>
    public decimal MinTopNotionalUsd { get; set; } = 2500m;

    /// <summary>Order-book depth levels to request.</summary>
    public int Depth { get; set; } = 50;
}
