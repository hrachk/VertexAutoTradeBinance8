namespace VertexAutoTradeBinance8.Models;

/// <summary>
/// На какой стадии воронки закончилась жизнь сигнала.
/// Порядок членов = порядок проверок в TradingWorker.ProcessSymbol,
/// поэтому по номеру сразу видно, как далеко дошёл сигнал.
/// </summary>
public enum DecisionStage
{
    Data = 0,          // не хватило свечей / ошибка загрузки
    Strategy = 1,      // стратегия не нашла сетап
    Cooldown = 2,      // символ на паузе после предыдущей сделки
    AiGate = 3,        // AI не разрешил
    RiskGate = 4,      // AI обнулил риск-множитель
    Liquidity = 5,     // страж ликвидности заблокировал вход
    Levels = 6,        // не удалось построить корректные SL/TP
    Sizing = 7,        // RiskManager не смог собрать объём
    Execution = 8,     // ордер отклонён биржей
    Entered = 9        // позиция открыта
}

public enum DecisionOutcome
{
    Rejected = 0,
    Entered = 1,
    Failed = 2
}

/// <summary>
/// Одна запись = одна оценка одного сигнала, от начала до конца.
///
/// ЗАЧЕМ ЭТО ВМЕСТО ДВУХ ЖУРНАЛОВ
/// Раньше история была разорвана: executed_signals.json знал только про
/// исполненные, а missed_trades.json писался ИСКЛЮЧИТЕЛЬНО из RiskManager,
/// то есть покрывал только отказы по объёму и балансу. Отказы AI, кулдаун,
/// ликвидность, отсутствие сетапа — примерно две трети всех решений — не
/// попадали никуда, кроме таблицы в консоли, и исчезали вместе с процессом.
/// Ответить на вопрос «почему бот не вошёл» по этим файлам было нельзя.
///
/// Здесь фиксируется каждое решение с полным контекстом расчёта, поэтому
/// воронку можно посчитать, а конкретный отказ — разобрать по числам.
/// </summary>
public class TradeDecision
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;

    public string Symbol { get; set; } = "";
    public string? Timeframe { get; set; }
    public SignalSide Side { get; set; }

    public DecisionStage Stage { get; set; }
    public DecisionOutcome Outcome { get; set; }

    /// <summary>Машинный код причины: AiBlocked, MinNotional, NoRoomToTarget…</summary>
    public string Reason { get; set; } = "";

    /// <summary>Человеческое объяснение — то, что читает оператор.</summary>
    public string Explanation { get; set; } = "";

    // ------------------------------------------------------------- Уровни
    public decimal EntryPrice { get; set; }
    public decimal StopLoss { get; set; }
    public List<decimal> TakeProfits { get; set; } = new();

    /// <summary>Каким методом построен стоп: Structure / AtrFloor / SignalFallback.</summary>
    public string? StopMethod { get; set; }

    /// <summary>Уровень структуры (свинг), от которого отталкивался стоп.</summary>
    public decimal? StructureLevel { get; set; }

    public decimal? Atr { get; set; }

    /// <summary>Расстояние до стопа в ATR — сравнимо между инструментами.</summary>
    public decimal? StopAtrMultiple { get; set; }

    /// <summary>Расстояние до стопа в процентах от входа.</summary>
    public decimal? StopPercent { get; set; }

    /// <summary>R:R по первому тейку.</summary>
    public decimal? RiskReward { get; set; }

    // ------------------------------------------------------------- Объём
    public decimal? Quantity { get; set; }
    public decimal? Notional { get; set; }
    public decimal? RiskUsdt { get; set; }
    public decimal? FreeBalance { get; set; }
    public decimal? RequiredMinNotional { get; set; }
    public decimal? Leverage { get; set; }

    // ------------------------------------------------------------- Рынок
    public string? Regime { get; set; }
    public string? SmartRegime { get; set; }
    public int? Confidence { get; set; }
    public int? Score { get; set; }
    public decimal? Slope { get; set; }
    public decimal? Volatility { get; set; }

    // ------------------------------------------------------------- Итог
    /// <summary>Идентификатор ордера, если вход состоялся.</summary>
    public long? OrderId { get; set; }

    /// <summary>Фактическая цена входа — она почти всегда отличается от плановой.</summary>
    public decimal? FilledPrice { get; set; }

    /// <summary>Стоп, реально принятый биржей.</summary>
    public decimal? PlacedStop { get; set; }

    public bool EnteredPosition => Outcome == DecisionOutcome.Entered;
}
