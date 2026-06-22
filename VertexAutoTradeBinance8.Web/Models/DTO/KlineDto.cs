namespace VertexAutoTradeBinance8.Web.Models;

public sealed record KlineDto(
    long OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume
)
{
    // The portion of Volume that was bought by takers (market buys
    // hitting the ask) — Binance's own kline data already includes
    // this directly, no extra API call needed. Sell-side volume is
    // simply Volume - TakerBuyBaseVolume. Non-positional with a
    // default so every existing call site constructing KlineDto with
    // the original 6 positional args continues to compile unchanged.
    public decimal? TakerBuyBaseVolume { get; init; }
}
