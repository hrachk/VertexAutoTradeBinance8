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
        var mode = _cfg["SymbolSelection:Mode"] ?? "Auto";

        // Manual — как и было
        if (string.Equals(mode, "Manual", StringComparison.OrdinalIgnoreCase))
        {
            var pinnedCfg = BlacklistFilter(GetPinnedSymbols());
            var pinnedPos = await GetPinnedByOpenPositionsSafeAsync(ct);

            var all = pinnedCfg.Concat(pinnedPos)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // ✅ Canonical: manual может быть пустым (если конфиг пустой), но это осознанно
            _snapshot = new UniverseSnapshot(
                UtcTime: DateTime.UtcNow,
                All: all,
                Long: all,
                Short: all,
                Pinned: pinnedCfg,
                PinnedByPositions: pinnedPos,
                DynamicCap: all.Count,
                AiCapLong: all.Count,
                AiCapShort: all.Count,
                BtcVol: 0m,
                BtcVolBucket: "MANUAL"
            );

            _logger.LogInformation("[SYMBOL] Manual mode: {cnt} → {list}",
                _snapshot.All.Count, string.Join(", ", _snapshot.All));
            return;
        }

        var refreshMinutes = _cfg.GetValue<int?>("SymbolSelection:Auto:RefreshInterval") ?? 10;
        var now = DateTime.UtcNow;

        await _refreshLock.WaitAsync(ct);
        try
        {
            // =========================================================
            // 1) SOFT window? (only if snapshot is non-empty)
            // =========================================================
            var inSoftWindow = (now - _lastHardRefresh) < TimeSpan.FromMinutes(refreshMinutes);

            if (inSoftWindow && _snapshot.All.Count > 0)
            {
                await SoftRefreshPinnedByPositionsAsync(ct);
                SoftHealthCheck();
                return;
            }

            if (inSoftWindow && _snapshot.All.Count == 0)
            {
                _logger.LogWarning("[SYMBOL] Soft window but universe EMPTY → forcing HARD refresh");
                // fall through to hard refresh
            }

            // =========================================================
            // 2) HARD refresh (always under the same lock)
            // =========================================================
            var built = await BuildUniverseSnapshotHardAsync(ct);

            // ✅ Hard refresh success gate: only commit if non-empty
            if (built.All.Count > 0)
            {
                _snapshot = built;
                _lastHardRefresh = now;

                _logger.LogInformation(
                    "[SYMBOL] Registry refresh: pinnedCfg={pc}, pinnedPos={pp}, long={lng}, short={sht}, total={tot}, bucket={bucket}",
                    _snapshot.Pinned.Count,
                    _snapshot.PinnedByPositions.Count,
                    _snapshot.Long.Count,
                    _snapshot.Short.Count,
                    _snapshot.All.Count,
                    _snapshot.BtcVolBucket);

                return;
            }

            // =========================================================
            // 3) If built empty → do NOT freeze, do NOT advance lastHardRefresh
            // =========================================================
            _logger.LogError("[SYMBOL] HARD refresh produced EMPTY universe → keep previous snapshot and retry next tick");

            // Если предыдущий snapshot тоже пустой — это сигнал upstream (нет snapshots/WS/market data)
            if (_snapshot.All.Count == 0)
                _logger.LogCritical("[SYMBOL] Universe still EMPTY after hard refresh. Market snapshots source likely failing.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SYMBOL] Refresh failed (canonical): keep last snapshot, do not freeze");
            // ✅ не трогаем _snapshot и _lastHardRefresh
        }
        finally
        {
            _refreshLock.Release();
        }
    }
    private async Task SoftRefreshPinnedByPositionsAsync(CancellationToken ct)
    {
        try
        {
            var pinnedPos = await GetPinnedByOpenPositionsSafeAsync(ct);
            if (pinnedPos.Count == 0)
                return;

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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SYMBOL] Soft refresh pinned-by-positions failed (ignored)");
        }
    }
    private async Task<UniverseSnapshot> BuildUniverseSnapshotHardAsync(CancellationToken ct)
    {
        var auto = _cfg.GetSection("SymbolSelection:Auto");

        var totalUniverseCap = auto.GetValue<int?>("TotalUniverseCap") ?? 20;
        var finalCap = auto.GetValue<int?>("FinalUniverseCap") ?? 25;
        var maxAdds = auto.GetValue<int?>("StabilityMaxAdds") ?? 3;

        var pinnedCfg = BlacklistFilter(GetPinnedSymbols());
        var pinnedPos = await GetPinnedByOpenPositionsSafeAsync(ct);
        var pinned = pinnedCfg.Concat(pinnedPos).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var topVolumeCount = auto.GetValue<int?>("TopVolumeCount") ?? 60;

        var minVolLong = auto.GetValue<decimal?>("Min24hVolumeLong") ?? auto.GetValue<decimal>("Min24hVolume");
        var minVolShort = auto.GetValue<decimal?>("Min24hVolumeShort") ?? auto.GetValue<decimal>("Min24hVolume");
        var minPrice = auto.GetValue<decimal>("MinPrice");

        var aiWeight = auto.GetValue<decimal?>("AiWeight") ?? 0.65m;
        var momWeight = auto.GetValue<decimal?>("MomentumWeight") ?? 0.35m;
        var momCap = auto.GetValue<decimal?>("MomentumCapPercent") ?? 15m;

        var enableBtcFilter = auto.GetValue<bool?>("EnableBtcFilter") ?? false;
        var btcDump = auto.GetValue<decimal?>("BtcDumpThreshold") ?? -2.5m;
        var btcSqueeze = auto.GetValue<decimal?>("BtcSqueezeThreshold") ?? 2.5m;

        var lastGood = _snapshot;

        // =====================================================
        // MARKET SNAPSHOTS
        // =====================================================
        var allSnapshots = await _liquidityScanner.LoadSnapshotsAsync(ct);
        if (allSnapshots == null || allSnapshots.Count == 0)
        {
            _logger.LogWarning("[SYMBOL] No market snapshots → cannot build universe");
            // ❗ не pinned-only: возвращаем пусто, чтобы LoadAsync НЕ зафиксировал пустоту и повторил позже
            return UniverseSnapshot.Empty() with { BtcVolBucket = "NO-SNAPSHOTS" };
        }

        // BTC metrics
        var btcSnapshotRaw = allSnapshots.FirstOrDefault(s => s.Symbol == "BTCUSDT");
        var btcVol = btcSnapshotRaw != null ? Math.Abs(btcSnapshotRaw.PriceChangePercent) : 0m;

        // =====================================================
        // BASE candidates (tradable)
        // =====================================================
        var tradable = allSnapshots.Where(s => _marketRegime.IsTradable(s.Symbol)).ToList();

        // ✅ canonical fallback: если regime убил всё — используем raw snapshots (кроме blacklist/price/volume фильтров builder'а)
        if (tradable.Count == 0)
        {
            _logger.LogWarning("[SYMBOL] Regime filtered all → fallback to raw snapshots");
            tradable = allSnapshots.ToList();
        }

        // BTC bias
        decimal btcBiasLong = 1m, btcBiasShort = 1m;
        if (enableBtcFilter && btcSnapshotRaw != null)
        {
            if (btcSnapshotRaw.PriceChangePercent <= btcDump) btcBiasLong = 0.35m;
            else if (btcSnapshotRaw.PriceChangePercent >= btcSqueeze) btcBiasShort = 0.35m;
        }

        // Startup relax
        var isStartup =
            _ai.TotalTrades < 5 ||
            (DateTime.UtcNow - _ai.StartedUtc) < TimeSpan.FromMinutes(15);

        // Dynamic cap (как у тебя было)
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

        // AI caps
        var longWr = _ai.GetWinRate(SignalSide.Buy);
        var shortWr = _ai.GetWinRate(SignalSide.Sell);

        static int AiAdjust(int cap, decimal wr, bool isStartup)
        {
            if (isStartup) return cap;
            if (wr < 0.35m) return Math.Max(1, cap - 2);
            if (wr < 0.45m) return Math.Max(1, cap - 1);
            return cap;
        }

        var aiCapLong = AiAdjust(dynamicCap, longWr, isStartup);
        var aiCapShort = AiAdjust(dynamicCap, shortWr, isStartup);

        // =====================================================
        // SCORING
        // =====================================================
        var longSnaps = tradable
            .Where(s => isStartup || _ai.GetRecentPnL(s.Symbol, SignalSide.Buy) > -0.15m)
            .OrderByDescending(s =>
            {
                var ai = Normalize(_ai.GetSymbolScore(s.Symbol, SignalSide.Buy), 0m, 1m);
                var mom = Normalize((decimal)Math.Abs(s.PriceChangePercent), 0m, momCap);
                return (ai * aiWeight + mom * momWeight) * btcBiasLong;
            })
            .ToList();

        var shortSnaps = tradable
            .Where(s => isStartup || _ai.GetRecentPnL(s.Symbol, SignalSide.Sell) > -0.15m)
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

        // ✅ canonical fallback: если builder вернул пусто — берём ТОП по объёму (raw), а не pinned-only
        if (longCandidates.Count == 0 && shortCandidates.Count == 0)
        {
            _logger.LogWarning("[SYMBOL] Builder produced empty candidates → fallback to top-volume raw list");

            var topRaw = allSnapshots
                .OrderByDescending(s => s.QuoteVolume24h) // если у Snapshot есть QuoteVolume; если нет — оставь как было или используй Volume field
                .Select(s => s.Symbol)
                .Where(s => !string.Equals(s, "AIAUSDT", StringComparison.OrdinalIgnoreCase))
                .Take(Math.Max(5, Math.Min(totalUniverseCap, topVolumeCount)))
                .ToList();

            // pinned остаётся приоритетом, но не единственным источником
            var allFallback = LimitUnion(pinnedCfg, pinnedPos, topRaw, topRaw, totalUniverseCap);

            return new UniverseSnapshot(
                UtcTime: DateTime.UtcNow,
                All: allFallback,
                Long: allFallback,
                Short: allFallback,
                Pinned: pinnedCfg,
                PinnedByPositions: pinnedPos,
                DynamicCap: dynamicCap,
                AiCapLong: aiCapLong,
                AiCapShort: aiCapShort,
                BtcVol: btcVol,
                BtcVolBucket: "FALLBACK-TOPVOLUME"
            );
        }

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

        var bucket = btcVol <= 1.2m ? "LOW" : btcVol <= 2.5m ? "MID" : "HIGH";

        return new UniverseSnapshot(
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
        // Canonical key
        var pinned = _cfg.GetSection("SymbolSelection:Pinned").Get<string[]>();

        // Backward-compatible keys (do not remove; prevents silent empty manual universe)
        pinned ??= _cfg.GetSection("SymbolSelection:Manual:Pinned").Get<string[]>();
        pinned ??= _cfg.GetSection("SymbolSelection:Manual").Get<string[]>();

        return (pinned ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
