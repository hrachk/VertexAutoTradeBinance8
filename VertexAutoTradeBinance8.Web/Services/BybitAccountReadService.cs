using System.Reflection;
using Bybit.Net.Enums;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// Read-only Bybit linear positions, open orders, closed PnL for /market UI tabs.
/// Property access is defensive — Bybit.Net field names vary by package version.
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
                var qty = Dec(p, "Quantity", "Size", "ClosedSize");
                if (qty == 0) continue;

                var sideStr = Str(p, "Side");
                var side = sideStr.Contains("Sell", StringComparison.OrdinalIgnoreCase) || qty < 0
                    ? "SHORT" : "LONG";
                qty = Math.Abs(qty);
                var entry = Dec(p, "AveragePrice", "AvgPrice", "EntryPrice");
                var mark = Dec(p, "MarkPrice");
                if (entry <= 0) entry = mark;
                if (mark <= 0) mark = entry;
                var lev = (int)Math.Max(1m, Dec(p, "Leverage"));
                if (lev < 1) lev = 1;
                var size = qty * entry;
                var margin = Dec(p, "PositionBalance", "PositionIM", "PositionMargin");
                if (margin <= 0 && lev > 0) margin = size / lev;
                var pnl = Dec(p, "UnrealizedPnl", "UnrealisedPnl");
                var liq = Dec(p, "LiquidationPrice", "LiqPrice");
                var roi = margin > 0 ? pnl / margin : 0m;

                list.Add(new BybitPositionDto(
                    Symbol: Str(p, "Symbol").ToUpperInvariant(),
                    Side: side,
                    Leverage: lev,
                    Qty: qty,
                    EntryPrice: entry,
                    MarkPrice: mark,
                    LiqPrice: liq,
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

            // Minimal overload — avoid openOnly bool vs int? mismatch across Bybit.Net versions
            var res = await client.V5Api.Trading.GetOrdersAsync(
                category: Category.Linear,
                settleAsset: "USDT",
                limit: 50,
                ct: ct).ConfigureAwait(false);

            if (!res.Success || res.Data?.List == null)
            {
                _log.LogDebug("[BYBIT-UI] GetOrders failed: {err}", res.Error?.Message);
                return list;
            }

            foreach (var o in res.Data.List)
            {
                // Prefer open / new / partially filled
                var status = Str(o, "Status", "OrderStatus").ToLowerInvariant();
                if (status.Length > 0
                    && status is not ("new" or "partiallyfilled" or "untriggered" or "created" or "active"))
                {
                    // still include reduce-only conditional if status empty/unknown
                    if (status is "filled" or "cancelled" or "canceled" or "rejected" or "deactivated")
                        continue;
                }

                var orderIdStr = Str(o, "OrderId");
                long.TryParse(orderIdStr, out var orderId);
                var price = Dec(o, "Price");
                if (price <= 0) price = Dec(o, "TriggerPrice", "StopLoss", "TakeProfit");
                var stopType = Str(o, "StopOrderType");

                list.Add(new BybitOrderDto(
                    OrderId: orderId,
                    Symbol: Str(o, "Symbol").ToUpperInvariant(),
                    Side: Str(o, "Side"),
                    Type: Str(o, "OrderType", "Type").Length > 0
                        ? Str(o, "OrderType", "Type")
                        : (stopType.Length > 0 ? stopType : "ORDER"),
                    Price: price,
                    Qty: Math.Abs(Dec(o, "Quantity", "Qty", "Size")),
                    ReduceOnly: Bool(o, "ReduceOnly"),
                    IsAlgo: stopType.Length > 0));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[BYBIT-UI] GetOpenOrders failed");
        }

        return list;
    }

    public async Task<List<BybitHistoryDto>> GetClosedHistoryAsync(CancellationToken ct = default)
    {
        var list = new List<BybitHistoryDto>();
        if (!IsEnabled) return list;

        try
        {
            var client = _factory.TryCreateRestClient();
            if (client == null) return list;

            var res = await client.V5Api.Trading.GetClosedProfitLossAsync(
                Category.Linear,
                limit: 50,
                ct: ct).ConfigureAwait(false);

            if (!res.Success || res.Data?.List == null)
            {
                _log.LogDebug("[BYBIT-UI] GetClosedProfitLoss failed: {err}", res.Error?.Message);
                return list;
            }

            foreach (var x in res.Data.List)
            {
                list.Add(new BybitHistoryDto(
                    Symbol: Str(x, "Symbol").ToUpperInvariant(),
                    Side: Str(x, "Side"),
                    Qty: Math.Abs(Dec(x, "ClosedSize", "Qty", "Quantity", "Size")),
                    AvgEntryPrice: Dec(x, "AvgEntryPrice", "EntryPrice", "AvgEntry"),
                    AvgExitPrice: Dec(x, "AvgExitPrice", "ExitPrice", "AvgExit", "OrderPrice"),
                    RealizedPnl: Dec(x, "ClosedPnl", "ClosedPnL", "RealisedPnl", "RealizedPnl"),
                    ClosedTimeUtc: Time(x, "UpdatedTime", "CreatedTime", "ExecTime", "OpenTime")));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[BYBIT-UI] GetClosedHistory failed");
        }

        return list.OrderByDescending(h => h.ClosedTimeUtc).ToList();
    }

    // ── reflection helpers (Bybit.Net property names differ by version) ──
    static object? Prop(object obj, string name)
    {
        var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return p?.GetValue(obj);
    }

    static string Str(object obj, params string[] names)
    {
        foreach (var n in names)
        {
            var v = Prop(obj, n);
            if (v == null) continue;
            var s = v.ToString();
            if (!string.IsNullOrWhiteSpace(s)) return s!;
        }
        return "";
    }

    static decimal Dec(object obj, params string[] names)
    {
        foreach (var n in names)
        {
            var v = Prop(obj, n);
            if (v == null) continue;
            try
            {
                if (v is decimal d) return d;
                if (v is double db) return (decimal)db;
                if (v is float f) return (decimal)f;
                if (v is int i) return i;
                if (v is long l) return l;
                if (v is string s && decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
                // Nullable<T>
                var t = v.GetType();
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))
                {
                    if (v != null) return Convert.ToDecimal(v);
                }
                return Convert.ToDecimal(v);
            }
            catch { /* try next name */ }
        }
        return 0m;
    }

    static bool Bool(object obj, params string[] names)
    {
        foreach (var n in names)
        {
            var v = Prop(obj, n);
            if (v is bool b) return b;
        }
        return false;
    }

    static DateTime Time(object obj, params string[] names)
    {
        foreach (var n in names)
        {
            var v = Prop(obj, n);
            if (v == null) continue;
            try
            {
                if (v is DateTime dt) return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                if (v is DateTimeOffset dto) return dto.UtcDateTime;
                if (v is long ms)
                {
                    if (ms > 1_000_000_000_000) // ms
                        return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                    return DateTimeOffset.FromUnixTimeSeconds(ms).UtcDateTime;
                }
            }
            catch { }
        }
        return DateTime.UtcNow;
    }
}

public sealed record BybitPositionDto(
    string Symbol, string Side, int Leverage, decimal Qty,
    decimal EntryPrice, decimal MarkPrice, decimal LiqPrice,
    decimal Margin, decimal Pnl, decimal Roi);

public sealed record BybitOrderDto(
    long OrderId, string Symbol, string Side, string Type,
    decimal Price, decimal Qty, bool ReduceOnly, bool IsAlgo);

public sealed record BybitHistoryDto(
    string Symbol, string Side, decimal Qty,
    decimal AvgEntryPrice, decimal AvgExitPrice,
    decimal RealizedPnl, DateTime ClosedTimeUtc);
