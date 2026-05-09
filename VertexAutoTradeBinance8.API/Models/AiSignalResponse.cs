using System;
using System.Collections.Generic;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.API.Models;

public class AiSignalResponse
{
    public string Symbol { get; set; } = string.Empty;
    public SignalSide Side { get; set; }

    public decimal EntryPrice { get; set; }
    public decimal StopLoss { get; set; }

    public List<decimal> TakeProfits { get; set; } = new();

    public string Timeframe { get; set; } = string.Empty;
    public DateTime Time { get; set; }

    // Поля «для UI», сейчас заполним базово, позже расширим
    public decimal? Atr { get; set; }
    public string? Strategy { get; set; }
    public string? Quality { get; set; }
}
