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
    public decimal BaseRiskPercent { get; set; } = 1.0m; // В ПРОЦЕНТАХ: 1.0 = 1% баланса на SL

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

    /// <summary>Глобальный выключатель новых входов (UI Settings). Supervisor продолжает работать.</summary>
    public bool TradingEnabled { get; set; } = true;

    /// <summary>Максимум одновременно открытых позиций (все символы). Default 4.</summary>
    public int MaxOpenPositions { get; set; } = 4;

    // ======================= v6 PRODUCTION RISK =======================

    /// <summary>
    /// Жёсткий потолок риска на сделку в % от wallet balance (после всех AI-множителей).
    /// BaseRiskPercent=1.0 означает 1% (раньше трактовалось как 100% → риск резался только cap'ом 20%).
    /// </summary>
    public decimal MaxRiskPercent { get; set; } = 1.5m;

    /// <summary>
    /// Дневной стоп: если реализованный убыток за текущие сутки UTC ≥ X% баланса —
    /// новые входы запрещены до 00:00 UTC. 0 = выключено.
    /// </summary>
    public decimal MaxDailyLossPercent { get; set; } = 4.0m;

    /// <summary>Минимальная дистанция SL в % от цены (иначе комиссии съедают R). Default 0.30%.</summary>
    public decimal MinStopDistancePercent { get; set; } = 0.30m;

    /// <summary>Максимальная дистанция SL в % от цены (защита от гигантских стопов). Default 6%.</summary>
    public decimal MaxStopDistancePercent { get; set; } = 6.0m;

    /// <summary>
    /// Если TP1 для нужного RR лежит дальше X·ATR — сигнал отклоняется
    /// (раньше TP просто растягивали до любого расстояния и RR-фильтр никогда не срабатывал).
    /// </summary>
    public decimal MaxTp1AtrMultiple { get; set; } = 5.0m;

    /// <summary>Разрешить входы по сигналам 1m (по умолчанию нет — шум + комиссии).</summary>
    public bool AllowOneMinuteEntries { get; set; } = false;

    /// <summary>Сколько секунд ждать исполнения лимитного входа.</summary>
    public int EntryFillTimeoutSeconds { get; set; } = 45;

    /// <summary>После исполнения TP1 перевести SL в безубыток (+комиссии).</summary>
    public bool BreakEvenAfterTp1 { get; set; } = true;

    /// <summary>После исполнения TP2 подтянуть SL на уровень TP1.</summary>
    public bool LockProfitAfterTp2 { get; set; } = true;

    /// <summary>Буфер безубытка над входом, % от цены (комиссии round-trip ≈ 0.08–0.10%).</summary>
    public decimal BreakEvenBufferPercent { get; set; } = 0.12m;

    /// <summary>
    /// Если minNotional биржи требует позицию, риск которой больше целевого в X раз — пропуск.
    /// Раньше позиция «бустилась» до minNotional×1.4 без оглядки на риск.
    /// </summary>
    public decimal MaxRiskOvershootForMinNotional { get; set; } = 1.5m;

    /// <summary>Распределение объёма по TP1/TP2/TP3 (по умолчанию 40/30/30).
    /// Default пустой: ConfigurationBinder ДОПИСЫВАЕТ элементы к непустому массиву по умолчанию.</summary>
    public decimal[] TpSplit { get; set; } = Array.Empty<decimal>();

    public decimal[] EffectiveTpSplit =>
        TpSplit is { Length: >= 3 } && TpSplit.Take(3).Sum() > 0 ? TpSplit.Take(3).ToArray() : new[] { 0.40m, 0.30m, 0.30m };
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
    /// Суббота/воскресенье UTC: не открывать новые позиции (weekend chop).
    /// Supervisor / SL-TP на уже открытых продолжают работать.
    /// </summary>
    public bool BlockWeekends { get; set; } = false;

    /// <summary>
    /// Разрешённые сессии. По умолчанию London + NewYork (эффективный блок дня).
    /// Overlap 12:00–16 UTC автоматически внутри объединения окон.
    /// </summary>
    public List<SessionWindowOptions> Windows { get; set; } = new(); // пусто → DefaultWindows (binder дописывает к непустому default)

    public static List<SessionWindowOptions> DefaultWindows => new()
    {
        new() { Name = "London", StartUtc = "07:00", EndUtc = "16:00" },
        new() { Name = "NewYork", StartUtc = "12:00", EndUtc = "21:00" }
    };

    public List<SessionWindowOptions> EffectiveWindows => Windows is { Count: > 0 } ? Windows : DefaultWindows;
}
