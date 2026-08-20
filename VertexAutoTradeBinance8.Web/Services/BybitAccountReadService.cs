using Bybit.Net.Enums;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// Read-only Bybit linear positions & open orders for /market UI.
/// Does not place orders — execution stays in Engine BybitOrderExecutor.
/// </summary>
public sealed class BybitAccountReadService
{
    private readonly BybitClientFactory _factory;
    private readonly IOptionsMonitor<ExchangeRuntimeOptions> _ex;
    private readonly ILogger<BybitAccountReadService> _log;

    public BybitAccountReadService(
        BybitClientFactory factory,
        IOptionsMonitor<ExchangeRuntimeOptions> ex,
        ILogger<BybitAccountReadService> log)
    {
        _factory = factory;
        _ex = ex;
        _log = log;
    }

    public bool IsEnabled => _ex.CurrentValue.IsBybitActive && _factory.HasCredentials();

    public async Task<List<BybitPositionDto>> GetOpenPositionsAsync(CancellationToken ct = default)
    {
        var list = new List<BybitPositionDto>();
        if (!IsEnabled) return list;

        try
        {
            var client = _factory.TryCreateRestClient();
            if (client == null) return list;

            // V5 linear positions (USDT settle)
            var res = await client.V5Api.Trading.GetPositionsAsync(
                Category.Linear,
                settleAsset: "USDT",
                ct: ct).ConfigureAwait(false);

            if (!res.Success || res.Data?.List == null)
            {
                _log.LogDebug("[BYBIT-UI] GetPositions failed: {err}", res.Error?.Message);
                return list;
            }

            foreach (var p in res.Data.List)
            {
                var qty = p.Quantity;
                if (qty == 0) continue;

                var side = string.Equals(p.Side?.ToString(), "Sell", StringComparison.OrdinalIgnoreCase)
                           || qty < 0
                    ? "SHORT" : "LONG";
                qty = Math.Abs(qty);
                var entry = p.AveragePrice > 0 ? p.AveragePrice : p.MarkPrice;
                var mark = p.MarkPrice > 0 ? p.MarkPrice : entry;
                var lev = (int)Math.Max(1, p.Leverage);
                var size = qty * entry;
                var margin = lev > 0 ? size / lev : size;
                var pnl = p.UnrealizedPnl;
                var roi = margin > 0 ? pnl / margin : 0m;

                list.Add(new BybitPositionDto(
                    Symbol: (p.Symbol ?? "").ToUpperInvariant(),
                    Side: side,
                    Leverage: lev,
                    Qty: qty,
                    EntryPrice: entry,
                    MarkPrice: mark,
                    LiqPrice: p.LiquidationPrice,
                    Margin: margin,
                    Pnl: pnl,
                    Roi: roi));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[BYBIT-UI] GetOpenPositions failed");
        }

        return list;
    }

    public async Task<List<BybitOrderDto>> GetOpenOrdersAsync(CancellationToken ct = default)
    {
        var list = new List<BybitOrderDto>();
        if (!IsEnabled) return list;

        try
        {
            var client = _factory.TryCreateRestClient();
            if (client == null) return list;

            var res = await client.V5Api.Trading.GetOrdersAsync(
                Category.Linear,
                settleAsset: "USDT",
                openOnly: true,
                limit: 50,
                ct: ct).ConfigureAwait(false);

            if (!res.Success || res.Data?.List == null)
            {
                _log.LogDebug("[BYBIT-UI] GetOrders failed: {err}", res.Error?.Message);
                return list;
            }

            foreach (var o in res.Data.List)
            {
                list.Add(new BybitOrderDto(
                    OrderId: long.TryParse(o.OrderId, out var id) ? id : 0,
                    Symbol: (o.Symbol ?? "").ToUpperInvariant(),
                    Side: o.Side?.ToString() ?? "",
                    Type: o.OrderType?.ToString() ?? o.StopOrderType?.ToString() ?? "ORDER",
                    Price: o.Price > 0 ? o.Price : (o.TriggerPrice > 0 ? o.TriggerPrice : 0),
                    Qty: o.Quantity,
                    ReduceOnly: o.ReduceOnly,
                    IsAlgo: !string.IsNullOrEmpty(o.StopOrderType?.ToString())));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[BYBIT-UI] GetOpenOrders failed");
        }

        return list;
    }
}

public sealed record BybitPositionDto(
    string Symbol, string Side, int Leverage, decimal Qty,
    decimal EntryPrice, decimal MarkPrice, decimal LiqPrice,
    decimal Margin, decimal Pnl, decimal Roi);

public sealed record BybitOrderDto(
    long OrderId, string Symbol, string Side, string Type,
    decimal Price, decimal Qty, bool ReduceOnly, bool IsAlgo);
