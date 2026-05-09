using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;

public class ClosedFuturesTrade
{
    public string Symbol { get; set; } = "";
    public SignalSide Side { get; set; }    // Buy / Sell
    public decimal Qty { get; set; }        // Кол-во контракта/лот
    public decimal EntryPrice { get; set; } // Цена входа
    public decimal ExitPrice { get; set; }  // Цена выхода
    public decimal RealizedPnl { get; set; } // Реализованный PnL
    public DateTime OpenTime { get; set; }  // Время открытия позиции
    public DateTime CloseTime { get; set; } // Время закрытия позиции
}
public class BinanceFuturesUsdtUserTrade
{
    public string Symbol { get; set; } = "";
    public bool Buyer { get; set; }        // true = buy (entry)
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public DateTime Time { get; set; }     // UTC
    public decimal RealizedPnl { get; set; }
    public PositionSide PositionSide { get; set; } // "LONG" / "SHORT"
    public long Id { get; set; }           // trade id
   
}

internal static class FuturesTradeAggregator
{ 
    public static IReadOnlyList<ClosedFuturesTrade> BuildClosedTrades(
        IEnumerable<BinanceFuturesUsdtUserTrade> fills)
    {
        var result = new List<ClosedFuturesTrade>();

        var groups = fills
            .GroupBy(f => (f.Symbol, f.PositionSide))
            .ToList();

        foreach (var g in groups)
        {
            decimal posQty = 0m;
            decimal entryValue = 0m;
            DateTime? openTime = null;

            foreach (var f in g.OrderBy(x => x.Time))
            {
                if (f.Quantity <= 0) continue;

                bool isEntry =
                    (g.Key.PositionSide ==  PositionSide.Long && f.Buyer) ||
                    (g.Key.PositionSide ==  PositionSide.Short&& !f.Buyer);

                bool isExit = !isEntry;

                if (isEntry)
                {
                    posQty += f.Quantity;
                    entryValue += f.Price * f.Quantity;
                    openTime ??= f.Time;
                }
                else if (isExit && posQty > 0)
                {
                    var exitQty = Math.Min(posQty, f.Quantity);
                    var entryPrice = entryValue / posQty;

                    result.Add(new ClosedFuturesTrade
                    {
                        Symbol = f.Symbol,
                        Side = g.Key.PositionSide ==  PositionSide.Long
                            ? SignalSide.Buy
                            : SignalSide.Sell,
                        Qty = exitQty,
                        EntryPrice = entryPrice,
                        ExitPrice = f.Price,
                        RealizedPnl = f.RealizedPnl,
                        OpenTime = openTime ?? f.Time,
                        CloseTime = f.Time
                    });

                    posQty -= exitQty;
                    entryValue -= entryPrice * exitQty;

                    if (posQty <= 0)
                    {
                        posQty = 0;
                        entryValue = 0;
                        openTime = null;
                    }
                }
            }
        }

        return result;
    }
}
