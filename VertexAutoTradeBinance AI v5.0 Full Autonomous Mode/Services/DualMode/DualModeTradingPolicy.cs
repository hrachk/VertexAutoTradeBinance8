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
    /// <summary>Pure major (BTC/ETH/SOL/BNB/XRP) — TREND only, never RANGE spread.</summary>
    CoreMajor = 1,
    /// <summary>Liquid mid in TrendUniverse — TREND ok; RANGE spread ok.</summary>
    TrendLiquid = 2,
    /// <summary>Explicit liquid spread list or thin alt — RANGE only.</summary>
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
/// Foundation policy: regime → universe → capital-flow → leverage/size.
/// Replaces "blind local trend on every alt" with mode-aware rules.
/// </summary>
public sealed class DualModeTradingPolicy
{
    private readonly IOptionsMonitor<DualModeOptions> _opt;
    private readonly ILogger<DualModeTradingPolicy> _log;
    private readonly OiFundingTracker? _oi;
    private readonly MarketDataService? _md;

    private DualMarketMode _btcMode = DualMarketMode.Unknown;
    private DateTime _modeUtc = DateTime.MinValue;
    private string _modeDetail = "init";

    private readonly ConcurrentDictionary<string, decimal> _volEma =
        new(StringComparer.OrdinalIgnoreCase);

    public DualModeTradingPolicy(
        IOptionsMonitor<DualModeOptions> opt,
        ILogger<DualModeTradingPolicy> log,
        OiFundingTracker? oi = null,
        MarketDataService? md = null)
    {
        _opt = opt;
        _log = log;
        _oi = oi;
        _md = md;
    }

    public DualMarketMode CurrentBtcMode => _btcMode;
    public string ModeDetail => _modeDetail;

    /// <summary>
    /// Update global mode from BTC OHLC (caller supplies recent closes/high/low, newest last).
    /// Efficiency ratio + ATR expansion → Trend / Range / Chaos.
    /// </summary>
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

    public UniverseTier ClassifySymbol(string symbol)
    {
        var o = _opt.CurrentValue;
        var u = Normalize(symbol);

        foreach (var b in o.Blacklist)
            if (Normalize(b) == u) return UniverseTier.Blocked;

        foreach (var c in o.CoreMajors)
            if (Normalize(c) == u) return UniverseTier.CoreMajor;

        foreach (var t in o.TrendUniverse)
            if (Normalize(t) == u) return UniverseTier.TrendLiquid;

        // Explicit liquid spread names (popular non-core)
        foreach (var s in o.SpreadLiquidUniverse)
            if (Normalize(s) == u) return UniverseTier.SpreadAlt;
        foreach (var s in o.SpreadUniverse)
            if (Normalize(s) == u) return UniverseTier.SpreadAlt;

        // Thin / other alts
        if (o.AllowThinAltsInRange)
            return UniverseTier.SpreadAlt;

        return UniverseTier.Blocked;
    }

    
    /// <summary>
    /// Main gate before sizing/execution.
    /// </summary>
    public async Task<DualModeDecision> EvaluateAsync(
        TradeSignal signal,
        decimal? lastVolume = null,
        CancellationToken ct = default)
    {
        var o = _opt.CurrentValue;
        if (!o.Enabled)
            return new DualModeDecision(true, DualMarketMode.Unknown, UniverseTier.TrendLiquid,
                o.TrendLeverageMax, 1m, "DualMode disabled — legacy path", true);

        var symbol = signal.Symbol ?? "";
        var tier = ClassifySymbol(symbol);
        if (tier == UniverseTier.Blocked)
            return Reject(DualMarketMode.Unknown, tier, "SYMBOL_BLACKLIST");

        // Refresh stale unknown
        var mode = _btcMode;
        if (mode == DualMarketMode.Unknown ||
            (DateTime.UtcNow - _modeUtc).TotalSeconds > Math.Max(30, o.RegimeCacheSeconds * 3))
        {
            // Keep last known; if never set, treat as Range (conservative)
            if (mode == DualMarketMode.Unknown)
                mode = DualMarketMode.Range;
        }

        if (mode == DualMarketMode.Chaos)
            return Reject(mode, tier, "REGIME_CHAOS: " + _modeDetail);

        bool isLong = signal.Side == SignalSide.Buy;
        var flow = await BuildFlowAsync(symbol, isLong, lastVolume, ct).ConfigureAwait(false);
        bool flowOk = flow.HasCapitalConfirmation(o.MinVolumeRatio, o.MinOiDeltaAbs, o.MinBookAlign);

                // --- TREND: CoreMajors + liquid mid (TrendUniverse), flow required ---
        if (mode == DualMarketMode.Trend)
        {
            if (tier != UniverseTier.CoreMajor && tier != UniverseTier.TrendLiquid)
                return Reject(mode, tier, "TREND_LIQUID_ONLY — thin alt deferred to RANGE/spread");

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
                "TREND+FLOW " + flow.Summarize(), flowOk);
        }

        // --- RANGE: NO core majors; liquid popular + thin alts for spread, small lev ---
        if (mode == DualMarketMode.Range)
        {
            if (tier == UniverseTier.CoreMajor)
                return Reject(mode, tier,
                    "RANGE_NO_CORE_MAJORS — BTC/ETH/SOL/BNB/XRP wait for TREND+flow");

            if (tier != UniverseTier.TrendLiquid && tier != UniverseTier.SpreadAlt)
                return Reject(mode, tier, "RANGE_UNIVERSE_MISS");

            if (o.RequireFlowOnRange && !flowOk)
                return new DualModeDecision(false, mode, tier, o.RangeLeverageMin, 0m,
                    "RANGE_NO_FLOW " + flow.Summarize(), false);

            int lev = ClampLev(o.RangeLeverageMin, o.RangeLeverageMax, preferHigh: false);
            // Liquid mid (in TrendUniverse or SpreadLiquid) gets slightly larger range size
            decimal size = tier == UniverseTier.TrendLiquid
                ? o.RangeLiquidSizeMult
                : o.RangeSizeMult;
            if (!flowOk)
                size = Math.Min(size, 0.25m);

            return new DualModeDecision(true, mode, tier, lev, size,
                "RANGE_SPREAD " + flow.Summarize(), flowOk);
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

        // Volume EMA
        if (lastVolume is decimal v && v > 0)
        {
            var ema = _volEma.AddOrUpdate(symbol, v, (_, prev) => prev * 0.9m + v * 0.1m);
            snap = new CapitalFlowSnapshot
            {
                Symbol = symbol,
                IsLong = isLong,
                VolumeRatio = ema > 0 ? v / ema : 1m,
                HasVolume = true,
                OiDeltaPct = snap.OiDeltaPct,
                FundingRate = snap.FundingRate,
                BookAlign = snap.BookAlign,
                SpreadPct = snap.SpreadPct,
                HasOi = snap.HasOi,
                HasBook = snap.HasBook
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
            catch
            {
                // fail-soft
            }
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
