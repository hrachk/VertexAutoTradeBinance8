using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.Services.HistoricalData;

/// <summary>
/// Aggregates ALL symbol sources into one de-duplicated, ordered list
/// for HistoricalDataLoaderService to persist into datadb/.
///
/// Sources (in priority order):
///   1. HistoricalData:Symbols    — explicit hand-picked list (appsettings)
///   2. SymbolSelection:Pinned    — manually pinned trading symbols
///   3. SymbolRegistryService     — live trading universe (15 rotating symbols)
///   4. OpenPositionSymbolTracker — currently open position symbols (manual trades too)
///   5. _seenSymbols              — any symbol ever seen since last restart
///                                  (manually traded, appeared in universe, etc.)
///
/// WHY THIS IS SAFE (no Binance spam):
///   - Feed is read-only: we NEVER call Binance here.
///   - HistoricalDataLoaderService already gates ALL REST calls through
///     a single SemaphoreSlim(_restGate, 1, 1) with 250ms delay between calls.
///   - Incremental fetch: after first pull, only bars since last known
///     OpenTime are fetched (a handful of new bars per symbol per cycle).
///   - For N=50 symbols × 2 timeframes × 300s interval:
///     worst case = 100 calls / 5 min = 20 req/min — well within
///     Binance Futures REST limit (2400 req/min weight budget).
///   - _seenSymbols is bounded by MaxSeenSymbols to prevent unbounded growth.
///
/// THREAD SAFETY:
///   GetSymbols() is called from HistoricalDataLoaderService's background loop.
///   Notify*() methods are called from TradingWorker and OrderExecutor.
///   ConcurrentDictionary + Interlocked make all ops lock-free.
/// </summary>
public sealed class DataDbSymbolFeed
{
    private readonly IConfiguration _cfg;
    private readonly SymbolRegistryService _registry;
    private readonly IOpenPositionSymbolSource _posSource;
    private readonly ILogger<DataDbSymbolFeed> _logger;

    /// <summary>
    /// Symbols ever seen by this Engine run (universe + positions + manual trades).
    /// Bounded to prevent unbounded growth if bot runs for months.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _seenSymbols
        = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxSeenSymbols = 200;

    // Cached result — rebuilt every RefreshIntervalSeconds, not on every call
    private volatile string[] _cachedFeed = Array.Empty<string>();
    private long _lastRefreshTick = 0;
    private const int RefreshIntervalSeconds = 60; // rebuild feed list once a minute

    public DataDbSymbolFeed(
        IConfiguration cfg,
        SymbolRegistryService registry,
        IOpenPositionSymbolSource posSource,
        ILogger<DataDbSymbolFeed> logger)
    {
        _cfg      = cfg;
        _registry = registry;
        _posSource = posSource;
        _logger   = logger;
    }

    // =========================================================
    // NOTIFY API — called by Engine when a symbol becomes active
    // =========================================================

    /// <summary>
    /// Call when a signal fires for a symbol (TradingWorker).
    /// Ensures the symbol is remembered even after it leaves the universe.
    /// </summary>
    public void NotifySignal(string symbol) => Remember(symbol);

    /// <summary>
    /// Call when an order is executed (OrderExecutor).
    /// Manual trades and bot trades both flow through here.
    /// </summary>
    public void NotifyExecution(string symbol) => Remember(symbol);

    /// <summary>
    /// Call when a position is detected on boot/supervisor scan.
    /// Picks up manually opened positions on any symbol.
    /// </summary>
    public void NotifyPosition(string symbol) => Remember(symbol);

    private void Remember(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        var sym = symbol.Trim().ToUpperInvariant();
        if (_seenSymbols.Count >= MaxSeenSymbols && !_seenSymbols.ContainsKey(sym)) return;
        _seenSymbols.TryAdd(sym, 0);
        Invalidate(); // force rebuild on next GetSymbols() call
    }

    private void Invalidate() => Interlocked.Exchange(ref _lastRefreshTick, 0);

    // =========================================================
    // QUERY API — called by HistoricalDataLoaderService
    // =========================================================

    /// <summary>
    /// Returns the full de-duplicated, sorted set of symbols to backfill.
    /// Result is cached for RefreshIntervalSeconds to avoid redundant work.
    /// </summary>
    public async Task<string[]> GetSymbolsAsync(CancellationToken ct)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastRefreshTick);
        if (now - last < RefreshIntervalSeconds * 1000L && _cachedFeed.Length > 0)
            return _cachedFeed;

        var feed = await BuildFeedAsync(ct);
        _cachedFeed = feed;
        Interlocked.Exchange(ref _lastRefreshTick, now);
        return feed;
    }

    private async Task<string[]> BuildFeedAsync(CancellationToken ct)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── 1. appsettings HistoricalData:Symbols ──────────────────────
        var explicit_ = _cfg.GetSection("HistoricalData:Symbols").Get<string[]>()
                        ?? Array.Empty<string>();
        foreach (var s in explicit_) if (!string.IsNullOrWhiteSpace(s)) all.Add(s.Trim().ToUpperInvariant());

        // ── 2. SymbolSelection:Pinned ───────────────────────────────────
        var pinned = _cfg.GetSection("SymbolSelection:Pinned").Get<string[]>()
                     ?? Array.Empty<string>();
        foreach (var s in pinned) if (!string.IsNullOrWhiteSpace(s)) all.Add(s.Trim().ToUpperInvariant());

        // ── 3. Live trading universe (SymbolRegistryService) ─────────────
        // ActiveSymbols is a snapshot property — no Binance call, pure in-memory.
        foreach (var s in _registry.ActiveSymbols) all.Add(s);

        // ── 4. Currently open positions (picks up manual trades) ──────────
        try
        {
            var openPos = await _posSource.GetOpenPositionSymbolsAsync(ct);
            foreach (var s in openPos) all.Add(s);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DATADB-FEED] Failed to read open positions (non-critical)");
        }

        // ── 5. Seen symbols (universe members over time, manual trades) ───
        foreach (var s in _seenSymbols.Keys) all.Add(s);

        var result = all
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .OrderBy(s => s)
            .ToArray();

        _logger.LogDebug(
            "[DATADB-FEED] Feed rebuilt: {total} symbols " +
            "(explicit={ex}, pinned={pin}, universe={uni}, positions={pos}, seen={seen})",
            result.Length,
            explicit_.Length,
            pinned.Length,
            _registry.ActiveSymbols.Count,
            all.Count - explicit_.Length - pinned.Length,
            _seenSymbols.Count);

        return result;
    }
}
