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
    public decimal? LiquidityScore { get; set; }
    public bool LiquiditySoftWarning { get; set; }
    public decimal SizeMultiplier { get; set; } = 1m;
    public string? LiquidityDetails { get; set; }

    public string Symbol { get; set; } = string.Empty;
    public SignalSide Side { get; set; }

    public decimal EntryPrice { get; set; }
    public decimal StopLoss { get; set; }

    /// <summary>
    /// Optional entry-zone bounds, communicating "anywhere in this range
    /// is an acceptable entry" rather than one precise price. EntryPrice
    /// remains the primary execution price everywhere unchanged — these
    /// are purely additive/informational (e.g. for display, or for a
    /// future scale-in feature) and default to EntryPrice on both ends
    /// when unset, so nothing reading only EntryPrice needs to change.
    /// </summary>
    public decimal? EntryRangeLow { get; set; }
    public decimal? EntryRangeHigh { get; set; }

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

    public decimal SafetyRiskMultiplier { get; set; } = 1m;
    public bool HighTfSafetyMode { get; set; } = false;


    /// <summary>Флаг: сигнал создан из ручной позиции пользователя.</summary>
    public bool IsManual { get; set; }

    public string? RejectReason { get; set; }

    /// <summary>Оценка AI (0..1), если нужно отделять "одобренные" сигналы от остальных.</summary>
    public decimal? AiQuality { get; set; }
   
    public decimal? Leverage { get; set; } 

    /// <summary>
    /// Уверенность сигнала (0..1), вычисляется StrategyEngine (SmartRegime.Confidence)
    /// Используется для динамического управления (Early TP / BE / Risk)
    /// </summary>
    public decimal? Confidence { get; set; }
    public bool ForceFullExit { get; set; } = false;
    public decimal? TimeStopBars { get; set; }

    // ── Price Action fields (added for professional strategy) ────────
    /// <summary>Market structure: HH/HL (bullish) or LH/LL (bearish)</summary>
    public string? MarketStructure { get; set; }

    /// <summary>Detected chart pattern: SYM TRIANGLE, PARALLEL RANGE, etc.</summary>
    public string? PatternLabel { get; set; }

    /// <summary>Pattern confidence 0-100 (like JonyDong "99")</summary>
    public int PatternConfidence { get; set; }

    /// <summary>MOMO score 0-100: RSI×0.40 + ROC×0.35 + MACD×0.25</summary>
    public decimal MomoScore { get; set; }

    /// <summary>Volume as multiple of 20-bar average (0.3x, 1.5x, etc.)</summary>
    public decimal VolumeRatio { get; set; } = 1m;

    /// <summary>VWAP position: "ABV", "BLW", "AT"</summary>
    public string? VwapPosition { get; set; }

    /// <summary>Nearest support level from S/R analysis</summary>
    public decimal? NearestSupport { get; set; }

    /// <summary>Nearest resistance level from S/R analysis</summary>
    public decimal? NearestResistance { get; set; }

    /// <summary>R-multiple at TP1 (should be ≥ 1.0)</summary>
    public decimal RRatio { get; set; } = 0m;

    /// <summary>Action hint from pattern: "WAIT BREAK DOWN", "WAIT BREAK UP"</summary>
    public string? PatternAction { get; set; }
   

    public   decimal GetTpPart(int index)
    {
        return index switch
        {
            0 => 0.40m,
            1 => 0.35m,
            2 => 0.25m,
            _ => 0m
        };
    }

    public void CopyFrom(TradeSignal src)
    {
        if (src == null) return;

        EntryPrice = src.EntryPrice;
        StopLoss = src.StopLoss;

        var tps = src.TakeProfits;
        TakeProfits = tps;

        Confidence = src.Confidence;
        Reason = src.Reason;
        Atr = src.Atr;
        Leverage = src.Leverage;
        SafetyRiskMultiplier = src.SafetyRiskMultiplier;
        IsSuperSignal = src.IsSuperSignal;

    }

}

