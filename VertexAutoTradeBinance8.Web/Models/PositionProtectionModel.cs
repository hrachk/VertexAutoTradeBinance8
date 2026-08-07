namespace VertexAutoTradeBinance8.Web.Models;

/// <summary>
/// Открытая позиция + факт наличия защитных ордеров.
///
/// Существует ровно ради одного вопроса, который консоль должна отвечать
/// мгновенно: есть ли стоп под каждой открытой позицией. Всё остальное
/// (PnL, плечо, ликвидация) — контекст вокруг этого ответа.
/// </summary>
public class PositionProtectionModel
{
    public string Symbol { get; set; } = "";

    /// <summary>LONG / SHORT / BOTH</summary>
    public string PositionSide { get; set; } = "";

    public decimal Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal MarkPrice { get; set; }
    public decimal UnrealizedPnl { get; set; }
    public decimal Notional { get; set; }
    public int? Leverage { get; set; }
    public decimal? LiquidationPrice { get; set; }

    /// <summary>Триггер активного STOP_MARKET / STOP по этой стороне.</summary>
    public decimal? StopPrice { get; set; }

    /// <summary>Ближайший триггер TAKE_PROFIT_MARKET по этой стороне.</summary>
    public decimal? TakeProfitPrice { get; set; }

    /// <summary>Сколько защитных стопов реально висит (дубли — это тоже проблема).</summary>
    public int StopOrderCount { get; set; }

    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;

    public bool IsLong => string.Equals(PositionSide, "LONG", StringComparison.OrdinalIgnoreCase);
    public bool HasStop => StopPrice is > 0;
    public bool HasDuplicateStops => StopOrderCount > 1;

    /// <summary>Риск до стопа в USDT (без учёта комиссий).</summary>
    public decimal? RiskUsdt =>
        StopPrice is > 0 && EntryPrice > 0
            ? Math.Abs(EntryPrice - StopPrice.Value) * Math.Abs(Quantity)
            : null;

    /// <summary>Дистанция до стопа в процентах от входа.</summary>
    public decimal? StopDistancePercent =>
        StopPrice is > 0 && EntryPrice > 0
            ? Math.Abs(EntryPrice - StopPrice.Value) / EntryPrice * 100m
            : null;
}

/// <summary>Свод по всему счёту — то, что выносится в шапку консоли.</summary>
public class ProtectionSummary
{
    public List<PositionProtectionModel> Positions { get; set; } = new();

    /// <summary>Заполняется, когда Binance недоступен или ключи не заданы.</summary>
    public string? Error { get; set; }

    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;

    public int Total => Positions.Count;
    public int Unprotected => Positions.Count(p => !p.HasStop);
    public int Duplicates => Positions.Count(p => p.HasDuplicateStops);
    public bool AllProtected => Total > 0 && Unprotected == 0;

    public decimal TotalPnl => Positions.Sum(p => p.UnrealizedPnl);
    public decimal TotalNotional => Positions.Sum(p => Math.Abs(p.Notional));

    /// <summary>Суммарный риск по позициям, под которыми стоп есть.</summary>
    public decimal KnownRisk => Positions.Where(p => p.RiskUsdt.HasValue).Sum(p => p.RiskUsdt!.Value);
}
