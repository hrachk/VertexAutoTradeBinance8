namespace VertexAutoTrade.Regime;

public sealed class RegimeOptions
{
    public decimal AdxTrendThreshold { get; set; } = 22m;
    public decimal AtrRatioChopThreshold { get; set; } = 1.8m;
    public decimal BbWidthChopThreshold { get; set; } = 0.04m;
    public bool BlockHighVolatilityChop { get; set; } = true;
    public decimal BtcSpikePct15m { get; set; } = 1.5m;
    public int BtcSpikePauseMinutes { get; set; } = 15;
}
