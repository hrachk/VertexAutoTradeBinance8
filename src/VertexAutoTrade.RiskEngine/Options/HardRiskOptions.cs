namespace VertexAutoTrade.RiskEngine.Options;

/// <summary>Hard risk limits evaluated BEFORE strategy execution.</summary>
public sealed class HardRiskOptions
{
    /// <summary>Max realized loss in R-units for the UTC day (e.g. 3.0 = −3R).</summary>
    public decimal MaxDailyLossR { get; set; } = 3.0m;

    /// <summary>Optional equity drawdown fraction for the day (e.g. 0.03 = 3%). 0 = disabled.</summary>
    public decimal MaxDailyLossEquityFraction { get; set; } = 0.03m;

    /// <summary>Max concurrent open positions (all symbols).</summary>
    public int MaxOpenPositions { get; set; } = 3;

    /// <summary>Hours to block new entries after hard trip (default 24).</summary>
    public int BlockHoursAfterTrip { get; set; } = 24;

    /// <summary>Base risk fraction of equity per trade before anti-tilt scaling (e.g. 0.01 = 1%).</summary>
    public decimal BaseRiskFraction { get; set; } = 0.01m;

    /// <summary>Absolute USD hard cap on notional risk (1R dollars). 0 = no USD cap.</summary>
    public decimal HardCapRiskUsd { get; set; } = 100m;
}
