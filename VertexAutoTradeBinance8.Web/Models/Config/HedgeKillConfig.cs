public class HedgeKillConfig
{
    public string Mode { get; set; } = "Hybrid";

    public decimal NetOkUsd { get; set; }
    public decimal HardNetUsd { get; set; }
    public decimal HardLoserUsd { get; set; }
    public decimal HardLoserAtrMult { get; set; }

    public decimal GivebackMinUsd { get; set; }
    public decimal GivebackMaxUsd { get; set; }
    public decimal GivebackBucketLow { get; set; }
    public decimal GivebackBucketMid { get; set; }
    public decimal GivebackBucketHigh { get; set; }

    public decimal SlopeWeak { get; set; }
    public decimal SlopeStrong { get; set; }
    public decimal AtrPctExtreme { get; set; }

    public bool UseCooldown { get; set; }
    public int CooldownMinutes { get; set; }

    public decimal LoserCloseFraction { get; set; }
}
