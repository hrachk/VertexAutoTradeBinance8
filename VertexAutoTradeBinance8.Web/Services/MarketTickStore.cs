using System.Collections.Concurrent;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// Data-plane for live market prices (exchange-terminal pattern).
/// Every tick is stored; UI never needs to "keep up" with every paint —
/// chart/JS reads the latest value; Blazor re-renders only on structure change.
/// </summary>
public sealed class MarketTickStore
{
    readonly ConcurrentDictionary<string, decimal> _last = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, long> _lastMs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Always updates — never drops a price (full live fidelity).</summary>
    public void SetLast(string symbol, decimal price)
    {
        if (string.IsNullOrEmpty(symbol) || price <= 0) return;
        _last[symbol] = price;
        _lastMs[symbol] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public bool TryGetLast(string symbol, out decimal price) =>
        _last.TryGetValue(symbol ?? "", out price);

    public decimal GetLastOrDefault(string symbol, decimal fallback = 0m) =>
        TryGetLast(symbol, out var p) ? p : fallback;

    public IReadOnlyDictionary<string, decimal> Snapshot() => _last;
}
