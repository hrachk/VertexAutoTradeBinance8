namespace VertexAutoTrade.Core.Market;

public enum MarketRegimeKind
{
    Unknown = 0,
    Trend = 1,
    Range = 2,
    HighVolatilityChop = 3
}

public enum TradeBias
{
    Neutral = 0,
    LongOnly = 1,
    ShortOnly = 2,
    Flat = 3 // no new entries
}
