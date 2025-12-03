namespace VertexAutoTradeBinance8.Configuration;

public class TradingOptions
{
    public int TimeframeMinutes { get; set; } = 15;
    public double RiskPerTrade { get; set; } = 0.01; // доля депозита riska
    public double Deposit { get; set; } = 100.0;    // размер депозита для расчёта
    public double MinQuantity { get; set; } = 0.001;
    public int CooldownSeconds { get; set; } = 120;
    public int Leverage { get; set; } = 15;


    public decimal MinAtrSlMult { get; set; } = 1.25m;   // минимум SL = 1.25 ATR
    public decimal MinNotional { get; set; } = 35m;     // минимум позиция в USDT
    public decimal MinRiskReward { get; set; } = 2.0m;   // TP1 ≥ 2 * SL
}