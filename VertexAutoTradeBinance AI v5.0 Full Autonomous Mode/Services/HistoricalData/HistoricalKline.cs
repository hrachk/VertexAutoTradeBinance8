namespace VertexAutoTradeBinance8.Services.HistoricalData
{
    /// <summary>
    /// One OHLCV bar, stored in datadb/*.json files. Shape intentionally
    /// matches VertexAutoTradeBinance8.Web.Models.KlineDto exactly — the
    /// Web project's chart code can read these files directly without any
    /// conversion step.
    /// </summary>
    public sealed record HistoricalKline(
        long OpenTime,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        decimal Volume
    );
}
