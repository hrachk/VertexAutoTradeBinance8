namespace VertexAutoTradeBinance8.Configuration;

/// <summary>Signal Quality Scoring & Auction (Settings → Signal Quality).</summary>
public sealed class SignalQualityOptions
{
    public const string SectionName = "SignalQuality";

    /// <summary>Minimum CompositeScore 0..100. Below → REJECT BELOW_QUALITY_THRESHOLD.</summary>
    public decimal MinQualityThreshold { get; set; } = 70m;

    /// <summary>BestScoreAuction | FirstComeFirstServed</summary>
    public string SignalSelectionStrategy { get; set; } = "BestScoreAuction";

    /// <summary>Auction drain window in milliseconds (batch competing signals).</summary>
    public int AuctionWindowMs { get; set; } = 400;

    public decimal WeightTrend { get; set; } = 0.35m;
    public decimal WeightDerivatives { get; set; } = 0.20m;
    public decimal WeightVolume { get; set; } = 0.20m;
    public decimal WeightRiskReward { get; set; } = 0.25m;

    /// <summary>Score ≥ this → full size; between Min and this → linear scale to 0.7.</summary>
    public decimal FullSizeScore { get; set; } = 90m;
}
