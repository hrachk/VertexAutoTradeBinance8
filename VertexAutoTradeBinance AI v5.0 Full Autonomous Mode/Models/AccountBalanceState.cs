namespace VertexAutoTradeBinance8.Models
{
    public sealed class AccountBalanceState
    {
        public decimal WalletBalanceUsd { get; set; }        // wb
        public decimal EquityUsd { get; set; }               // wb + uPnL
        public decimal AvailableBalanceUsd { get; set; }     // ab
        public decimal UsedMarginUsd { get; set; }           // im
        public decimal UnrealizedPnlUsd { get; set; }        // up

        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        // 🔥 FIX: вычисляемое свойство
        public decimal MarginRatio =>
            EquityUsd > 0
                ? (UsedMarginUsd / EquityUsd) * 100m
                : 0m;
    }
}
