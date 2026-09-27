namespace VertexAutoTrade.Core.Risk;

/// <summary>Institutional R:R gate — TP1 must be ≥ MinRr × risk distance.</summary>
public static class RiskRewardCalculator
{
    public const decimal DefaultMinTp1Rr = 1.50m;

    public static decimal Tp1RewardRiskRatio(decimal entry, decimal stopLoss, decimal tp1)
    {
        decimal risk = Math.Abs(entry - stopLoss);
        if (risk <= 0) return 0m;
        return Math.Abs(tp1 - entry) / risk;
    }

    public static bool MeetsMinTp1Rr(decimal entry, decimal stopLoss, decimal tp1, decimal minRr = DefaultMinTp1Rr)
        => Tp1RewardRiskRatio(entry, stopLoss, tp1) >= minRr * 0.999m;

    public static bool IsSlWithinPctCap(decimal entry, decimal stopLoss, decimal maxRiskPct = 0.022m)
    {
        if (entry <= 0) return false;
        return Math.Abs(entry - stopLoss) / entry <= maxRiskPct;
    }
}
