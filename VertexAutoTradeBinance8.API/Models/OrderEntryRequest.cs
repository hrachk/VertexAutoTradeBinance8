using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.API.Models;

public class OrderEntryRequest
{
    public string Symbol { get; set; } = string.Empty;
    public SignalSide Side { get; set; }

    public decimal EntryPrice { get; set; }
    public decimal StopLoss { get; set; }
    public decimal Leverage { get; set; }

    /// <summary>
    /// Пользовательский множитель риска (1 = стандартный риск, 0.5 = половина и т.д.)
    /// </summary>
    public decimal RiskMultiplier { get; set; } = 1m;
}
