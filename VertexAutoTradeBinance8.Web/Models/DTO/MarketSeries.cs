namespace VertexAutoTradeBinance8.Web.Models;

public sealed record MarketSeries(
    string Symbol,
    string Timeframe,
    List<KlineDto> Klines
);
