namespace VertexAutoTradeBinance8.Configuration;

public class TradingOptions
{
    public int TimeframeMinutes { get; set; } = 5;
    public double RiskPerTrade { get; set; } = 0.01; // доля депозита
    public double Deposit { get; set; } = 100.0;    // размер депозита для расчёта
    public double MinQuantity { get; set; } = 0.001;
    public int CooldownSeconds { get; set; } = 120;
    public int Leverage { get; set; }
}