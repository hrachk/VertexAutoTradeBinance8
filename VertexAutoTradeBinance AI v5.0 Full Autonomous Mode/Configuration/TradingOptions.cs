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

    /// <summary>
    /// Controls whether the Engine places real orders on Binance.
    /// false = signal-only / dry-run mode: signals are generated and logged
    /// but NO orders are sent. PositionSupervisor continues to run normally
    /// (SL/BE management of already-open positions is unaffected).
    /// Toggle from Web UI: Settings → "AutoTrade — Order Execution".
    /// </summary>
    public bool EnableExecution { get; set; } = true;

    /// <summary>
    /// Maximum margin as a fraction of balance that a single position may consume.
    /// Default 0.12 = 12%. Previously hardcoded as const decimal in RiskManager.
    /// </summary>
    public decimal MaxMarginPercent { get; set; } = 0.12m;

    /// <summary>
    /// Whether PositionSupervisor is allowed to manage Take-Profit orders.
    ///
    /// true  (default) — Supervisor places Emergency TP when none exists,
    ///                    and runs partial-close / trailing logic normally.
    ///
    /// false — Supervisor leaves all TP orders untouched. Use this when
    ///         you place your own TP orders manually on Binance and do not
    ///         want the bot to interfere, cancel, or re-place them.
    ///         SL and Break-Even management continue to work normally.
    ///
    /// Toggle from Web UI: Settings → "Supervisor TP Management".
    /// Writes to Trading:SupervisorManageTP in appsettings.runtime.json.
    /// Applies within seconds, no restart needed.
    /// </summary>
    /// <summary>
    /// Symbols to always exclude from strategy analysis.
    /// Use for ETF-like tokens (SOXL, EWY, KORU) where EMA21 strategy
    /// does not apply — they generate only no_signal and waste resources.
    /// Populated from appsettings.json "SymbolBlacklist" array.
    /// </summary>
    public List<string> SymbolBlacklist { get; set; } = new();

    public bool SupervisorManageTP { get; set; } = true;
} 
