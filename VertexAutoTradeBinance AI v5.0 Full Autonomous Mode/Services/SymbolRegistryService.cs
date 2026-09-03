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
    private readonly IOpenPositionSymbolSource? _posSource;

    private readonly SemaphoreSlim _refreshLock = new(1, 1); 

    private volatile UniverseSnapshot _snapshot = UniverseSnapshot.Empty();
    public event Action<IReadOnlyList<string>>? UniverseChanged;

    // ============================================================
    // DTO
    // ============================================================
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

    // ============================================================
    // CTOR
    // ============================================================
    public SymbolRegistryService(
        IConfiguration cfg,
        ILogger<SymbolRegistryService> logger,
        SymbolLiquidityScanner liquidityScanner,
        SymbolUniverseBuilder universeBuilder,
        AiMarketRegimeService marketRegime,
        AiSelfLearningService ai,
        UniverseDryRunFileLogger dryRun,
        IOpenPositionSymbolSource? posSource = null)
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

    // ============================================================
    // SNAPSHOT
    // ============================================================
    public SymbolRegistrySnapshotDto GetSnapshot()
    {
        var s = _snapshot;
        return new SymbolRegistrySnapshotDto(
            s.UtcTime, s.All, s.Long, s.Short,
            s.Pinned, s.PinnedByPositions,
            s.DynamicCap, s.AiCapLong, s.AiCapShort,
            s.BtcVol, s.BtcVolBucket);
    }

    // ============================================================
    // UNIVERSE HASH (order-independent, O(n))
    // ============================================================
    private static ulong ComputeUniverseHash(IReadOnlyList<string> symbols)
    {
        unchecked
        {
            ulong h = 1469598103934665603UL;
            for (int i = 0; i < symbols.Count; i++)
            {
                var s = symbols[i];
                if (string.IsNullOrWhiteSpace(s)) continue;

                var sh = (ulong)StringComparer.OrdinalIgnoreCase.GetHashCode(s);
                h ^= sh + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
            }
            h ^= (ulong)symbols.Count;
            return h;
        }
    }
    private static bool HasUniverseChanged(
    UniverseSnapshot oldSnap,
    IReadOnlyList<string> newAll,
    ulong newHash)
    {
        return oldSnap.AllHash != newHash || oldSnap.All.Count != newAll.Count;
    }
    private void PublishUniverseIfChanged(IReadOnlyList<string> newAll, ulong newHash)
    {
        var s = _snapshot;
        if (s.All.Count == newAll.Count && s.AllHash == newHash)
            return;

        UniverseChanged?.Invoke(newAll);
    }

    // ============================================================
    // SNAPSHOT MODEL
    // ============================================================
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
        string BtcVolBucket,
        ulong AllHash)
    {
        public static UniverseSnapshot Empty() => new(
            DateTime.MinValue,
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            0, 0, 0,
            0m, "NA",
            0UL);
    }

    // ============================================================
    // SAFE NORMALIZATION (NO DECISIONS INSIDE)
    // ============================================================
    private static bool TryNormalize01(decimal value, decimal min, decimal max, out decimal norm)
    {
        norm = 0m;
        if (max <= min) return false;

        var range = max - min;
        if (range == 0m) return false;

        var x = (value - min) / range;
        norm = Math.Clamp((value - min) / (max - min), 0m, 1m);
        return true;
    }

    // ============================================================
    // HELPERS
    // ============================================================
    private static IReadOnlyList<string> BlacklistFilter(IEnumerable<string> symbols)
        => symbols.Where(s => !string.Equals(s, "TANSSIUSDT", StringComparison.OrdinalIgnoreCase)).ToList();

    private static IReadOnlyList<string> NormalizeSymbols(IEnumerable<string> symbols)
        => symbols.Where(s => !string.IsNullOrWhiteSpace(s))
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

        var pin = NormalizeSymbols(pinnedByPos).Concat(NormalizeSymbols(pinnedCfg))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (totalCap <= 0)
            return pin; // ❗ pinned-only, безопасно

        var pinSet = new HashSet<string>(pin, StringComparer.OrdinalIgnoreCase);

        var rest = NormalizeSymbols(longs)
            .Concat(NormalizeSymbols(shorts))
           .Where(s => !pinSet.Contains(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var slots = Math.Max(0, totalCap - pin.Count);
        return pin.Concat(rest.Take(slots)).ToList();
    }

    // ============================================================
    // LOAD
    // ============================================================
    public async Task LoadAsync(CancellationToken ct)
    {
        // ============================================================
        // SYMBOL SELECTION MODE (STRICT + LOGGED)
        // ============================================================
        var modeRaw = _cfg["SymbolSelection:Mode"];

        var mode = string.IsNullOrWhiteSpace(modeRaw)
            ? "Auto"
            : modeRaw.Trim();

        if (!mode.Equals("Auto", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("Manual", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "[SYMBOL-REGISTRY] Invalid SymbolSelection:Mode = '{mode}', fallback to AUTO",
                modeRaw);

            mode = "Auto";
        }

        _logger.LogInformation(
            "[SYMBOL-REGISTRY] Mode = {mode}",
            mode.ToUpperInvariant());

        // ============================================================
        // MANUAL MODE
        // ============================================================
        if (mode.Equals("Manual", StringComparison.OrdinalIgnoreCase))
        {
            var pinnedCfg = BlacklistFilter(GetPinnedSymbols());
            var pinnedPos = await GetPinnedByOpenPositionsSafeAsync(ct);
            var all = pinnedCfg.Concat(pinnedPos)
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .ToList();

            var hash = ComputeUniverseHash(all);

            _snapshot = new UniverseSnapshot(
                DateTime.UtcNow,
                all,
                all,
                all,
                pinnedCfg,
                pinnedPos,
                all.Count,
                all.Count,
                all.Count,
                0m,
                "MANUAL",
                hash);

            if (all.Count > 0)
                PublishUniverseIfChanged(all, hash);

            return;
        }

        // ============================================================
        // AUTO MODE (DEFAULT)
        // ============================================================
        await _refreshLock.WaitAsync(ct);
        try
        {
            var built = await BuildUniverseSnapshotHardAsync(ct);
            if (built.All.Count == 0)
                return;

            var old = _snapshot;
            _snapshot = built;

            if (HasUniverseChanged(old, built.All, built.AllHash))
                UniverseChanged?.Invoke(built.All);
        }
        finally
        {
            _refreshLock.Release();
        }
    }


    // ============================================================
    // HARD BUILD (CLEAN, DETERMINISTIC)
    // ============================================================
    private async Task<UniverseSnapshot> BuildUniverseSnapshotHardAsync(CancellationToken ct)
    {
        // ============================================================
        // CONFIG (STRICT)
        // ============================================================
        var auto = _cfg.GetSection("SymbolSelection:Auto");

        var totalCapRaw = auto.GetValue<int?>("TotalUniverseCap");
        var totalCap = totalCapRaw.HasValue && totalCapRaw.Value > 0
            ? Math.Clamp(totalCapRaw.Value, 5, 100)
            : 20;

        // ============================================================
        // PINNED (CFG + OPEN POSITIONS)
        // ============================================================
        var pinnedCfg = BlacklistFilter(GetPinnedSymbols());
        var pinnedPos = await GetPinnedByOpenPositionsSafeAsync(ct);

        pinnedCfg = NormalizeSymbols(pinnedCfg);
        pinnedPos = NormalizeSymbols(pinnedPos);

        // ============================================================
        // LIQUIDITY SNAPSHOTS
        // ============================================================
        var snapshots = await _liquidityScanner.LoadSnapshotsAsync(ct);
        if (snapshots == null || snapshots.Count == 0)
        {
            _logger.LogWarning("[SYMBOL-REGISTRY] No liquidity snapshots");
            return UniverseSnapshot.Empty() with { BtcVolBucket = "NO-SNAPSHOTS" };
        }

        // ============================================================
        // BTC VOLATILITY (SAFE)
        // ============================================================
        decimal btcVol = 0m;
        decimal btcChangeSigned = 0m;
        var btc = snapshots.FirstOrDefault(s =>
            string.Equals(s.Symbol, "BTCUSDT", StringComparison.OrdinalIgnoreCase));

        if (btc != null)
        {
            btcChangeSigned = btc.PriceChangePercent;
            btcVol = Math.Abs(btcChangeSigned);
        }

        // ============================================================
        // BTC DUMP/SQUEEZE FILTER — REAL GATE (was previously dead config)
        // ============================================================
        // Institutional practice during acute BTC-driven stress: pause
        // NEW automated entries (not touch existing pinned positions/
        // watchlist) while BTC's own 24h move is in extreme dump or
        // squeeze territory — altcoin correlation to BTC spikes sharply
        // during these windows (>0.85 correlation during high-vol
        // periods per market research), making freshly-scanned signals
        // on unrelated symbols statistically less trustworthy right
        // when the broader market is itself in a violent, BTC-driven
        // move. Thresholds: BtcDumpThreshold is negative (e.g. -5.0 =
        // pause if BTC is down 5%+ in 24h), BtcSqueezeThreshold is
        // positive (e.g. +6.0 = pause if BTC is up 6%+) — squeeze
        // threshold set wider than dump by default, reflecting crypto's
        // known asymmetry where sharp upside squeezes tend to run
        // hotter in magnitude than typical down-moves before reversing.
        bool enableBtcFilter = auto.GetValue<bool?>("EnableBtcFilter") ?? true;
        decimal dumpThreshold = auto.GetValue<decimal?>("BtcDumpThreshold") ?? -5.0m;
        decimal squeezeThreshold = auto.GetValue<decimal?>("BtcSqueezeThreshold") ?? 6.0m;
        bool btcDumpSqueezeActive = enableBtcFilter && btc != null &&
            (btcChangeSigned <= dumpThreshold || btcChangeSigned >= squeezeThreshold);

        if (btcDumpSqueezeActive)
        {
            _logger.LogWarning(
                "[SYMBOL-REGISTRY] BTC dump/squeeze filter ACTIVE: BTC 24h change={chg:F2}% (dump<={dump:F1}%, squeeze>={squeeze:F1}%) — NOTE only (universe NOT cleared) — raise BtcSqueezeThreshold if noisy",
                btcChangeSigned, dumpThreshold, squeezeThreshold);
        }

        // ============================================================
        // TRADABLE FILTER (AI / REGIME)
        // ============================================================
                // BTC dump/squeeze: WARN only — do NOT zero the auto universe.
        // User config BtcSqueezeThreshold=2% was killing all Auto symbols on mild BTC moves
        // (log: TRACKED total=2, PROC NOT_IN_ACTIVE_*). Pinned-only is not a viable bot.
        var tradable = snapshots
                .Where(s => !string.IsNullOrWhiteSpace(s.Symbol))
                .Where(s => _marketRegime.IsTradable(s.Symbol))
                .ToList() Pinned symbols (added below via pinnedCfg/pinnedPos) still flow through untouched
            : snapshots
                .Where(s => !string.IsNullOrWhiteSpace(s.Symbol))
                .Where(s => _marketRegime.IsTradable(s.Symbol))
                .ToList();

        if (tradable.Count == 0 && !btcDumpSqueezeActive)
        {
            _logger.LogWarning(
                "[SYMBOL-REGISTRY] Tradable filter empty → fallback to all snapshots");

            tradable = snapshots;
        }

        // ============================================================
        // SCORING (LONG / SHORT)
        // ============================================================
        decimal Score(SymbolMarketSnapshot s, SignalSide side)
        {
            // AI score: нормализуем 0..1
            TryNormalize01(_ai.GetSymbolScore(s.Symbol, side), 0m, 1m, out var ai);

            // Price change: учитываем падение как отрицательный фактор
            // нормализуем от -15% до +15%
            TryNormalize01(s.PriceChangePercent, -15m, 15m, out var mom);

            // Объединяем с весами
            return ai * 0.65m + mom * 0.35m;
        }

        var longSnaps = tradable
            .OrderByDescending(s => Score(s, SignalSide.Buy))
            .ToList();

        var shortSnaps = tradable
            .OrderByDescending(s => Score(s, SignalSide.Sell))
            .ToList();

        // ============================================================
        // UNIVERSE BUILD (AI CAP = 60)
        // ============================================================
        var momentumCapPercent = 100m; // или из конфига
        var longs = BlacklistFilter(
            NormalizeSymbols(
                _universeBuilder.Build(longSnaps, pinnedCfg.ToArray(), 60, 0m, 0m, momentumCapPercent)));

        var shorts = BlacklistFilter(
            NormalizeSymbols(
                _universeBuilder.Build(shortSnaps, pinnedCfg.ToArray(), 60, 0m, 0m, momentumCapPercent)));

        // ============================================================
        // FINAL UNION (PINNED FIRST, HARD CAP)
        // ============================================================
        var all = LimitUnion(
            pinnedCfg,
            pinnedPos,
            longs,
            shorts,
            totalCap);

        var hash = ComputeUniverseHash(all);

        // ============================================================
        // BTC VOL BUCKET
        // ============================================================
        var btcBucket =
            btcVol <= 1.2m ? "LOW" :
            btcVol <= 2.5m ? "MID" :
                             "HIGH";

        // ============================================================
        // SNAPSHOT
        // ============================================================
        // Pinned must always be tradeable both sides (NOT_IN_ACTIVE_* was rejecting BTC/ETH)
        var longActive = longs.Where(all.Contains).ToList();
        var shortActive = shorts.Where(all.Contains).ToList();
        foreach (var p in pinnedCfg.Concat(pinnedPos).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (all.Contains(p, StringComparer.OrdinalIgnoreCase))
            {
                if (!longActive.Contains(p, StringComparer.OrdinalIgnoreCase)) longActive.Add(p);
                if (!shortActive.Contains(p, StringComparer.OrdinalIgnoreCase)) shortActive.Add(p);
            }
        }

        return new UniverseSnapshot(
            DateTime.UtcNow,
            all,
            longActive,
            shortActive,
            pinnedCfg,
            pinnedPos,
            totalCap,
            totalCap,
            totalCap,
            btcVol,
            btcBucket,
            hash);
    }


    // ============================================================
    // MISC
    // ============================================================
    private List<string> GetPinnedSymbols()
    {
        var pinned = _cfg.GetSection("SymbolSelection:Pinned").Get<string[]>();
        return (pinned ?? Array.Empty<string>())
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<string>> GetPinnedByOpenPositionsSafeAsync(CancellationToken ct)
    {
        if (_posSource == null) return Array.Empty<string>();
        try
        {
            return BlacklistFilter(NormalizeSymbols(await _posSource.GetOpenPositionSymbolsAsync(ct)));
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}

// ============================================================
// CONTRACT
// ============================================================
public interface IOpenPositionSymbolSource
{
    Task<IReadOnlyList<string>> GetOpenPositionSymbolsAsync(CancellationToken ct);
}
