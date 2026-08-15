using Binance.Net.Enums;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.Web.Services;

public class OpenOrderRow
{
    public string Symbol { get; set; } = "";
    public long OrderId { get; set; }
    public string Side { get; set; } = "";
    public string PositionSide { get; set; } = "";
    public string Type { get; set; } = "";
    public string Status { get; set; } = "";
    public decimal Price { get; set; }
    public decimal StopPrice { get; set; }
    public decimal Quantity { get; set; }
    public decimal Filled { get; set; }
    public bool ReduceOnly { get; set; }
    public string WorkingType { get; set; } = "";
    public DateTime CreateTime { get; set; }
    public string Kind { get; set; } = "ORDER"; // ORDER | COND
}

public class OpenOrdersUiService
{
    private readonly BinanceClientFactory _factory;
    private readonly ILogger<OpenOrdersUiService> _logger;
    private readonly object _lock = new();
    private List<OpenOrderRow> _cache = new();
    private DateTime _lastFetchUtc = DateTime.MinValue;
    private string? _lastError;
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(4);

    public OpenOrdersUiService(BinanceClientFactory factory, ILogger<OpenOrdersUiService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public string? LastError => _lastError;
    public DateTime LastFetchUtc => _lastFetchUtc;

    public async Task<List<OpenOrderRow>> GetOpenOrdersAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _lastFetchUtc < MinInterval && _cache.Count >= 0 && _lastError == null)
                return _cache.ToList();
        }

        try
        {
            using var client = _factory.CreateRestClient();
            var list = new List<OpenOrderRow>();

            // All open futures orders (includes STOP / TP reduce-only on many accounts)
            var open = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(ct: ct);
            if (open.Success && open.Data != null)
            {
                foreach (var o in open.Data)
                {
                    list.Add(new OpenOrderRow
                    {
                        Symbol = o.Symbol,
                        OrderId = o.Id,
                        Side = o.Side.ToString(),
                        PositionSide = o.PositionSide.ToString(),
                        Type = o.Type.ToString(),
                        Status = o.Status.ToString(),
                        Price = o.Price,
                        StopPrice = o.StopPrice,
                        Quantity = o.Quantity,
                        Filled = o.QuantityFilled,
                        ReduceOnly = o.ReduceOnly,
                        WorkingType = o.WorkingType.ToString(),
                        CreateTime = o.CreateTime,
                        Kind = IsProtective(o.Type.ToString()) ? "PROTECT" : "ORDER"
                    });
                }
            }
            else if (!open.Success)
            {
                _lastError = open.Error?.Message ?? "GetOpenOrders failed";
                _logger.LogWarning("[ORDERS-UI] GetOpenOrders: {err}", _lastError);
            }

            list = list
                .OrderBy(x => x.Symbol)
                .ThenByDescending(x => x.CreateTime)
                .ToList();

            lock (_lock)
            {
                _cache = list;
                _lastFetchUtc = DateTime.UtcNow;
                if (open.Success) _lastError = null;
            }

            return list;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogWarning(ex, "[ORDERS-UI] fetch error");
            lock (_lock) { return _cache.ToList(); }
        }
    }

    private static bool IsProtective(string type)
    {
        var t = type.ToUpperInvariant();
        return t.Contains("STOP") || t.Contains("TAKE_PROFIT") || t.Contains("TRAILING");
    }
}
