namespace VertexAutoTradeBinance8.Configuration;

public class TradingOptions
{
    public int TimeframeMinutes { get; set; }
    /// <summary>Базовый риск → по умолчанию 1.0% если не указано</summary>
    public decimal BaseRiskPercent { get; set; }
    /// <summary>Минимальный размер позиции после AI-скейлинга</summary>
    public decimal MinNotionalGuard { get; set; }

    public decimal Deposit { get; set; }   // размер депозита для расчёта
    public int CooldownMinutes { get; set; }

    public double RiskPerTrade { get; set; } // доля депозита riska 
    public double MinQuantity { get; set; } 
    public int CooldownSeconds { get; set; } 
    public int Leverage { get; set; }  
    public decimal MinAtrSlMult { get; set; }    // минимум SL = 1.25 ATR
    public decimal MinNotional { get; set; }     // минимум позиция в USDT
    public decimal MinRiskReward { get; set; }    // TP1 ≥ 2 * SL
    public decimal MinNotionalUsd { get; set; }   
    /// <summary>Минимальный процент → если позиция получается слишком маленькой</summary>
    public decimal MinNotionalGuardPercent { get; set; } // 0.3% капитала
 
    // =====================================================
    // STARTUP / BOOTSTRAP OPTIONS
    // =====================================================

    /// <summary>
    /// Max symbols to subscribe on cold start (before engine is warm).
    /// Prevents WS / warmup storm.
    /// </summary>
    public int StartupSubscriptionCap { get; set; } = 8;
    public bool EnableExecution { get; set; } = true;
} 