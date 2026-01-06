using System.Collections.Concurrent;
using System.Threading;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services;

public class SymbolRegistryService
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<SymbolRegistryService> _logger;

    private readonly SymbolLiquidityScanner _liquidityScanner;
    private readonly SymbolUniverseBuilder _universeBuilder;
    private readonly AiMarketRegimeService _marketRegime;
    private readonly AiSelfLearningService _ai;
    private readonly UniverseDryRunFileLogger _dryRun;

    // OPTIONAL (but recommended): allow registry to pin open positions (no symbol loss while in position)
    private readonly IOpenPositionSymbolSource? _posSource;


    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private DateTime _lastHardRefresh = DateTime.MinValue;

    // Atomically swapped snapshot
    private volatile UniverseSnapshot _snapshot = UniverseSnapshot.Empty();

    public sealed record SymbolRegistrySnapshotDto(
    DateTime UtcTime,
    IReadOnlyList<string> All,
    IReadOnlyList<string> Long,
    IReadOnlyList<string> Short,
    IReadOnlyList<string> Pinned,
    IReadOnlyList<string> PinnedByPositions,
    int DynamicCap,
    int AiCapLong,
    int AiCapShort,
    decimal BtcVol,
    string BtcVolBucket);


    public IReadOnlyList<string> ActiveSymbols => _snapshot.All;
    public IReadOnlyList<string> ActiveLongSymbols => _snapshot.Long;
    public IReadOnlyList<string> ActiveShortSymbols => _snapshot.Short;
    public IReadOnlyList<string> PinnedSymbols => _snapshot.Pinned;


    public SymbolRegistryService(
        IConfiguration cfg,
        ILogger<SymbolRegistryService> logger,
        SymbolLiquidityScanner liquidityScanner,
        SymbolUniverseBuilder universeBuilder,
        AiMarketRegimeService marketRegime,
        AiSelfLearningService ai,
        UniverseDryRunFileLogger dryRun,
        IOpenPositionSymbolSource? posSource = null) // safe optional DI
    {
        _cfg = cfg;
        _logger = logger;
        _liquidityScanner = liquidityScanner;
        _universeBuilder = universeBuilder;
        _marketRegime = marketRegime;
        _ai = ai;
        _dryRun = dryRun;
        _posSource = posSource;
    }

    public SymbolRegistrySnapshotDto GetSnapshot()
    {
        var s = _snapshot;
        return new SymbolRegistrySnapshotDto(
            s.UtcTime,
            s.All,
            s.Long,
            s.Short,
            s.Pinned,
            s.PinnedByPositions,
            s.DynamicCap,
            s.AiCapLong,
            s.AiCapShort,
            s.BtcVol,
            s.BtcVolBucket
        );
    }


    private record UniverseSnapshot(
        DateTime UtcTime,
        IReadOnlyList<string> All,
        IReadOnlyList<string> Long,
        IReadOnlyList<string> Short,
        IReadOnlyList<string> Pinned,
        IReadOnlyList<string> PinnedByPositions,
        int DynamicCap,
        int AiCapLong,
        int AiCapShort,
        decimal BtcVol,
        string BtcVolBucket)
    {
        public static UniverseSnapshot Empty() => new(
            DateTime.MinValue,
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            0, 0, 0,
            0m, "NA");
    }

    private static decimal Normalize(decimal value, decimal min, decimal max)
    {
        if (max <= min) return 0.5m;
        return Math.Clamp((value - min) / (max - min), 0m, 1m);
    }

    private static IReadOnlyList<string> BlacklistFilter(IEnumerable<string> symbols)
        => symbols.Where(s => !string.Equals(s, "AIAUSDT", StringComparison.OrdinalIgnoreCase)).ToList();

    private static IReadOnlyList<string> NormalizeSymbols(IEnumerable<string> symbols)
        => symbols
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<string> LimitUnion(
    IEnumerable<string> pinnedCfg,
    IEnumerable<string> pinnedByPos,
    IEnumerable<string> longs,
    IEnumerable<string> shorts,
    int totalCap)
    {
        var pinCfg = NormalizeSymbols(pinnedCfg);
        var pinPos = NormalizeSymbols(pinnedByPos);

        // 🔒 absolute priority: symbols with open positions
        var priority = pinPos
            .Concat(pinCfg)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rest = NormalizeSymbols(longs)
            .Concat(NormalizeSymbols(shorts))
            .Where(s => !priority.Contains(s, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (totalCap <= 0)
            return priority.Concat(rest).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var slotsLeft = Math.Max(0, totalCap - priority.Count);

        return priority
            .Concat(rest.Take(slotsLeft))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        var mode = _cfg["SymbolSelection:Mode"] ?? "Manual";

        if (string.Equals(mode, "Manual", StringComparison.OrdinalIgnoreCase))
        {
            var manual = _cfg.GetSection("SymbolSelection:Manual").Get<string[]>() ?? Array.Empty<string>();
            var list = BlacklistFilter(NormalizeSymbols(manual));

            // pinned-by-position still applied in manual, for safety
            var posPinned = await GetPinnedByOpenPositionsSafeAsync(ct);

            var all = list
                .Concat(posPinned)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var snap = new UniverseSnapshot(
                UtcTime: DateTime.UtcNow,
                All: all,
                Long: all,
                Short: all,
                Pinned: list,
                PinnedByPositions: posPinned,
                DynamicCap: all.Count,
                AiCapLong: all.Count,
                AiCapShort: all.Count,
                BtcVol: 0m,
                BtcVolBucket: "MANUAL"
            );

            _snapshot = snap;

            _logger.LogInformation("[SYMBOL] Manual mode: {cnt} → {list}",
                _snapshot.All.Count, string.Join(", ", _snapshot.All));

            return;
        }

        var refreshMinutes = _cfg.GetValue<int?>("SymbolSelection:Auto:RefreshInterval") ?? 10;
        var now = DateTime.UtcNow;

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (now - _lastHardRefresh < TimeSpan.FromMinutes(refreshMinutes))
            {
                // 🔒 SOFT refresh: only update pinned-by-positions (no recompute)
                try
                {
                    var pinnedPos = await GetPinnedByOpenPositionsSafeAsync(ct);

                    if (pinnedPos.Count > 0)
                    {
                        var merged = _snapshot.All
                            .Concat(pinnedPos)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        _snapshot = _snapshot with
                        {
                            All = merged,
                            PinnedByPositions = pinnedPos
                        };
                    }
                }
                catch { /* ignored by design */ }

                SoftHealthCheck();
                return;
            }
             
        }
        finally
        {
            _refreshLock.Release();
        }

        // last-known-good snapshot for rollback
        var lastGood = _snapshot;

        try
        {
            var auto = _cfg.GetSection("SymbolSelection:Auto");

            // === CAPS / PARAMS =================================================
            var totalUniverseCap = auto.GetValue<int?>("TotalUniverseCap") ?? 20;

            var pinnedCfg = BlacklistFilter(GetPinnedSymbols());
            var pinnedPos = await GetPinnedByOpenPositionsSafeAsync(ct);

            var pinned = pinnedCfg
                .Concat(pinnedPos)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var topVolumeCount = auto.GetValue<int?>("TopVolumeCount") ?? 60;

            var minVolLong = auto.GetValue<decimal?>("Min24hVolumeLong")
                             ?? auto.GetValue<decimal>("Min24hVolume");

            var minVolShort = auto.GetValue<decimal?>("Min24hVolumeShort")
                              ?? auto.GetValue<decimal>("Min24hVolume");

            var minPrice = auto.GetValue<decimal>("MinPrice");

            var finalCap = auto.GetValue<int?>("FinalUniverseCap") ?? 25;
            var maxAdds = auto.GetValue<int?>("StabilityMaxAdds") ?? 3;

            var aiWeight = auto.GetValue<decimal?>("AiWeight") ?? 0.65m;
            var momWeight = auto.GetValue<decimal?>("MomentumWeight") ?? 0.35m;
            var momCap = auto.GetValue<decimal?>("MomentumCapPercent") ?? 15m;

            var dryRunLimit = auto.GetValue<int?>("DryRunLogLimit") ?? 12;

            var enableBtcFilter = auto.GetValue<bool?>("EnableBtcFilter") ?? false;
            var btcDump = auto.GetValue<decimal?>("BtcDumpThreshold") ?? -2.5m;
            var btcSqueeze = auto.GetValue<decimal?>("BtcSqueezeThreshold") ?? 2.5m;

            // === SNAPSHOTS =====================================================
            var allSnapshots = await _liquidityScanner.LoadSnapshotsAsync(ct);

            if (allSnapshots == null || allSnapshots.Count == 0)
            {
                _logger.LogWarning("[SYMBOL] No market snapshots → BOOTSTRAP pinned universe");

                if (pinned.Count > 0)
                {
                    _snapshot = new UniverseSnapshot(
                        UtcTime: DateTime.UtcNow,
                        All: pinned,
                        Long: pinned,
                        Short: pinned,
                        Pinned: pinnedCfg,
                        PinnedByPositions: pinnedPos,
                        DynamicCap: pinned.Count,
                        AiCapLong: pinned.Count,
                        AiCapShort: pinned.Count,
                        BtcVol: 0m,
                        BtcVolBucket: "BOOTSTRAP"
                    );
                }

                return;
            }

            // === BTC METRICS ===================================================
            var btcSnapshotRaw = allSnapshots.FirstOrDefault(s => s.Symbol == "BTCUSDT");
            var btcVol = btcSnapshotRaw != null
                ? Math.Abs(btcSnapshotRaw.PriceChangePercent)
                : 0m;

            // === REGIME FILTER =================================================
            var baseSnapshots = allSnapshots
                .Where(s => _marketRegime.IsTradable(s.Symbol))
                .ToList();

            if (baseSnapshots.Count == 0)
            {
                _logger.LogWarning("[SYMBOL] Regime filtered all → FALLBACK pinned universe");

                if (pinned.Count > 0)
                {
                    _snapshot = new UniverseSnapshot(
                        UtcTime: DateTime.UtcNow,
                        All: pinned,
                        Long: pinned,
                        Short: pinned,
                        Pinned: pinnedCfg,
                        PinnedByPositions: pinnedPos,
                        DynamicCap: pinned.Count,
                        AiCapLong: pinned.Count,
                        AiCapShort: pinned.Count,
                        BtcVol: btcVol,
                        BtcVolBucket: "REGIME-FALLBACK"
                    );
                }

                return;
            }

            // === BTC BIAS ======================================================
            decimal btcBiasLong = 1m;
            decimal btcBiasShort = 1m;

            if (enableBtcFilter && btcSnapshotRaw != null)
            {
                if (btcSnapshotRaw.PriceChangePercent <= btcDump)
                {
                    btcBiasLong = 0.35m;
                    _logger.LogWarning("[SYMBOL][BTC] Dump {pct}% → LONG bias", btcSnapshotRaw.PriceChangePercent);
                }
                else if (btcSnapshotRaw.PriceChangePercent >= btcSqueeze)
                {
                    btcBiasShort = 0.35m;
                    _logger.LogWarning("[SYMBOL][BTC] Squeeze {pct}% → SHORT bias", btcSnapshotRaw.PriceChangePercent);
                }
            }

            // === DYNAMIC CAP ===================================================
            var dyn = _cfg.GetSection("SymbolSelection:DynamicCap");
            var useDynCap = dyn.GetValue<bool?>("Enabled") ?? false;

            var dynamicCap = finalCap;

            if (useDynCap)
            {
                var low = dyn.GetValue<decimal?>("LowVolPct") ?? 1.2m;
                var mid = dyn.GetValue<decimal?>("MidVolPct") ?? 2.5m;

                var capLow = dyn.GetValue<int?>("CapLowVol") ?? finalCap;
                var capMid = dyn.GetValue<int?>("CapMidVol") ?? finalCap;
                var capHigh = dyn.GetValue<int?>("CapHighVol") ?? Math.Max(1, Math.Min(finalCap, 4));

                if (btcVol <= low) dynamicCap = capLow;
                else if (btcVol <= mid) dynamicCap = capMid;
                else dynamicCap = capHigh;

                dynamicCap = Math.Clamp(dynamicCap, 1, Math.Min(finalCap, topVolumeCount));
            }
            else
            {
                dynamicCap = Math.Clamp(finalCap, 1, topVolumeCount);
            }

            // === AI CAPS =======================================================
            var longWr = _ai.GetWinRate(SignalSide.Buy);
            var shortWr = _ai.GetWinRate(SignalSide.Sell);

            static int AiAdjust(int cap, decimal wr)
            {
                if (wr < 0.35m) return Math.Max(1, cap - 2);
                if (wr < 0.45m) return Math.Max(1, cap - 1);
                return cap;
            }

            var aiCapLong = AiAdjust(dynamicCap, longWr);
            var aiCapShort = AiAdjust(dynamicCap, shortWr);

            // === SCORING =======================================================
            var longSnaps = baseSnapshots
                .Where(s => _ai.GetRecentPnL(s.Symbol, SignalSide.Buy) > -0.15m)
                .OrderByDescending(s =>
                {
                    var ai = Normalize(_ai.GetSymbolScore(s.Symbol, SignalSide.Buy), 0m, 1m);
                    var mom = Normalize((decimal)Math.Abs(s.PriceChangePercent), 0m, momCap);
                    return (ai * aiWeight + mom * momWeight) * btcBiasLong;
                })
                .ToList();

            var shortSnaps = baseSnapshots
                .Where(s => _ai.GetRecentPnL(s.Symbol, SignalSide.Sell) > -0.15m)
                .OrderByDescending(s =>
                {
                    var ai = Normalize(_ai.GetSymbolScore(s.Symbol, SignalSide.Sell), 0m, 1m);
                    var mom = Normalize((decimal)Math.Abs(s.PriceChangePercent), 0m, momCap);
                    return (ai * aiWeight + mom * momWeight) * btcBiasShort;
                })
                .ToList();

            var longCandidates = BlacklistFilter(_universeBuilder.Build(
                longSnaps, pinned.ToArray(), topVolumeCount, minVolLong, minPrice));

            var shortCandidates = BlacklistFilter(_universeBuilder.Build(
                shortSnaps, pinned.ToArray(), topVolumeCount, minVolShort, minPrice));

            var longList = ApplyStabilityGuardSide(lastGood.Long, longCandidates, maxAdds)
                .Take(aiCapLong)
                .ToList();

            var shortList = ApplyStabilityGuardSide(lastGood.Short, shortCandidates, maxAdds)
                .Take(aiCapShort)
                .ToList();

            var all = LimitUnion(
                pinnedCfg,
                pinnedPos,
                longList,
                shortList,
                totalUniverseCap);

            longList = longList.Where(s => all.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
            shortList = shortList.Where(s => all.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();

            var bucket =
                btcVol <= 1.2m ? "LOW" :
                btcVol <= 2.5m ? "MID" : "HIGH";

            _snapshot = new UniverseSnapshot(
                UtcTime: DateTime.UtcNow,
                All: all,
                Long: longList,
                Short: shortList,
                Pinned: pinnedCfg,
                PinnedByPositions: pinnedPos,
                DynamicCap: dynamicCap,
                AiCapLong: aiCapLong,
                AiCapShort: aiCapShort,
                BtcVol: btcVol,
                BtcVolBucket: bucket
            );
            _lastHardRefresh = DateTime.UtcNow;
            _logger.LogInformation(
                "[SYMBOL] Registry refresh: pinnedCfg={pc}, pinnedPos={pp}, long={lng}, short={sht}, total={tot}",
                _snapshot.Pinned.Count,
                _snapshot.PinnedByPositions.Count,
                _snapshot.Long.Count,
                _snapshot.Short.Count,
                _snapshot.All.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SYMBOL] Refresh failed → rollback last-good universe");
            _snapshot = lastGood;
        }

    }

    private void SoftHealthCheck()
    {
        if (_snapshot.All.Count == 0)
            _logger.LogWarning("[SYMBOL] ActiveSymbols empty during soft check");
        if (_snapshot.Long.Count == 0)
            _logger.LogWarning("[SYMBOL] ActiveLongSymbols empty during soft check");
        if (_snapshot.Short.Count == 0)
            _logger.LogWarning("[SYMBOL] ActiveShortSymbols empty during soft check");
    }

    private IReadOnlyList<string> ApplyStabilityGuardSide(
        IReadOnlyList<string> prev,
        IReadOnlyList<string> next,
        int maxAdds)
    {
        prev ??= Array.Empty<string>();
        next ??= Array.Empty<string>();

        if (next.Count == 0)
            return next;

        if (prev.Count == 0 || next.Count <= maxAdds)
            return next;

        var stayed = prev.Intersect(next, StringComparer.OrdinalIgnoreCase).ToList();
        var added = next.Except(stayed, StringComparer.OrdinalIgnoreCase)
                        .Take(Math.Max(0, maxAdds))
                        .ToList();

        return stayed
            .Concat(added)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> GetPinnedSymbols()
    {
        return _cfg
            .GetSection("SymbolSelection:Pinned")
            .Get<string[]>()?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new();
    }

    private async Task<IReadOnlyList<string>> GetPinnedByOpenPositionsSafeAsync(CancellationToken ct)
    {
        try
        {
            if (_posSource == null) return Array.Empty<string>();

            var symbols = await _posSource.GetOpenPositionSymbolsAsync(ct);
            return BlacklistFilter(NormalizeSymbols(symbols));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SYMBOL] Pinned-by-positions source failed");
            return Array.Empty<string>();
        }
    }

}

// =====================================================================
// Contract for SymbolRegistry to pin symbols while positions are open.
// Implement it using your existing EngineState/WS positions cache.
// =====================================================================
public interface IOpenPositionSymbolSource
{
    Task<IReadOnlyList<string>> GetOpenPositionSymbolsAsync(CancellationToken ct);
}
