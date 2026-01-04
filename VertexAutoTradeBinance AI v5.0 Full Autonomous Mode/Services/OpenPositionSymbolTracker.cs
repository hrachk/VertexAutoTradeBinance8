using System.Collections.Concurrent;

namespace VertexAutoTradeBinance8.Services;

/// <summary>
/// In-memory tracker of open position symbols.
/// - Thread-safe
/// - Supports multiple sides (Long/Short) and partial closes
/// - Designed for SymbolRegistry "PinnedByPositions" + instant drop on full close.
/// </summary>
public sealed class OpenPositionSymbolTracker : IOpenPositionSymbolSource
{
    // symbol -> open legs count (e.g. long+short = 2). We store count to be safe for hedge.
    private readonly ConcurrentDictionary<string, int> _open = new(StringComparer.OrdinalIgnoreCase);

    public void MarkOpen(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        symbol = symbol.Trim().ToUpperInvariant();

        _open.AddOrUpdate(symbol, 1, (_, prev) => prev + 1);
    }

    public void MarkClosed(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        symbol = symbol.Trim().ToUpperInvariant();

        _open.AddOrUpdate(symbol, 0, (_, prev) => Math.Max(0, prev - 1));

        if (_open.TryGetValue(symbol, out var v) && v <= 0)
            _open.TryRemove(symbol, out _);
    }

    public void MarkLegState(string symbol, bool isOpen)
    {
        if (isOpen) MarkOpen(symbol);
        else MarkClosed(symbol);
    }

    public Task<IReadOnlyList<string>> GetOpenPositionSymbolsAsync(CancellationToken ct)
    {
        // Snapshot enumeration is safe for ConcurrentDictionary.
        var list = _open.Keys.ToList();
        return Task.FromResult((IReadOnlyList<string>)list);
    }
}
