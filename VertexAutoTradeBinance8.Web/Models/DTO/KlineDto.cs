namespace VertexAutoTradeBinance8.Web.Models;

public sealed record KlineDto(
    long OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume
);
