namespace VertexAutoTrade.MLEngine;

public sealed class TradeFeatureVector
{
    public decimal Confidence { get; init; }
    public decimal AtrPct { get; init; }
    public decimal Funding { get; init; }
    public decimal SpreadBps { get; init; }
    public int HourUtc { get; init; }
    public decimal NewsImpact { get; init; }
    public int RegimeCode { get; init; } // 0 unknown 1 trend 2 range 3 chop
    public int SideSign { get; init; } // +1 long -1 short
}
