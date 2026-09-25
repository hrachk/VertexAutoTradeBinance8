namespace VertexAutoTrade.RiskEngine.Options;

public sealed class AntiTiltOptions
{
    /// <summary>Consecutive SL_STRATEGY_FAIL (or equivalent) before cool-off.</summary>
    public int ConsecutiveStrategyFailsToCoolOff { get; set; } = 3;

    /// <summary>Cool-off duration after consecutive strategy fails.</summary>
    public int CoolOffHours { get; set; } = 4;

    /// <summary>Rolling window of closed trades for avg-R tracker.</summary>
    public int RollingWindowTrades { get; set; } = 20;

    /// <summary>If rolling avg R is below this, risk is halved.</summary>
    public decimal AvgRRiskCutThreshold { get; set; } = 0.20m;

    /// <summary>Multiplier applied to base risk when avg R is weak (e.g. 0.5 = half size).</summary>
    public decimal WeakAvgRRiskMultiplier { get; set; } = 0.5m;

    /// <summary>Close reasons counted as strategy-fail for consecutive streak.</summary>
    public string[] StrategyFailReasonTokens { get; set; } =
    {
        "SL_STRATEGY_FAIL",
        "STRATEGY_FAIL",
        "SL"
    };
}
