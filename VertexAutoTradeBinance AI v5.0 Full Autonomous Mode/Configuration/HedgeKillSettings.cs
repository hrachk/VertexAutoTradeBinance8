namespace VertexAutoTradeBinance8.Configuration
{
    public enum HedgeKillMode
    {
        Safe = 0,
        Hybrid = 1,
        Aggressive = 2
    }

    public sealed class HedgeKillSettings
    {
        public HedgeKillMode Mode { get; set; } = HedgeKillMode.Hybrid;

        // базовые “страховочные” пороги
        public decimal NetOkUsd { get; set; } = 3m;          // если netPnL >= NetOkUsd -> не режем (кроме funding/HardLoss)
        public decimal HardNetUsd { get; set; } = -10m;      // если hedge netPnL уже отрицательный -> резать быстрее

        // hard-loss: минимум боли в USD и множитель ATR-notional боли
        public decimal HardLoserUsd { get; set; } = 18m;
        public decimal HardLoserAtrMult { get; set; } = 1.8m;

        // Giveback budget bounds (в USD) — “золотая середина”
        public decimal GivebackMinUsd { get; set; } = 3.5m;
        public decimal GivebackMaxUsd { get; set; } = 25m;

        // доли bucket в зависимости от уверенности (low..high)
        public decimal GivebackBucketLow { get; set; } = 0.18m;   // low confidence
        public decimal GivebackBucketMid { get; set; } = 0.28m;   // mid
        public decimal GivebackBucketHigh { get; set; } = 0.40m;  // high confidence

        // slope thresholds (SmartRegime)
        public decimal SlopeWeak { get; set; } = 0.008m;   // 0.8%
        public decimal SlopeStrong { get; set; } = 0.015m; // 1.5%

        // volatility threshold (ATR% of price)
        public decimal AtrPctExtreme { get; set; } = 0.020m; // 2% -> осторожнее с kill

        // optional cooldown (режимно)
        public bool UseCooldown { get; set; } = false;
        public int CooldownMinutes { get; set; } = 10;

        // loser close fraction in Hybrid (если не hardLoss)
        public decimal LoserCloseFraction { get; set; } = 0.60m;
    }
}
