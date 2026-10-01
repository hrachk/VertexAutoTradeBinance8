using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VertexAutoTrade.Execution;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.Services.DualMode;

public enum DualMarketMode
{
    Unknown = 0,
    Trend = 1,
    Range = 2,
    Chaos = 3
}

public enum UniverseTier
{
    Blocked = 0,
    /// <summary>Top-N by 24h volume — TREND only.</summary>
    CoreMajor = 1,
    /// <summary>Inside trend top band, outside core — TREND + RANGE spread.</summary>
    TrendLiquid = 2,
    /// <summary>Liquid mid or thin alt above min volume — RANGE only.</summary>
    SpreadAlt = 3
}

public sealed record DualModeDecision(
    bool Allow,
    DualMarketMode Mode,
    UniverseTier Tier,
    int LeverageCap,
    decimal SizeMult,
    string Reason,
    bool FlowConfirmed);

/// <summary>
/// Regime × dynamic liquidity universe × capital-flow → leverage/size.
/// Universe ranks come from live Binance 24h quote volume (SymbolLiquidityScanner).
/// </summary>
public sealed class DualModeTradingPolicy
{
    private readonly IOptionsMonitor<DualModeOptions> _opt;
    private readonly ILogger<DualModeTradingPolicy> _log;
    private readonly OiFundingTracker? _oi;
    private readonly MarketDataService? _md;
    private readonly SymbolLiquidityScanner? _scanner;

    private DualMarketMode _btcMode = DualMarketMode.Unknown;
    private DateTime _modeUtc = DateTime.MinValue;
    private string _modeDetail = "init";

    // Ranked by 24h quote volume, index 0 = highest volume
    private List<string> _ranked = new();
    private Dictionary<string, int> _rankBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, decimal> _vol24BySymbol = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _universeUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _universeGate = new(1, 1);

    private readonly ConcurrentDictionary<string, decimal> _volEma =
        new(StringComparer.OrdinalIgnoreCase);

    public DualModeTradingPolicy(
        IOptionsMonitor<DualModeOptions> opt,
        ILogger<DualModeTradingPolicy> log,
        OiFundingTracker? oi = null,
        MarketDataService? md = null,
        SymbolLiquidityScanner? scanner = null)
    {
        _opt = opt;
        _log = log;
        _oi = oi;
        _md = md;
        _scanner = scanner;
    }

    public DualMarketMode CurrentBtcMode => _btcMode;
    public string ModeDetail => _modeDetail;
    public IReadOnlyList<string> RankedUniverse => _ranked;

    public void UpdateBtcRegime(
        IReadOnlyList<decimal> closes,
        IReadOnlyList<decimal> highs,
        IReadOnlyList<decimal> lows,
        decimal? btcMove15mPct = null)
    {
        var o = _opt.CurrentValue;
        if (closes.Count < 30 || highs.Count < 30 || lows.Count < 30)
        {
            _btcMode = DualMarketMode.Unknown;
            _modeDetail = "insufficient_btc_bars";
            return;
        }

        var er = EfficiencyRatio(closes, 14);
        var atr14 = Atr(highs, lows, closes, 14);
        var atr50 = Atr(highs, lows, closes, 50);
        var atrRatio = atr50 > 0 ? atr14 / atr50 : 1m;

        DualMarketMode mode;
        if (btcMove15mPct is decimal m && Math.Abs(m) >= o.ChaosBtcMove15mPct)
        {
            mode = DualMarketMode.Chaos;
            _modeDetail = $"BTC_SPIKE {m:F2}%/15m atrR={atrRatio:F2} er={er:F2}";
        }
        else if (atrRatio >= o.ChaosAtrRatioMin && er < 0.22m)
        {
            mode = DualMarketMode.Chaos;
            _modeDetail = $"CHAOS atrR={atrRatio:F2} er={er:F2}";
        }
        else if (er >= o.TrendEfficiencyMin)
        {
            mode = DualMarketMode.Trend;
            _modeDetail = $"TREND er={er:F2} atrR={atrRatio:F2}";
        }
        else
        {
            mode = DualMarketMode.Range;
            _modeDetail = $"RANGE er={er:F2} atrR={atrRatio:F2}";
        }

        _btcMode = mode;
        _modeUtc = DateTime.UtcNow;
        _log.LogInformation("[DUAL-MODE] BTC regime → {mode} ({detail})", mode, _modeDetail);
    }

    /// <summary>
    /// Rebuild core / trend / spread bands from live 24h quote volume ranking.
    /// </summary>
    public async Task EnsureUniverseAsync(CancellationToken ct = default)
    {
        var o = _opt.CurrentValue;
        var ttl = Math.Max(60, o.UniverseRefreshSeconds);
        if (_ranked.Count > 0 && (DateTime.UtcNow - _universeUtc).TotalSeconds < ttl)
            return;

        if (_scanner == null)
        {
            _log.LogWarning("[DUAL-MODE] No SymbolLiquidityScanner — universe empty until available");
            return;
        }

        if (!await _universeGate.WaitAsync(0, ct).ConfigureAwait(false))
            return; // another refresh in flight

        try
        {
            if (_ranked.Count > 0 && (DateTime.UtcNow - _universeUtc).TotalSeconds < ttl)
                return;

            var snaps = await _scanner.LoadSnapshotsAsync(ct).ConfigureAwait(false);
            if (snaps == null || snaps.Count == 0)
            {
                _log.LogWarning("[DUAL-MODE] ticker snapshots empty");
                return;
            }

            var bl = new HashSet<string>(
                (o.Blacklist ?? Array.Empty<string>()).Select(Normalize),
                StringComparer.OrdinalIgnoreCase);

            var ordered = snaps
                .Where(s => !string.IsNullOrWhiteSpace(s.Symbol))
                .Where(s => s.Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase))
                .Where(s => s.QuoteVolume24h > 0 && s.LastPrice > 0)
                .Where(s => !bl.Contains(Normalize(s.Symbol)))
                .OrderByDescending(s => s.QuoteVolume24h)
                .ToList();

            var ranked = ordered.Select(s => Normalize(s.Symbol)).Distinct().ToList();
            var rankMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var volMap = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ordered.Count; i++)
            {
                var sym = Normalize(ordered[i].Symbol);
                if (!rankMap.ContainsKey(sym))
                    rankMap[sym] = rankMap.Count; // 0-based unique rank
                volMap[sym] = ordered[i].QuoteVolume24h;
            }

            _ranked = ranked;
            _rankBySymbol = rankMap;
            _vol24BySymbol = volMap;
            _universeUtc = DateTime.UtcNow;

            int coreN = Math.Max(1, o.CoreMajorCount);
            int trendN = Math.Max(coreN, o.TrendUniverseCount);
            var corePreview = ranked.Take(Math.Min(coreN, ranked.Count)).ToList();
            var trendPreview = ranked.Take(Math.Min(trendN, ranked.Count)).ToList();

            _log.LogInformation(
                "[DUAL-MODE] Dynamic universe n={n} coreTop={core} trendTop={trend} (by 24h quote vol)",
                ranked.Count,
                string.Join(",", corePreview),
                string.Join(",", trendPreview.Take(12)) + (trendPreview.Count > 12 ? "…" : ""));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[DUAL-MODE] universe refresh failed");
        }
        finally
        {
            _universeGate.Release();
        }
    }

    public UniverseTier ClassifySymbol(string symbol)
    {
        var o = _opt.CurrentValue;
        var u = Normalize(symbol);

        foreach (var b in o.Blacklist ?? Array.Empty<string>())
            if (Normalize(b) == u) return UniverseTier.Blocked;

        // No market data yet → fail closed for unknown (except never block BTC/ETH soft)
        if (_rankBySymbol.Count == 0)
        {
            // Fail-open only for absolute leaders so system can still trade while warming
            if (u is "BTCUSDT" or "ETHUSDT")
                return UniverseTier.CoreMajor;
            return UniverseTier.Blocked;
        }

        if (!_rankBySymbol.TryGetValue(u, out int rank))
        {
            // Not in ticker set
            return UniverseTier.Blocked;
        }

        int coreN = Math.Max(1, o.CoreMajorCount);
        int trendN = Math.Max(coreN, o.TrendUniverseCount);
        int spreadBand = coreN + Math.Max(0, o.SpreadLiquidCount);

        if (rank < coreN)
            return UniverseTier.CoreMajor;

        if (rank < trendN)
            return UniverseTier.TrendLiquid;

        // Liquid mid preferred for range (still above thin)
        if (rank < spreadBand)
            return UniverseTier.SpreadAlt;

        if (o.AllowThinAltsInRange &&
            _vol24BySymbol.TryGetValue(u, out var vol) &&
            vol >= o.MinQuoteVolume24hForRange)
            return UniverseTier.SpreadAlt;

        return UniverseTier.Blocked;
    }

    public async Task<DualModeDecision> EvaluateAsync(
        TradeSignal signal,
        decimal? lastVolume = null,
        CancellationToken ct = default)
    {
        var o = _opt.CurrentValue;
        if (!o.Enabled)
            return new DualModeDecision(true, DualMarketMode.Unknown, UniverseTier.TrendLiquid,
                o.TrendLeverageMax, 1m, "DualMode disabled — legacy path", true);

        await EnsureUniverseAsync(ct).ConfigureAwait(false);

        var symbol = signal.Symbol ?? "";
        var tier = ClassifySymbol(symbol);
        if (tier == UniverseTier.Blocked)
            return Reject(DualMarketMode.Unknown, tier, "SYMBOL_BLOCKED_OR_ILLIQUID");

        var mode = _btcMode;
        if (mode == DualMarketMode.Unknown)
            mode = DualMarketMode.Range;

        if (mode == DualMarketMode.Chaos)
            return Reject(mode, tier, "REGIME_CHAOS: " + _modeDetail);

        bool isLong = signal.Side == SignalSide.Buy;
        var flow = await BuildFlowAsync(symbol, isLong, lastVolume, ct).ConfigureAwait(false);
        bool flowOk = flow.HasCapitalConfirmation(o.MinVolumeRatio, o.MinOiDeltaAbs, o.MinBookAlign);

        string rankInfo = _rankBySymbol.TryGetValue(Normalize(symbol), out var r)
            ? $"rank={r + 1}"
            : "rank=?";

        if (mode == DualMarketMode.Trend)
        {
            if (tier != UniverseTier.CoreMajor && tier != UniverseTier.TrendLiquid)
                return Reject(mode, tier, $"TREND_TOP_LIQUID_ONLY ({rankInfo})");

            if (o.RequireFlowOnTrend && !flowOk)
            {
                _log.LogWarning(
                    "[DUAL-MODE] REJECT {sym} TREND no capital flow ({flow})",
                    symbol, flow.Summarize());
                return new DualModeDecision(false, mode, tier, o.TrendLeverageMin, 0m,
                    "NO_CAPITAL_FLOW: " + flow.Summarize(), false);
            }

            int lev = ClampLev(o.TrendLeverageMin, o.TrendLeverageMax, preferHigh: true);
            return new DualModeDecision(true, mode, tier, lev, o.TrendSizeMult,
                $"TREND+FLOW {rankInfo} " + flow.Summarize(), flowOk);
        }

        if (mode == DualMarketMode.Range)
        {
            if (tier == UniverseTier.CoreMajor)
                return Reject(mode, tier,
                    $"RANGE_NO_CORE_MAJORS ({rankInfo}) — top volume waits for TREND+flow");

            if (tier != UniverseTier.TrendLiquid && tier != UniverseTier.SpreadAlt)
                return Reject(mode, tier, $"RANGE_UNIVERSE_MISS ({rankInfo})");

            if (o.RequireFlowOnRange && !flowOk)
                return new DualModeDecision(false, mode, tier, o.RangeLeverageMin, 0m,
                    "RANGE_NO_FLOW " + flow.Summarize(), false);

            int lev = ClampLev(o.RangeLeverageMin, o.RangeLeverageMax, preferHigh: false);
            decimal size = tier == UniverseTier.TrendLiquid
                ? o.RangeLiquidSizeMult
                : o.RangeSizeMult;
            if (!flowOk)
                size = Math.Min(size, 0.25m);

            return new DualModeDecision(true, mode, tier, lev, size,
                $"RANGE_SPREAD {rankInfo} " + flow.Summarize(), flowOk);
        }

        return Reject(mode, tier, "MODE_UNKNOWN");
    }

    private DualModeDecision Reject(DualMarketMode mode, UniverseTier tier, string reason) =>
        new(false, mode, tier, 1, 0m, reason, false);

    private async Task<CapitalFlowSnapshot> BuildFlowAsync(
        string symbol, bool isLong, decimal? lastVolume, CancellationToken ct)
    {
        var snap = new CapitalFlowSnapshot
        {
            Symbol = symbol,
            IsLong = isLong,
            VolumeRatio = 1m
        };

        if (lastVolume is decimal v && v > 0)
        {
            var ema = _volEma.AddOrUpdate(symbol, v, (_, prev) => prev * 0.9m + v * 0.1m);
            snap = new CapitalFlowSnapshot
            {
                Symbol = symbol,
                IsLong = isLong,
                VolumeRatio = ema > 0 ? v / ema : 1m,
                HasVolume = true
            };
        }

        decimal oiDelta = 0, funding = 0;
        bool hasOi = false;
        if (_oi != null && _oi.TryGet(symbol, out var oiSnap))
        {
            oiDelta = oiSnap.OiDeltaPct;
            funding = oiSnap.FundingRate;
            hasOi = true;
        }

        decimal bookAlign = 0, spreadPct = 0;
        bool hasBook = false;
        if (_md != null)
        {
            try
            {
                var book = await _md.GetOrderBookAsync(symbol, 20).ConfigureAwait(false);
                if (book is { Bids.Count: > 0, Asks.Count: > 0 })
                {
                    decimal bid = book.Bids[0].price;
                    decimal ask = book.Asks[0].price;
                    decimal mid = (bid + ask) / 2m;
                    if (mid > 0 && ask >= bid)
                        spreadPct = (ask - bid) / mid;

                    decimal band = mid * 0.005m;
                    decimal bidDepth = book.Bids.Where(x => mid - x.price <= band).Sum(x => x.qty * x.price);
                    decimal askDepth = book.Asks.Where(x => x.price - mid <= band).Sum(x => x.qty * x.price);
                    decimal tot = bidDepth + askDepth;
                    if (tot > 0)
                    {
                        decimal raw = (bidDepth - askDepth) / tot;
                        bookAlign = isLong ? raw : -raw;
                        hasBook = true;
                    }
                }
            }
            catch { }
        }

        return new CapitalFlowSnapshot
        {
            Symbol = symbol,
            IsLong = isLong,
            VolumeRatio = snap.VolumeRatio,
            HasVolume = snap.HasVolume,
            OiDeltaPct = oiDelta,
            FundingRate = funding,
            HasOi = hasOi,
            BookAlign = bookAlign,
            SpreadPct = spreadPct,
            HasBook = hasBook
        };
    }

    private static int ClampLev(int min, int max, bool preferHigh)
    {
        if (max < min) (min, max) = (max, min);
        return preferHigh ? max : min;
    }

    private static string Normalize(string s) =>
        (s ?? "").Trim().ToUpperInvariant().Replace(" ", "");

    private static decimal EfficiencyRatio(IReadOnlyList<decimal> c, int n)
    {
        if (c.Count < n + 1) return 0m;
        var net = Math.Abs(c[^1] - c[^(n + 1)]);
        decimal sum = 0;
        for (int i = c.Count - n; i < c.Count; i++)
            sum += Math.Abs(c[i] - c[i - 1]);
        return sum > 0 ? net / sum : 0m;
    }

    private static decimal Atr(
        IReadOnlyList<decimal> h, IReadOnlyList<decimal> l, IReadOnlyList<decimal> c, int n)
    {
        if (c.Count < n + 1) return 0m;
        decimal sum = 0;
        int start = c.Count - n;
        for (int i = start; i < c.Count; i++)
        {
            var tr = Math.Max(h[i] - l[i],
                Math.Max(Math.Abs(h[i] - c[i - 1]), Math.Abs(l[i] - c[i - 1])));
            sum += tr;
        }
        return sum / n;
    }
}
