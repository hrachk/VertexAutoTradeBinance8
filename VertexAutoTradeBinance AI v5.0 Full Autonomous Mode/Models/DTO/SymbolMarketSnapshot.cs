namespace VertexAutoTradeBinance8.Models;

public class SymbolMarketSnapshot
{
    public string Symbol { get; init; } = "";
    public decimal QuoteVolume24h { get; init; }
    public decimal LastPrice { get; init; }
    public decimal PriceChangePercent { get; init; }
    public decimal Score { get; set; }
}

