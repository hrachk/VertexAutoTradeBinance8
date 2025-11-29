using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Models;

public enum SignalSide
{
    None = 0,
    Buy = 1,
    Sell = 2
}

public class TradeSignal
{
    public string Symbol { get; set; } = string.Empty;
    public SignalSide Side { get; set; }

    public decimal EntryPrice { get; set; }
    public decimal StopLoss { get; set; }

    /// <summary>Один базовый TP – для совместимости</summary>
    public decimal? TakeProfit { get; set; }

    /// <summary>Мульти-TP: TP1/TP2/TP3...</summary>
    public List<decimal> TakeProfits { get; set; } = new();

    /// <summary>ATR(14) на момент сигнала</summary>
    public decimal? Atr { get; set; }

    /// <summary>Таймфрейм сигнала (например "OneMinute")</summary>
    public string? Timeframe { get; set; }

    public DateTime Time { get; set; }

    /// <summary>
    /// Причина/тип сигнала: "PULLBACK_EMA21", "BREAKOUT_RETEST_LONG" и т.д.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Супер-сигнал (разрешаем усреднение/повторный вход при открытой позе)
    /// </summary>
    public bool IsSuperSignal { get; set; }

    public decimal GetTpPart(int index)
    {
        // возвращает часть позиции для TP1 / TP2 / TP3
        if (index == 0) return 0.40m;
        if (index == 1) return 0.35m;
        if (index == 2) return 0.25m;
        return 0m;
    }
}
