using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services.Levels;

public sealed class LevelPlan
{
    public bool Valid { get; set; }

    /// <summary>Машинный код отказа: NoStructure, TooWide, NoRoomToTarget, LowRR.</summary>
    public string Reason { get; set; } = "";
    public string Explanation { get; set; } = "";

    public decimal Entry { get; set; }
    public decimal StopLoss { get; set; }
    public List<decimal> TakeProfits { get; set; } = new();

    public string StopMethod { get; set; } = "";
    public decimal? StructureLevel { get; set; }

    public decimal Atr { get; set; }
    public decimal RiskDistance { get; set; }
    public decimal StopAtrMultiple { get; set; }
    public decimal StopPercent { get; set; }
    public decimal RiskReward { get; set; }
}

/// <summary>
/// ============================================================================
/// МЕТОДОЛОГИЯ РАСЧЁТА SL И TP
/// ============================================================================
///
/// Главный принцип: СТОП СТАВИТСЯ ТУДА, ГДЕ ИДЕЯ СДЕЛКИ ОПРОВЕРГНУТА,
/// а не на фиксированное расстояние. Дистанция — это результат расчёта,
/// а не входной параметр.
///
/// Чем это отличается от AiStopLossOptimizer, который работал раньше:
/// тот брал уже готовый стоп от стратегии и двигал его множителями ATR
/// (GetDynamicSlAtrMult, 1.0…1.8×ATR) плюс эвристика по хвостам свечей.
/// Стоп в итоге оказывался на круглой доле волатильности — то есть ровно там,
/// где его ставит большинство, и где рынок его чаще всего собирает. Уровень,
/// который реально ломает сетап, в расчёте не участвовал вообще.
/// Вторая проблема: когда стоп получался слишком широким, оптимизатор просто
/// расширял его, а RiskManager молча уменьшал объём. Сделка с плохим R:R
/// всё равно открывалась, только меньшим размером.
///
/// ПОРЯДОК ПОСТРОЕНИЯ СТОПА
///
///   1. СТРУКТУРА. Ищем последний значимый пивот (свинг-лоу для лонга,
///      свинг-хай для шорта) в окне LookbackBars. Это цена, при которой сетап
///      объективно сломан.
///
///   2. БУФЕР ВОЛАТИЛЬНОСТИ. Отступаем за структуру на StructureBufferAtr × ATR.
///      Уровни пробивают хвостами, не закрытиями: стоп ровно на уровне
///      снимается шумом при живой идее.
///
///   3. ПОЛ ПО ШУМУ. Стоп не ближе MinStopAtr × ATR от входа. Ближе — это
///      не риск-менеджмент, а лотерея на обычном дыхании рынка.
///
///   4. ПОТОЛОК. Стоп не дальше MaxStopAtr × ATR и не дальше MaxStopPercent
///      от цены. Если структура дальше — сделка ОТКЛОНЯЕТСЯ, а не ужимается
///      по объёму. Это ключевая дисциплина, которой не было.
///
///   5. ТИК. Округление к шагу цены делает вызывающий код (у него есть фильтры).
///
/// ПОРЯДОК ПОСТРОЕНИЯ ТЕЙКОВ
///
///   Тейки считаются в R — единицах риска (1R = расстояние вход→стоп).
///   Это единственная шкала, в которой цели сравнимы между инструментами
///   и таймфреймами.
///
///   Базовая лестница TpRMultiples = 1.0R / 2.0R / 3.5R, затем каждый уровень
///   ПРИЖИМАЕТСЯ К БЛИЖАЙШЕМУ ВСТРЕЧНОМУ УРОВНЮ СТРУКТУРЫ: если по пути стоит
///   свинг-хай, цель ставится чуть перед ним, а не за ним. Фиксировать прибыль
///   перед стенкой выгоднее, чем ждать за ней.
///
///   Если ближайшая встречная структура ближе, чем MinRR × риск — идти некуда,
///   и сделка отклоняется с причиной NoRoomToTarget. Это честная замена
///   «расширим стоп и всё равно войдём».
///
/// Движок детерминирован: одни и те же свечи дают один и тот же план,
/// и каждый отказ объясним числом.
/// ============================================================================
/// </summary>
public sealed class TradeLevelEngine
{
    private readonly ILogger<TradeLevelEngine> _logger;
    private readonly LevelOptions _opt;

    public TradeLevelEngine(ILogger<TradeLevelEngine> logger, LevelOptions options)
    {
        _logger = logger;
        _opt = options;
    }

    public LevelPlan Build(
        TradeSignal signal,
        IReadOnlyList<BinanceFuturesUsdtKline> klines)
    {
        var plan = new LevelPlan { Entry = signal.EntryPrice };

        if (signal.EntryPrice <= 0)
            return Fail(plan, "NoEntryPrice", "У сигнала нет цены входа.");

        if (klines == null || klines.Count < _opt.LookbackBars + 5)
            return Fail(plan, "NotEnoughBars",
                $"Для поиска структуры нужно минимум {_opt.LookbackBars + 5} свечей, получено {klines?.Count ?? 0}.");

        var atr = signal.Atr is > 0 ? signal.Atr!.Value : Atr(klines, 14);
        if (atr <= 0)
            return Fail(plan, "NoAtr", "ATR равен нулю — волатильность посчитать не удалось.");

        plan.Atr = atr;

        bool isLong = signal.Side == SignalSide.Buy;

        // ---------------------------------------------------------- 1. Структура
        var structure = FindStructureLevel(klines, isLong, signal.EntryPrice);
        plan.StructureLevel = structure;

        decimal stop;

        if (structure is > 0)
        {
            // 2. Буфер за уровень
            var buffer = atr * _opt.StructureBufferAtr;
            stop = isLong ? structure.Value - buffer : structure.Value + buffer;
            plan.StopMethod = "Structure";
        }
        else
        {
            // Структуры в окне нет — работаем от волатильности, но честно это помечаем
            stop = isLong
                ? signal.EntryPrice - atr * _opt.FallbackStopAtr
                : signal.EntryPrice + atr * _opt.FallbackStopAtr;
            plan.StopMethod = "AtrFallback";
        }

        // ---------------------------------------------------------- 3. Пол по шуму
        var minDist = atr * _opt.MinStopAtr;
        var dist = Math.Abs(signal.EntryPrice - stop);

        if (dist < minDist)
        {
            stop = isLong ? signal.EntryPrice - minDist : signal.EntryPrice + minDist;
            dist = minDist;
            plan.StopMethod += "+NoiseFloor";
        }

        // ---------------------------------------------------------- 4. Потолок
        var maxByAtr = atr * _opt.MaxStopAtr;
        var maxByPct = signal.EntryPrice * _opt.MaxStopPercent / 100m;
        var maxDist = Math.Min(maxByAtr, maxByPct);

        if (dist > maxDist)
        {
            return Fail(plan, "StopTooWide",
                $"Уровень слома идеи в {dist / atr:0.00} ATR от входа " +
                $"({dist / signal.EntryPrice * 100m:0.00}% цены). " +
                $"Потолок {maxDist / atr:0.00} ATR. Сделка слишком дорогая — " +
                $"уменьшать объём под такой стоп значит ухудшать R:R ради самого входа.");
        }

        plan.StopLoss = stop;
        plan.RiskDistance = dist;
        plan.StopAtrMultiple = dist / atr;
        plan.StopPercent = dist / signal.EntryPrice * 100m;

        // ---------------------------------------------------------- Тейки
        var barrier = FindOpposingBarrier(klines, isLong, signal.EntryPrice);

        if (barrier is > 0)
        {
            var room = Math.Abs(barrier.Value - signal.EntryPrice);
            var roomR = room / dist;

            if (roomR < _opt.MinRiskReward)
            {
                return Fail(plan, "NoRoomToTarget",
                    $"Ближайший встречный уровень {barrier.Value:0.######} даёт всего {roomR:0.00}R " +
                    $"при минимуме {_opt.MinRiskReward:0.00}R. Пространства для движения нет.");
            }
        }

        plan.TakeProfits = BuildTargets(signal.EntryPrice, dist, isLong, barrier, atr);

        if (plan.TakeProfits.Count == 0)
            return Fail(plan, "NoTargets", "Не удалось построить ни одной цели.");

        plan.RiskReward = Math.Abs(plan.TakeProfits[0] - signal.EntryPrice) / dist;

        if (plan.RiskReward < _opt.MinRiskReward)
        {
            return Fail(plan, "LowRR",
                $"R:R по первой цели {plan.RiskReward:0.00} при минимуме {_opt.MinRiskReward:0.00}. " +
                "После прижатия цели к структуре сделка перестала окупать риск.");
        }

        plan.Valid = true;
        plan.Explanation =
            $"Стоп {plan.StopMethod} на {plan.StopAtrMultiple:0.00} ATR ({plan.StopPercent:0.00}%), " +
            $"первая цель {plan.RiskReward:0.00}R.";

        return plan;
    }

    // =====================================================================
    // Пивот: бар, у которого экстремум ниже (выше) соседей с обеих сторон.
    // Берём ПОСЛЕДНИЙ подходящий — он актуальнее, а не самый глубокий.
    // =====================================================================
    private decimal? FindStructureLevel(
        IReadOnlyList<BinanceFuturesUsdtKline> k, bool isLong, decimal entry)
    {
        int w = _opt.PivotWidth;
        int from = Math.Max(w, k.Count - _opt.LookbackBars);
        int to = k.Count - 1 - w;

        decimal? best = null;

        for (int i = to; i >= from; i--)
        {
            bool pivot = true;

            for (int j = 1; j <= w && pivot; j++)
            {
                if (isLong)
                {
                    if (k[i].LowPrice > k[i - j].LowPrice || k[i].LowPrice > k[i + j].LowPrice)
                        pivot = false;
                }
                else
                {
                    if (k[i].HighPrice < k[i - j].HighPrice || k[i].HighPrice < k[i + j].HighPrice)
                        pivot = false;
                }
            }

            if (!pivot) continue;

            var level = isLong ? k[i].LowPrice : k[i].HighPrice;

            // Структура должна быть по нужную сторону от входа
            if (isLong && level >= entry) continue;
            if (!isLong && level <= entry) continue;

            best = level;
            break;
        }

        return best;
    }

    /// <summary>Ближайший встречный уровень: свинг-хай для лонга, свинг-лоу для шорта.</summary>
    private decimal? FindOpposingBarrier(
        IReadOnlyList<BinanceFuturesUsdtKline> k, bool isLong, decimal entry)
    {
        int w = _opt.PivotWidth;
        int from = Math.Max(w, k.Count - _opt.LookbackBars);
        int to = k.Count - 1 - w;

        decimal? nearest = null;

        for (int i = to; i >= from; i--)
        {
            bool pivot = true;

            for (int j = 1; j <= w && pivot; j++)
            {
                if (isLong)
                {
                    if (k[i].HighPrice < k[i - j].HighPrice || k[i].HighPrice < k[i + j].HighPrice)
                        pivot = false;
                }
                else
                {
                    if (k[i].LowPrice > k[i - j].LowPrice || k[i].LowPrice > k[i + j].LowPrice)
                        pivot = false;
                }
            }

            if (!pivot) continue;

            var level = isLong ? k[i].HighPrice : k[i].LowPrice;

            if (isLong && level <= entry) continue;
            if (!isLong && level >= entry) continue;

            if (nearest == null ||
                Math.Abs(level - entry) < Math.Abs(nearest.Value - entry))
                nearest = level;
        }

        return nearest;
    }

    /// <summary>
    /// Лестница целей в R, каждая прижата к встречной структуре, если та мешает.
    /// </summary>
    private List<decimal> BuildTargets(
        decimal entry, decimal risk, bool isLong, decimal? barrier, decimal atr)
    {
        var result = new List<decimal>();

        foreach (var r in _opt.TpRMultiples)
        {
            var target = isLong ? entry + risk * r : entry - risk * r;

            if (barrier is > 0)
            {
                var pad = atr * _opt.BarrierPadAtr;

                // Цель за стенкой переносится перед стенкой
                if (isLong && target > barrier.Value - pad)
                    target = barrier.Value - pad;
                else if (!isLong && target < barrier.Value + pad)
                    target = barrier.Value + pad;
            }

            // Цель должна быть строго за предыдущей, иначе смысла в ней нет
            if (result.Count > 0)
            {
                var prev = result[^1];
                if (isLong && target <= prev) continue;
                if (!isLong && target >= prev) continue;
            }

            if (isLong && target <= entry) continue;
            if (!isLong && target >= entry) continue;

            result.Add(target);
        }

        return result;
    }

    private static decimal Atr(IReadOnlyList<BinanceFuturesUsdtKline> k, int period)
    {
        if (k.Count < period + 2) return 0m;

        decimal sum = 0m;
        int start = k.Count - period;

        for (int i = start; i < k.Count; i++)
        {
            var c = k[i];
            var p = k[i - 1];

            var tr = Math.Max(c.HighPrice - c.LowPrice,
                     Math.Max(Math.Abs(c.HighPrice - p.ClosePrice),
                              Math.Abs(c.LowPrice - p.ClosePrice)));
            sum += tr;
        }

        return sum / period;
    }

    private LevelPlan Fail(LevelPlan p, string reason, string explanation)
    {
        p.Valid = false;
        p.Reason = reason;
        p.Explanation = explanation;
        return p;
    }
}
