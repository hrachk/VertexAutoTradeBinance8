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

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private DateTime _lastHardRefresh = DateTime.MinValue;

    public IReadOnlyList<string> ActiveSymbols { get; private set; } = new List<string>();
    public IReadOnlyList<string> ActiveLongSymbols { get; private set; } = new List<string>();
    public IReadOnlyList<string> ActiveShortSymbols { get; private set; } = new List<string>();

    public SymbolRegistryService(
        IConfiguration cfg,
        ILogger<SymbolRegistryService> logger,
        SymbolLiquidityScanner liquidityScanner,
        SymbolUniverseBuilder universeBuilder,
        AiMarketRegimeService marketRegime,
        AiSelfLearningService ai,
        UniverseDryRunFileLogger dryRun)
    {
        _cfg = cfg;
        _logger = logger;
        _liquidityScanner = liquidityScanner;
        _universeBuilder = universeBuilder;
        _marketRegime = marketRegime;
        _ai = ai;
        _dryRun = dryRun;
    }

    private static decimal Normalize(decimal value, decimal min, decimal max)
    {
        if (max <= min) return 0.5m;
        return Math.Clamp((value - min) / (max - min), 0m, 1m);
    }

    private static IReadOnlyList<string> BlacklistFilter(IEnumerable<string> symbols)
        => symbols.Where(s => !string.Equals(s, "AIAUSDT", StringComparison.OrdinalIgnoreCase)).ToList();

    public async Task LoadAsync(CancellationToken ct)
    {
        var mode = _cfg["SymbolSelection:Mode"] ?? "Manual";

        if (string.Equals(mode, "Manual", StringComparison.OrdinalIgnoreCase))
        {
            var manual = _cfg.GetSection("SymbolSelection:Manual").Get<string[]>() ?? Array.Empty<string>();

            var list = manual
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase);

            ActiveSymbols = BlacklistFilter(list);
            ActiveLongSymbols = ActiveSymbols;
            ActiveShortSymbols = ActiveSymbols;

            _logger.LogInformation("[SYMBOL] Manual mode: {cnt} → {list}",
                ActiveSymbols.Count, string.Join(", ", ActiveSymbols));

            return;
        }

        var refreshMinutes = _cfg.GetValue<int?>("SymbolSelection:Auto:RefreshInterval") ?? 10;
        var now = DateTime.UtcNow;

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (now - _lastHardRefresh < TimeSpan.FromMinutes(refreshMinutes))
            {
                SoftHealthCheck();
                return;
            }

            _lastHardRefresh = now;
        }
        finally
        {
            _refreshLock.Release();
        }

        var auto = _cfg.GetSection("SymbolSelection:Auto");

        var pinned = BlacklistFilter(GetPinnedSymbols());
        var topVolumeCount = auto.GetValue<int>("TopVolumeCount");

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

        var baseSnapshots = await _liquidityScanner.LoadSnapshotsAsync(ct);
        if (baseSnapshots == null || baseSnapshots.Count == 0)
        {
            _logger.LogWarning("[SYMBOL] No market snapshots → skip refresh");
            return;
        }

        // Regime filter
        baseSnapshots = baseSnapshots
            .Where(s => _marketRegime.IsTradable(s.Symbol))
            .ToList();

        // BTC bias
        decimal btcBiasLong = 1m;
        decimal btcBiasShort = 1m;

        if (enableBtcFilter)
        {
            var btc = baseSnapshots.FirstOrDefault(s => s.Symbol == "BTCUSDT");
            if (btc != null)
            {
                if (btc.PriceChangePercent <= btcDump)
                {
                    btcBiasLong = 0.35m;
                    _logger.LogWarning("[SYMBOL][BTC] Dump {pct}% → LONG score reduced", btc.PriceChangePercent);
                }
                else if (btc.PriceChangePercent >= btcSqueeze)
                {
                    btcBiasShort = 0.35m;
                    _logger.LogWarning("[SYMBOL][BTC] Squeeze {pct}% → SHORT score reduced", btc.PriceChangePercent);
                }
            }
        }


        var dyn = _cfg.GetSection("SymbolSelection:DynamicCap");
        var useDynCap = dyn.GetValue<bool?>("Enabled") ?? false;

        var dynamicCap = finalCap;

        // === AI CAP ADJUSTMENT (WINRATE) ===
        var longWr = _ai.GetWinRate("LONG", lastN: 30);
        var shortWr = _ai.GetWinRate("SHORT", lastN: 30);

        int AiAdjust(int cap, decimal wr)
        {
            if (wr < 0.35m) return Math.Max(1, cap - 2);
            if (wr < 0.45m) return Math.Max(1, cap - 1);
            return cap;
        }

        var aiCapLong = AiAdjust(dynamicCap, longWr);
        var aiCapShort = AiAdjust(dynamicCap, shortWr);

        _logger.LogWarning(
            "[SYMBOL][AI-CAP] wr(L)={lwr:P0} wr(S)={swr:P0} cap={cap} → L={lcap} S={scap}",
            longWr, shortWr, dynamicCap, aiCapLong, aiCapShort);

        if (useDynCap)
        {
            var low = dyn.GetValue<decimal?>("LowVolPct") ?? 1.2m;
            var mid = dyn.GetValue<decimal?>("MidVolPct") ?? 2.5m;

            var capLow = dyn.GetValue<int?>("CapLowVol") ?? finalCap;
            var capMid = dyn.GetValue<int?>("CapMidVol") ?? finalCap;
            var capHigh = dyn.GetValue<int?>("CapHighVol") ?? Math.Max(1, Math.Min(finalCap, 4));

            // Vol proxy = abs(BTC 24h %)
            var btcVol = GetBtcVolatilityPercent(baseSnapshots);

            if (btcVol <= low) dynamicCap = capLow;
            else if (btcVol <= mid) dynamicCap = capMid;
            else dynamicCap = capHigh;

            // hard clamp: cannot exceed configured caps and TopVolumeCount
            dynamicCap = Math.Clamp(dynamicCap, 1, Math.Min(finalCap, topVolumeCount));

            _logger.LogWarning("[SYMBOL][DYN-CAP] BTC vol={vol:F2}% → cap={cap}", btcVol, dynamicCap);
        }
        else
        {
            // clamp finalCap to TopVolumeCount anyway (sanity)
            dynamicCap = Math.Clamp(finalCap, 1, topVolumeCount);
        }

        // LONG snaps scored
        var longSnaps = baseSnapshots
            .Where(s => _ai.GetRecentPnL(s.Symbol, SignalSide.Buy) > -0.15m)
            .OrderByDescending(s =>
            {
                var ai = Normalize(_ai.GetSymbolScore(s.Symbol, SignalSide.Buy), 0m, 1m);
                var mom = Normalize((decimal)Math.Abs(s.PriceChangePercent), 0m, momCap);
                return (ai * aiWeight + mom * momWeight) * btcBiasLong;
            })
            .ToList();

        // SHORT snaps scored
        var shortSnaps = baseSnapshots
            .Where(s => _ai.GetRecentPnL(s.Symbol, SignalSide.Sell) > -0.15m)
            .OrderByDescending(s =>
            {
                var ai = Normalize(_ai.GetSymbolScore(s.Symbol, SignalSide.Sell), 0m, 1m);
                var mom = Normalize((decimal)Math.Abs(s.PriceChangePercent), 0m, momCap);
                return (ai * aiWeight + mom * momWeight) * btcBiasShort;
            })
            .ToList();

        // UniverseBuilder
        var longCandidates = BlacklistFilter(_universeBuilder.Build(
            longSnaps, pinned.ToArray(), topVolumeCount, minVolLong, minPrice));

        var shortCandidates = BlacklistFilter(_universeBuilder.Build(
            shortSnaps, pinned.ToArray(), topVolumeCount, minVolShort, minPrice));


        // Stability guard (per side) + dynamic cap
        ActiveLongSymbols = ApplyStabilityGuardSide(ActiveLongSymbols, longCandidates, maxAdds)
            .Take(aiCapLong)
            .ToList();

        ActiveShortSymbols = ApplyStabilityGuardSide(ActiveShortSymbols, shortCandidates, maxAdds)
            .Take(aiCapShort)
            .ToList();




        // Union for UI/WS warmup
        ActiveSymbols = pinned
            .Concat(ActiveLongSymbols)
            .Concat(ActiveShortSymbols)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation(
            "[SYMBOL] Registry refresh: pinned={pin}, long={lng}, short={sht}, total={tot}",
            pinned.Count,
            ActiveLongSymbols.Count,
            ActiveShortSymbols.Count,
            ActiveSymbols.Count);

        _logger.LogInformation("[SYMBOL][DRY-RUN] LONG → {list}",
            string.Join(", ", ActiveLongSymbols.Take(dryRunLimit)));

        _logger.LogInformation("[SYMBOL][DRY-RUN] SHORT → {list}",
            string.Join(", ", ActiveShortSymbols.Take(dryRunLimit)));

        _dryRun.Log(new UniverseDryRunEntry(
    DateTime.UtcNow,
    GetBtcVolatilityPercent(baseSnapshots),
    GetBtcVolatilityPercent(baseSnapshots) <= 1.2m ? "LOW"
        : GetBtcVolatilityPercent(baseSnapshots) <= 2.5m ? "MID"
        : "HIGH",
    dynamicCap,
    ActiveLongSymbols,
    ActiveShortSymbols,
    pinned
));

    }

    private void SoftHealthCheck()
    {
        if (ActiveSymbols.Count == 0)
            _logger.LogWarning("[SYMBOL] ActiveSymbols empty during soft check");
        if (ActiveLongSymbols.Count == 0)
            _logger.LogWarning("[SYMBOL] ActiveLongSymbols empty during soft check");
        if (ActiveShortSymbols.Count == 0)
            _logger.LogWarning("[SYMBOL] ActiveShortSymbols empty during soft check");
    }

    private IReadOnlyList<string> ApplyStabilityGuardSide(
      IReadOnlyList<string> prev,
      IReadOnlyList<string> next,
      int maxAdds)
    {
        prev ??= Array.Empty<string>();
        next ??= Array.Empty<string>();

        if (prev.Count == 0)
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


    private decimal GetBtcVolatilityPercent(List<SymbolMarketSnapshot> snaps)
    {
        var btc = snaps.FirstOrDefault(s => s.Symbol == "BTCUSDT");
        if (btc == null) return 0m;
        return Math.Abs(btc.PriceChangePercent);
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
}
