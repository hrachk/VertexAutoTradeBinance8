namespace VertexAutoTradeBinance8.Configuration;

public class TradingOptions
{
    public int TimeframeMinutes { get; set; } = 15;
    public double RiskPerTrade { get; set; } = 0.01; // доля депозита riska
    public double Deposit { get; set; } = 100.0;    // размер депозита для расчёта
    public double MinQuantity { get; set; } = 0.001;
    public int CooldownSeconds { get; set; } = 120;

    /// <summary>
    /// Минимальная пауза после close перед ЛЮБЫМ новым входом по символу (минуты).
    /// Короткий «остыть» — не слепой блок на часы. Default 45m.
    /// </summary>
    public int PostCloseCooldownMinutes { get; set; } = 45;

    /// <summary>
    /// Пауза на ПОВТОР ТОЙ ЖЕ стороны после close (минуты).
    /// Тот же side не входим «по инерции» — ждём смену контекста / новый анализ.
    /// Противоположный side может войти раньше (см. PostCloseCooldownMinutes).
    /// Default 240m = 4h.
    /// </summary>
    public int SameSideCooldownMinutes { get; set; } = 240;
    public int Leverage { get; set; } = 15;


    public decimal MinAtrSlMult { get; set; } = 1.25m;   // минимум SL = 1.25 ATR
    public decimal MinNotional { get; set; } = 35m;     // минимум позиция в USDT
    public decimal MinRiskReward { get; set; } = 2.0m;   // TP1 ≥ 2 * SL
    public decimal MinNotionalUsd { get; set; } = 30m;
    /// <summary>Короткий cooldown после УСПЕШНОГО ОТКРЫТИЯ (анти-дубль ордеров). Минуты.</summary>
    public int CooldownMinutes { get; set; } = 15;

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




    /// <summary>
    /// Торговые сессии: вне окон — только наблюдение/анализ, без новых входов.
    /// </summary>
    public TradingSessionsOptions TradingSessions { get; set; } = new();
}

/// <summary>Окно одной биржевой сессии (время в UTC, HH:mm).</summary>
public class SessionWindowOptions
{
    public string Name { get; set; } = "";
    /// <summary>Начало сессии UTC, формат "HH:mm" или "H:mm".</summary>
    public string StartUtc { get; set; } = "07:00";
    /// <summary>Конец сессии UTC (не включая, если End &lt; Start — через полночь).</summary>
    public string EndUtc { get; set; } = "16:00";
}

public class TradingSessionsOptions
{
    /// <summary>true = фильтр сессий включён.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Насколько раньше официального open разрешать торги (минуты).
    /// 60 = за час до London/NY open.
    /// </summary>
    public int EarlyStartMinutes { get; set; } = 60;

    /// <summary>
    /// Разрешённые сессии. По умолчанию London + NewYork (эффективный блок дня).
    /// Overlap 12:00–16 UTC автоматически внутри объединения окон.
    /// </summary>
    public List<SessionWindowOptions> Windows { get; set; } = new()
    {
        new() { Name = "London", StartUtc = "07:00", EndUtc = "16:00" },
        new() { Name = "NewYork", StartUtc = "12:00", EndUtc = "21:00" }
    };
}
