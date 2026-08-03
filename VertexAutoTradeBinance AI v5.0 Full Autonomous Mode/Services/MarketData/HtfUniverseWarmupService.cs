using Binance.Net.Enums;
using System.Collections.Concurrent;
using VertexAutoTradeBinance8.MarketData;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.HistoricalData;

namespace VertexAutoTradeBinance8.Services.Bootstrap;

/// <summary>
/// Fills the 1h / 4h / 1d buffers for EVERY symbol in the active universe —
/// not just the pinned ones SupervisorBootstrapHostedService covers.
///
/// WHY THIS EXISTS
/// ---------------
/// MarketContextService reads exactly 1h, 4h and 1d to compute regime
/// alignment: the ConfidenceBoost (up to x1.25 when all three agree) and the
/// LongOnly / ShortOnly bias gate. Its Read() bails out to MarketRegime.Range
/// whenever a timeframe has fewer than 20 bars.
///
/// SupervisorBootstrapHostedService only bootstraps _symbols.PinnedSymbols.
/// Any symbol that enters the universe through Auto-selection rotation
/// therefore had no HTF data of its own — the only remaining source being
/// live WebSocket, which delivers ONE 1d candle per day. Such a symbol sat
/// below the 20-bar threshold for weeks, so every signal on it was evaluated
/// with HTF confirmation silently disabled.
///
/// This service closes that gap WITHOUT slowing startup: it waits on
/// IBootGate.WaitReadyAsync() first, so the trading loop is already live
/// before a single REST call is made here.
///
/// RATE-LIMIT SAFETY
/// -----------------
/// One REST call at a time (RestGate), a fixed delay between calls, and the
/// archive on disk is always consulted before the network. At the configured
/// 400 ms spacing this is ~2.5 req/s worst case — roughly 6% of the Binance
/// futures budget, and only while there is actually something to backfill.
/// Once every symbol is warm the sweep does zero network work.
/// </summary>
public sealed class HtfUniverseWarmupService : BackgroundService
{
    private readonly SymbolRegistryService _symbols;
    private readonly MarketDataService _market;
    private readonly MarketDataKlineBuffer _buffer;
    private readonly HistoricalDataStore _historicalStore;
    private readonly IBootGate _bootGate;
    private readonly ILogger<HtfUniverseWarmupService> _logger;

    /// <summary>The three timeframes MarketContextService actually reads.</summary>
    private static readonly KlineInterval[] HtfSet =
    {
        KlineInterval.OneHour,
        KlineInterval.FourHour,
        KlineInterval.OneDay,
    };

    /// <summary>
    /// Bars we want per HTF. MarketContextService asks for 120 (ContextBars)
    /// and AiMarketRegimeService needs a decent window for slope/volatility,
    /// so 150 leaves headroom without making the fetch expensive.
    /// </summary>
    private const int TargetBars = 150;

    /// <summary>
    /// Below this we consider the series cold and backfill it. Deliberately
    /// higher than MarketContextService's own 20-bar floor: a series sitting
    /// at 25 bars technically passes that guard but still produces a poor
    /// regime read, which is worse than no read at all.
    /// </summary>
    private const int MinAcceptableBars = 100;

    /// <summary>One historical REST call at a time, process-wide.</summary>
    private static readonly SemaphoreSlim RestGate = new(1, 1);
    private static readonly TimeSpan RestDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>How often the full universe is re-swept.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);

    /// <summary>Symbols that just joined the universe — warmed before the rest.</summary>
    private readonly ConcurrentQueue<string> _priority = new();
    private readonly SemaphoreSlim _wake = new(0);

    public HtfUniverseWarmupService(
        SymbolRegistryService symbols,
        MarketDataService market,
        MarketDataKlineBuffer buffer,
        HistoricalDataStore historicalStore,
        IBootGate bootGate,
        ILogger<HtfUniverseWarmupService> logger)
    {
        _symbols = symbols;
        _market = market;
        _buffer = buffer;
        _historicalStore = historicalStore;
        _bootGate = bootGate;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Never delay the trading loop. Boot finishes, THEN we start working.
        try
        {
            await _bootGate.WaitReadyAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Small grace period so the boot-time REST burst has fully drained
        // before we add our own traffic on top of it.
        await Task.Delay(TimeSpan.FromSeconds(20), ct);

        _symbols.UniverseChanged += OnUniverseChanged;

        _logger.LogInformation(
            "[HTF-WARMUP] Started — target {bars} bars on 1h/4h/1d for the whole universe",
            TargetBars);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await DrainPriorityAsync(ct);
                    await SweepUniverseAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[HTF-WARMUP] Sweep failed (non-fatal)");
                }

                // Wake early if the universe rotated, otherwise sleep the
                // full interval.
                try
                {
                    await _wake.WaitAsync(SweepInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            _symbols.UniverseChanged -= OnUniverseChanged;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Universe rotation → warm the newcomers immediately
    // ─────────────────────────────────────────────────────────────
    private void OnUniverseChanged(IReadOnlyList<string> universe)
    {
        if (universe == null || universe.Count == 0)
            return;

        bool queued = false;

        foreach (var symbol in universe)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                continue;

            // Only queue symbols that actually need work, so a rotation that
            // reshuffles already-warm symbols costs nothing.
            if (!NeedsWarmup(symbol))
                continue;

            _priority.Enqueue(symbol);
            queued = true;
        }

        if (queued)
        {
            _logger.LogInformation("[HTF-WARMUP] Universe changed — new symbols queued");
            try { _wake.Release(); } catch (SemaphoreFullException) { }
        }
    }

    private bool NeedsWarmup(string symbol)
    {
        foreach (var tf in HtfSet)
        {
            if (_buffer.Count(symbol, tf) < MinAcceptableBars)
                return true;
        }
        return false;
    }

    // ─────────────────────────────────────────────────────────────
    // Work loops
    // ─────────────────────────────────────────────────────────────
    private async Task DrainPriorityAsync(CancellationToken ct)
    {
        while (_priority.TryDequeue(out var symbol))
        {
            ct.ThrowIfCancellationRequested();
            await WarmSymbolAsync(symbol, ct);
        }
    }

    private async Task SweepUniverseAsync(CancellationToken ct)
    {
        var universe = _symbols.ActiveSymbols;
        if (universe == null || universe.Count == 0)
            return;

        int warmed = 0;

        foreach (var symbol in universe)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(symbol))
                continue;

            if (await WarmSymbolAsync(symbol, ct))
                warmed++;
        }

        if (warmed > 0)
        {
            _logger.LogInformation(
                "[HTF-WARMUP] Sweep done — {warmed}/{total} symbols backfilled",
                warmed, universe.Count);
        }
    }

    /// <summary>Returns true if any timeframe for this symbol was backfilled.</summary>
    private async Task<bool> WarmSymbolAsync(string symbol, CancellationToken ct)
    {
        bool didWork = false;

        foreach (var tf in HtfSet)
        {
            ct.ThrowIfCancellationRequested();

            int have = _buffer.Count(symbol, tf);
            if (have >= MinAcceptableBars)
                continue;

            // 1) Local archive first — a file read beats a REST round-trip and
            //    HistoricalDataLoaderService already stores 1h/4h/1d there.
            if (await TryLoadFromArchiveAsync(symbol, tf, ct))
            {
                didWork = true;

                // The archive is only as fresh as the loader's last cycle, so
                // still close the gap with one small REST call below if we're
                // now above the threshold but potentially stale.
                if (_buffer.Count(symbol, tf) >= MinAcceptableBars)
                {
                    await GapFillAsync(symbol, tf, ct);
                    continue;
                }
            }

            // 2) Archive missing or too thin → full REST backfill.
            if (await TryLoadFromRestAsync(symbol, tf, TargetBars, ct))
                didWork = true;
        }

        return didWork;
    }

    // ─────────────────────────────────────────────────────────────
    // Sources
    // ─────────────────────────────────────────────────────────────
    private async Task<bool> TryLoadFromArchiveAsync(
        string symbol, KlineInterval tf, CancellationToken ct)
    {
        var label = TfToArchiveLabel(tf);
        if (label == null || !_historicalStore.Has(symbol, label))
            return false;

        try
        {
            var archived = await _historicalStore.LoadLastAsync(symbol, label, TargetBars, ct);
            if (archived == null || archived.Count == 0)
                return false;

            foreach (var k in archived)
            {
                _buffer.Upsert(symbol, tf, new Binance.Net.Objects.Models.Futures.BinanceFuturesUsdtKline
                {
                    OpenTime = DateTimeOffset.FromUnixTimeMilliseconds(k.OpenTime).UtcDateTime,
                    OpenPrice = k.Open,
                    HighPrice = k.High,
                    LowPrice = k.Low,
                    ClosePrice = k.Close,
                    Volume = k.Volume,
                });
            }

            _logger.LogDebug(
                "[HTF-WARMUP] {symbol} {tf} ← archive ({n} bars)",
                symbol, tf, archived.Count);

            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[HTF-WARMUP] {symbol} {tf} archive read failed", symbol, tf);
            return false;
        }
    }

    private async Task<bool> TryLoadFromRestAsync(
        string symbol, KlineInterval tf, int bars, CancellationToken ct)
    {
        await RestGate.WaitAsync(ct);
        try
        {
            await Task.Delay(RestDelay, ct);

            var klines = await _market.GetKlines(symbol, tf, bars);
            if (klines == null)
                return false;

            int n = 0;
            foreach (var k in klines)
            {
                _buffer.Upsert(symbol, tf, k);
                n++;
            }

            if (n == 0)
                return false;

            _logger.LogInformation(
                "[HTF-WARMUP] {symbol} {tf} ← REST ({n} bars, buffer now {total})",
                symbol, tf, n, _buffer.Count(symbol, tf));

            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HTF-WARMUP] {symbol} {tf} REST backfill failed", symbol, tf);
            return false;
        }
        finally
        {
            RestGate.Release();
        }
    }

    /// <summary>
    /// Pulls just the most recent bars so an archive-warmed series reflects
    /// the current candle rather than the loader's last cycle.
    /// </summary>
    private async Task GapFillAsync(string symbol, KlineInterval tf, CancellationToken ct)
    {
        await RestGate.WaitAsync(ct);
        try
        {
            await Task.Delay(RestDelay, ct);

            var fresh = await _market.GetKlines(symbol, tf, 5);
            if (fresh == null)
                return;

            foreach (var k in fresh)
                _buffer.Upsert(symbol, tf, k);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[HTF-WARMUP] {symbol} {tf} gap-fill failed", symbol, tf);
        }
        finally
        {
            RestGate.Release();
        }
    }

    /// <summary>
    /// Must match HistoricalDataLoaderService's lowercase datadb/SYMBOL/TF.json
    /// filenames. Only the three HTF entries are needed here.
    /// </summary>
    private static string? TfToArchiveLabel(KlineInterval tf) => tf switch
    {
        KlineInterval.OneHour => "1h",
        KlineInterval.FourHour => "4h",
        KlineInterval.OneDay => "1d",
        _ => null
    };
}
