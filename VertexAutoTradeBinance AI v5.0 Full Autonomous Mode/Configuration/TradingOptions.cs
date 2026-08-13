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
    public decimal MinNotionalUsd { get; set; } = 30m;
    public int CooldownMinutes { get; internal set; }

    // ---  НОВЫЕ ПОЛЯ ---

    /// <summary>Базовый риск → по умолчанию 1.0% если не указано</summary>
    public decimal BaseRiskPercent { get; set; } = 1.0m;

    /// <summary>Минимальный размер позиции после AI-скейлинга</summary>
    public decimal MinNotionalGuard { get; set; } = 30m;

    /// <summary>Минимальный процент → если позиция получается слишком маленькой</summary>
    public decimal MinNotionalGuardPercent { get; set; } = 0.003m; // 0.3% капитала

    /// <summary>Макс. число входов по одному символу пока позиция открыта (1 = только первый, 2 = +один добор).</summary>
    public int MaxEntriesPerSymbol { get; set; } = 2;



    

    public bool Enabled { get; set; } = false;
    public string Level { get; set; } = "off";

    public bool AllowSoftEntryAlways { get; set; } = false;
    public bool RelaxRR { get; set; } = false;
    public bool RelaxPatternBlock { get; set; } = false;
    public bool RelaxLiquidity { get; set; } = false;
    public bool IgnoreCorrelation { get; set; } = false;
    public bool LowerRegimeThreshold { set; get; } = false;


}