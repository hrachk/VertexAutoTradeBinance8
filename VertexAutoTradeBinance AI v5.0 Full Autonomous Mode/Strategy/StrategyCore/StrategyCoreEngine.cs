using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.Learning;

namespace VertexAutoTradeBinance8.Strategy.StrategyCore;

/// <summary>
/// StrategyCore v1.6 — structure-first SL (HH/HL invalidation). No ATR-only stops.
///
/// Root cause of day-long silence: MarketDataFacade often returns &lt;70 bars
/// (snapshot-capped / REST backfill disabled) so EvaluateAsync exited early
/// and never emitted. v1.5:
///   - direct Binance REST fallback when buffer is thin
///   - simple trend-follow setup (fires in real trends)
///   - lower bar minimum, majors always scanned
/// </summary>
public sealed class StrategyCoreEngine
{
    private readonly ILogger<StrategyCoreEngine> _log;
    private readonly SymbolLiquidityScanner _liquidity;
    private readonly BinanceClientFactory _factory;
    private readonly TradeJournalService? _journal;
    private readonly string _clientId;
    private MarketDataFacade? _md;
    private Timer? _scanTimer;
    private int _scanBusy;
    private int _zeroEmitStreak;

    public event Action<TradeSignal>? OnSignalGenerated;

    private readonly ConcurrentDictionary<string, DateTime> _cooldown = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _lastSignalBarMs = new(StringComparer.OrdinalIgnoreCase);

    private HashSet<string> _qualitySymbols = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _qualityAtUtc = DateTime.MinValue;
    private static readonly TimeSpan QualityTtl = TimeSpan.FromMinutes(10);

    private const decimal MinAvgQuoteVol15m = 3_000m;
    // Professional mid-range R ladder (prop-desk style):
    // TP1 ≈ 1.2R — high hit-rate scale-out (past 1R to cover fees)
    // TP2 ≈ 1.7R — primary target
    // TP3 ≈ 2.4R — trend extension / runner
    // Soft ATR caps prevent "forever" targets on quiet pairs and
    // prevent micro-TPs on explosive ATR prints.
    // Professional R ladder (structure risk first — like discretionary desks)
    // Institutional R ladder: TP1 must clear fees + edge → min 1.5R
    private const decimal Tp1Rr = 1.50m;
    private const decimal Tp2Rr = 2.50m;
    private const decimal Tp3Rr = 3.50m;
    private const decimal MinRr = 1.50m;       // reject if TP1/risk < 1.5
    private const decimal MinAtrPct = 0.0015m;
    private const decimal MaxAtrPct = 0.060m;
    // Structure SL: swing ± pad; hard caps prevent 4%+ "lottery" stops
    private const decimal MinRiskAtr = 1.20m;   // ATR floor
    private const decimal MaxRiskAtr = 2.80m;   // reject if SL farther than ~2.8 ATR
    private const decimal MinRiskPct = 0.008m;  // CRITICAL: never micro-SL < 0.8% of entry
    private const decimal MaxRiskPct = 0.025m;  // reject if |entry-SL|/entry > 2.5%
    private const decimal StructurePadAtr = 0.50m; // SwingLow/High ± 0.5 ATR
    private const decimal MaxExtensionAtr = 1.20m; // no FOMO chase — was 1.80 (late entries)
    private const decimal RsiLongMax = 58m;     // no LONG if RSI15 > 58 (harder FOMO block)
    private const decimal RsiShortMin = 42m;    // no SHORT if RSI15 < 42
    private const int RsiPeriod = 14;
    private const int EmaFast = 21;
    private const int EmaSlow = 50;
    private const int SwingLookback = 40; // wider window for real swings
    private const int PivotWing = 2;      // fractal pivot L/R bars
    private const int Donchian = 20;
    private const int QualityTopN = 40;
    private const int MinBars = 55;
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30);
    private static readonly KlineInterval Tf = KlineInterval.FifteenMinutes;

    public StrategyCoreEngine(
        ILogger<StrategyCoreEngine> log,
        SymbolLiquidityScanner liquidity,
        BinanceClientFactory factory,
        TradeJournalService? journal = null,
        IConfiguration? cfg = null)
    {
        _log = log;
        _liquidity = liquidity;
        _factory = factory;
        _journal = journal;
        _clientId = cfg?["Client:Id"] ?? "client_001";
    }

    public void BindReactive(MarketDataFacade marketData)
    {
        UnbindReactive();
        _md = marketData;
        _md.WsClosedKline += OnWsClosed;
        _scanTimer = new Timer(_ => { _ = ScanQualitySafeAsync(); },
            null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
        _log.LogInformation(
            "[CORE] v1.6 bound TF={tf} REST-fallback=ON structure-SL=ON (no ATR-only stop) scan=30s",
            Tf);
    }

    public void UnbindReactive()
    {
        try { _scanTimer?.Dispose(); } catch { }
        _scanTimer = null;
        if (_md != null)
        {
            _md.WsClosedKline -= OnWsClosed;
            _md = null;
        }
    }

    private void OnWsClosed(string symbol, KlineInterval interval, BinanceFuturesUsdtKline kline)
    {
        if (interval != Tf) return;
        if (string.IsNullOrWhiteSpace(symbol)) return;
        var sym = symbol.Trim().ToUpperInvariant();
        if (!sym.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)) return;
        _ = EvaluateIfQualityAsync(sym);
    }

    private async Task EvaluateIfQualityAsync(string symbol)
    {
        try
        {
            if (!await IsQualitySymbolAsync(symbol).ConfigureAwait(false))
                return;
            await EvaluateAsync(symbol).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[CORE] evaluate {sym} failed", symbol);
        }
    }

    private async Task ScanQualitySafeAsync()
    {
        if (Interlocked.Exchange(ref _scanBusy, 1) == 1) return;
        try
        {
            await RefreshQualityUniverseAsync().ConfigureAwait(false);

            var majors = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT" };
            var batch = majors
                .Concat(_qualitySymbols.OrderBy(_ => Guid.NewGuid()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(15)
                .ToList();

            int evaluated = 0, emitted = 0, thin = 0;
            foreach (var sym in batch)
            {
                try
                {
                    var (ev, em, reason) = await EvaluateAsync(sym).ConfigureAwait(false);
                    if (ev) evaluated++;
                    if (em) emitted++;
                    if (reason == "thin_klines") thin++;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[CORE] scan {sym} failed", sym);
                }
                await Task.Delay(50).ConfigureAwait(false);
            }

            if (emitted == 0) _zeroEmitStreak++;
            else _zeroEmitStreak = 0;

            var level = _zeroEmitStreak >= 10 ? LogLevel.Warning : LogLevel.Information;
            _log.Log(level,
                "[CORE][SCAN] universe={u} batch={b} evaluated={e} emitted={sig} thin={t} zeroStreak={z}",
                _qualitySymbols.Count, batch.Count, evaluated, emitted, thin, _zeroEmitStreak);
        }
        finally { Interlocked.Exchange(ref _scanBusy, 0); }
    }

    private async Task RefreshQualityUniverseAsync()
    {
        if ((DateTime.UtcNow - _qualityAtUtc) < QualityTtl && _qualitySymbols.Count > 0)
            return;
        try
        {
            var snaps = await _liquidity.LoadSnapshotsAsync().ConfigureAwait(false);
            if (snaps == null || snaps.Count == 0) return;
            var top = snaps
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Symbol)
                            && s.Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
                            && s.QuoteVolume24h > 0)
                .OrderByDescending(s => s.QuoteVolume24h)
                .Take(QualityTopN)
                .Select(s => s.Symbol.Trim().ToUpperInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (top.Count > 0)
            {
                _qualitySymbols = top;
                _qualityAtUtc = DateTime.UtcNow;
                _log.LogInformation("[CORE] quality universe n={n} sample={s}",
                    top.Count, string.Join(",", top.Take(6)));
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[CORE] liquidity refresh failed");
        }
    }

    private async Task<bool> IsQualitySymbolAsync(string symbol)
    {
        await RefreshQualityUniverseAsync().ConfigureAwait(false);
        if (_qualitySymbols.Count == 0) return true;
        return _qualitySymbols.Contains(symbol);
    }

    private async Task<(bool evaluated, bool emitted, string reason)> EvaluateAsync(string symbol)
    {
        if (InCooldown(symbol)) return (false, false, "cooldown");

        var list = await LoadKlinesAsync(symbol).ConfigureAwait(false);
        if (list == null || list.Count < MinBars)
        {
            _log.LogWarning("[CORE][{sym}] thin klines n={n} (need {need})",
                symbol, list?.Count ?? 0, MinBars);
            return (false, false, "thin_klines");
        }

        int closedIdx = list.Count - 1;
        var barOpen = list[closedIdx].OpenTime;
        if (barOpen.Kind == DateTimeKind.Unspecified)
            barOpen = DateTime.SpecifyKind(barOpen, DateTimeKind.Utc);
        if ((DateTime.UtcNow - barOpen.ToUniversalTime()).TotalMinutes < 14.0 && list.Count >= 2)
            closedIdx = list.Count - 2;

        var closed = list[closedIdx];
        long barKey = ToMs(closed.OpenTime);
        if (_lastSignalBarMs.TryGetValue(symbol, out var prev) && prev == barKey)
            return (false, false, "same_bar");

        var slice = list.Take(closedIdx + 1).ToList();
        if (slice.Count < MinBars) return (false, false, "thin_slice");

        // Soft liquidity — log but don't hard-block majors
        if (!HasQuoteLiquidity(slice) && !IsMajor(symbol))
        {
            _lastSignalBarMs[symbol] = barKey;
            return (true, false, "liquidity");
        }

        var atr = Atr(slice, 14);
        if (atr <= 0) return (false, false, "no_atr");

        var mid = (closed.HighPrice + closed.LowPrice) / 2m;
        if (mid <= 0) return (false, false, "bad_mid");
        var atrPct = atr / mid;
        if (atrPct < MinAtrPct || atrPct > MaxAtrPct)
        {
            _lastSignalBarMs[symbol] = barKey;
            return (true, false, "atr_band");
        }

        TradeSignal? signal =
            TryPullback(symbol, slice, atr)
            ?? (IsMajor(symbol) ? TryBreakoutRetest(symbol, slice, atr) : null)
            ?? TrySimpleTrend(symbol, slice, atr);

        _lastSignalBarMs[symbol] = barKey;

        if (signal == null) return (true, false, "no_setup");

        // FOMO / exhaustion guard (15m RSI)
        if (!PassRsiGuard(slice, signal, out var rsiReason))
        {
            _log.LogInformation("[CORE][{sym}] REJECT {r}", symbol, rsiReason);
            return (true, false, "rsi_fomo");
        }

        if (!PassRiskGeometry(signal, atr, out var geoReason))
        {
            _log.LogInformation("[CORE][{sym}] REJECT {r}", symbol, geoReason);
            return (true, false, "risk_geo");
        }

        if (!EnforceMinRr(signal))
        {
            decimal risk = Math.Abs(signal.EntryPrice - signal.StopLoss);
            decimal tp1 = signal.TakeProfits is { Count: > 0 } ? signal.TakeProfits[0] : 0;
            decimal rr = risk > 0 ? Math.Abs(tp1 - signal.EntryPrice) / risk : 0;
            _log.LogInformation(
                "[CORE][{sym}] REJECT R:R too low rr={rr:F2} < {min:F2} (TP1 geometry)",
                symbol, rr, MinRr);
            return (true, false, "rr");
        }

        _cooldown[symbol] = DateTime.UtcNow;
        {
            var e0 = signal.EntryPrice;
            var sl0 = signal.StopLoss;
            var riskPx = Math.Abs(e0 - sl0);
            var riskPct = e0 > 0 ? riskPx / e0 * 100m : 0m;
            _log.LogInformation(
                "[CORE][{sym}] SIGNAL {side} e={e:F6} sl={sl:F6} tp1={tp:F6} conf={c:F2} risk={risk:F6} ({rp:F3}%) {r}",
                symbol, signal.Side, e0, sl0,
                signal.TakeProfits.FirstOrDefault(), signal.Confidence, riskPx, riskPct, signal.Reason);
        }

        OnSignalGenerated?.Invoke(signal);
        return (true, true, "ok");
    }

    /// <summary>Facade first; if thin, direct public REST (bypasses snapshot REST policy).</summary>
    private async Task<List<BinanceFuturesUsdtKline>?> LoadKlinesAsync(string symbol)
    {
        IReadOnlyList<BinanceFuturesUsdtKline>? fromMd = null;
        try
        {
            if (_md != null)
                fromMd = await _md.GetKlinesAsync(symbol, Tf, need: 120).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[CORE] facade GetKlines failed {sym}", symbol);
        }

        if (fromMd != null && fromMd.Count >= MinBars)
            return fromMd.OrderBy(k => k.OpenTime).ToList();

        // Direct REST fallback — critical for day-long silence when facade returns 20 bars
        try
        {
            using var client = _factory.CreateRestClient();
            var res = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                symbol: symbol,
                interval: Tf,
                limit: 120).ConfigureAwait(false);

            if (res.Success && res.Data != null && res.Data.Length >= MinBars)
            {
                var list = new List<BinanceFuturesUsdtKline>(res.Data.Length);
                foreach (var k in res.Data)
                {
                    if (k is BinanceFuturesUsdtKline concrete)
                        list.Add(concrete);
                }
                if (list.Count >= MinBars)
                {
                    list = list.OrderBy(x => x.OpenTime).ToList();
                    _log.LogInformation("[CORE][{sym}] REST fallback bars={n}", symbol, list.Count);
                    return list;
                }
                _log.LogWarning("[CORE][{sym}] REST klines not concrete n={n}", symbol, res.Data.Length);
            }

            _log.LogWarning("[CORE][{sym}] REST fallback failed success={s} n={n}",
                symbol, res.Success, res.Data?.Length ?? 0);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[CORE][{sym}] REST fallback exception", symbol);
        }

        return fromMd?.OrderBy(k => k.OpenTime).ToList();
    }

    /// <summary>Simple trend-follow — fires when price holds above/below EMA stack.</summary>
    /// <summary>
    /// Structure-first trend continuation. NO "SL = close ± k*ATR".
    /// Long only in HH/HL; short only in LH/LL. SL = swing invalidation ± pad.
    /// </summary>
    private TradeSignal? TrySimpleTrend(string symbol, List<BinanceFuturesUsdtKline> k, decimal atr)
    {
        if (k.Count < EmaSlow + 10) return null;

        var closes = k.Select(x => x.ClosePrice).ToList();
        var emaF = EmaSeries(closes, EmaFast);
        var emaS = EmaSeries(closes, EmaSlow);
        int i = closes.Count - 1;
        decimal close = closes[i], eF = emaF[i], eS = emaS[i];
        var bar = k[i];

        var struct_ = ReadStructure(k);
        if (struct_ == null) return null;

        // ── LONG: bullish structure only ──────────────────────────
        if (struct_.IsBullish
            && eF > eS
            && close > eS
            && bar.ClosePrice >= bar.OpenPrice)
        {
            decimal swingLow = struct_.LastSwingLow;
            // Must still be "defended" — price not already through invalidation
            if (close <= swingLow) return null;

            decimal sl = swingLow - atr * StructurePadAtr;
            decimal risk = close - sl;
            if (!RiskOk(risk, atr)) return null;
            // Late chase: entry too far above protective low → skip
            if ((close - swingLow) > atr * MaxExtensionAtr) return null;

            decimal ext = atr > 0 ? (close - swingLow) / atr : 99m;
            decimal conf = ScoreCoreConfidence(ext, bodyWithTrend: true, emaAligned: eF > eS);
            if (conf < 0.58m) return null; // do not emit weak structure longs
            return Make(symbol, SignalSide.Buy, close, sl,
                BuildTpLadder(isLong: true, entry: close, risk: risk, atr: atr),
                atr, "CORE_STRUCT_LONG", conf);
        }

        // ── SHORT: bearish structure only ─────────────────────────
        if (struct_.IsBearish
            && eF < eS
            && close < eS
            && bar.ClosePrice <= bar.OpenPrice)
        {
            decimal swingHigh = struct_.LastSwingHigh;
            if (close >= swingHigh) return null;

            decimal sl = swingHigh + atr * StructurePadAtr;
            decimal risk = sl - close;
            if (!RiskOk(risk, atr)) return null;
            if ((swingHigh - close) > atr * MaxExtensionAtr) return null;

            decimal extS = atr > 0 ? (swingHigh - close) / atr : 99m;
            decimal confS = ScoreCoreConfidence(extS, bodyWithTrend: true, emaAligned: eF < eS);
            if (confS < 0.58m) return null;
            return Make(symbol, SignalSide.Sell, close, sl,
                BuildTpLadder(isLong: false, entry: close, risk: risk, atr: atr),
                atr, "CORE_STRUCT_SHORT", confS);
        }

        return null;
    }

    private static bool HasQuoteLiquidity(List<BinanceFuturesUsdtKline> k)
    {
        if (k.Count < 20) return false;
        var last20 = k.TakeLast(20).ToList();
        decimal avgQuote = last20.Average(x => x.Volume * ((x.HighPrice + x.LowPrice) / 2m));
        return avgQuote >= MinAvgQuoteVol15m;
    }

    private static long ToMs(DateTime dt)
    {
        if (dt.Kind == DateTimeKind.Unspecified)
            dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return new DateTimeOffset(dt.ToUniversalTime()).ToUnixTimeMilliseconds();
    }

    private static bool IsMajor(string s) =>
        s.StartsWith("BTC", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("ETH", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("BNB", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("SOL", StringComparison.OrdinalIgnoreCase);

    private bool InCooldown(string symbol)
        => _cooldown.TryGetValue(symbol, out var t) && DateTime.UtcNow - t < Cooldown;

    private TradeSignal? TryPullback(string symbol, List<BinanceFuturesUsdtKline> k, decimal atr)
    {
        var closes = k.Select(x => x.ClosePrice).ToList();
        var emaF = EmaSeries(closes, EmaFast);
        var emaS = EmaSeries(closes, EmaSlow);
        int i = closes.Count - 1;
        if (i < EmaSlow + 5) return null;

        decimal eF = emaF[i], eS = emaS[i];
        var bar = k[i];
        decimal close = bar.ClosePrice, open = bar.OpenPrice;
        decimal high = bar.HighPrice, low = bar.LowPrice;

        var struct_ = ReadStructure(k);

        bool upTrend = eF > eS && closes[i - 1] > emaS[i - 1] && closes[i - 3] > emaS[i - 3];
        bool dnTrend = eF < eS && closes[i - 1] < emaS[i - 1] && closes[i - 3] < emaS[i - 3];
        if (!upTrend && !dnTrend) return null;

        // Structure filter: do not fade a broken market
        if (upTrend && struct_ != null && struct_.IsBearish) return null;
        if (dnTrend && struct_ != null && struct_.IsBullish) return null;

        decimal zone = Math.Max(atr * 0.50m, close * 0.0025m);
        bool touchLong = low <= eF + zone && close >= eF - zone * 0.6m;
        bool touchShort = high >= eF - zone && close <= eF + zone * 0.6m;
        bool bullReject = close > open && (close - low) >= (high - low) * 0.45m;
        bool bearReject = close < open && (high - close) >= (high - low) * 0.45m;

        if (upTrend && touchLong && bullReject)
        {
            // Invalidation = last structural swing low (prefer structure over raw window min)
            decimal swingLow = struct_?.LastSwingLow
                ?? k.Skip(Math.Max(0, k.Count - SwingLookback)).Min(x => x.LowPrice);
            swingLow = Math.Min(swingLow, eS);
            decimal sl = swingLow - atr * StructurePadAtr;
            decimal risk = close - sl;
            if (!RiskOk(risk, atr)) return null;
            if ((close - swingLow) > atr * MaxExtensionAtr) return null;

            return Make(symbol, SignalSide.Buy, close, sl,
                BuildTpLadder(isLong: true, entry: close, risk: risk, atr: atr),
                atr, "CORE_PULLBACK_LONG", 0.64m);
        }

        if (dnTrend && touchShort && bearReject)
        {
            decimal swingHigh = struct_?.LastSwingHigh
                ?? k.Skip(Math.Max(0, k.Count - SwingLookback)).Max(x => x.HighPrice);
            swingHigh = Math.Max(swingHigh, eS);
            decimal sl = swingHigh + atr * StructurePadAtr;
            decimal risk = sl - close;
            if (!RiskOk(risk, atr)) return null;
            if ((swingHigh - close) > atr * MaxExtensionAtr) return null;

            return Make(symbol, SignalSide.Sell, close, sl,
                BuildTpLadder(isLong: false, entry: close, risk: risk, atr: atr),
                atr, "CORE_PULLBACK_SHORT", 0.64m);
        }
        return null;
    }

    private TradeSignal? TryBreakoutRetest(string symbol, List<BinanceFuturesUsdtKline> k, decimal atr)
    {
        if (k.Count < Donchian + 6) return null;
        var window = k.Skip(k.Count - Donchian - 3).Take(Donchian).ToList();
        if (window.Count < Donchian) return null;
        decimal chHigh = window.Max(x => x.HighPrice);
        decimal chLow = window.Min(x => x.LowPrice);
        var cur = k[^1];
        decimal entry = cur.ClosePrice;

        bool brokeUp = k.Skip(k.Count - 4).Any(x => x.ClosePrice > chHigh);
        bool brokeDn = k.Skip(k.Count - 4).Any(x => x.ClosePrice < chLow);

        // Retest long: broke up, now sitting back near prior high, SL under retest low / range
        if (brokeUp && entry >= chHigh - atr * 0.35m && entry <= chHigh + atr * 0.80m
            && cur.ClosePrice >= cur.OpenPrice)
        {
            decimal sl = Math.Min(cur.LowPrice, chHigh) - atr * StructurePadAtr;
            // Prefer last swing low if tighter-but-valid structure exists under entry
            var st = ReadStructure(k);
            if (st != null && st.LastSwingLow < entry && st.LastSwingLow > sl - atr)
                sl = st.LastSwingLow - atr * StructurePadAtr;

            decimal risk = entry - sl;
            if (!RiskOk(risk, atr)) return null;

            return Make(symbol, SignalSide.Buy, entry, sl,
                BuildTpLadder(isLong: true, entry: entry, risk: risk, atr: atr),
                atr, "CORE_BREAKOUT_LONG", 0.60m);
        }

        if (brokeDn && entry <= chLow + atr * 0.35m && entry >= chLow - atr * 0.80m
            && cur.ClosePrice <= cur.OpenPrice)
        {
            decimal sl = Math.Max(cur.HighPrice, chLow) + atr * StructurePadAtr;
            var st = ReadStructure(k);
            if (st != null && st.LastSwingHigh > entry && st.LastSwingHigh < sl + atr)
                sl = st.LastSwingHigh + atr * StructurePadAtr;

            decimal risk = sl - entry;
            if (!RiskOk(risk, atr)) return null;

            return Make(symbol, SignalSide.Sell, entry, sl,
                BuildTpLadder(isLong: false, entry: entry, risk: risk, atr: atr),
                atr, "CORE_BREAKOUT_SHORT", 0.60m);
        }
        return null;
    }

    // ── Structure helpers (HH/HL vs LH/LL) ──────────────────────────

    private sealed class MarketStructure
    {
        public decimal LastSwingHigh { get; init; }
        public decimal PrevSwingHigh { get; init; }
        public decimal LastSwingLow { get; init; }
        public decimal PrevSwingLow { get; init; }
        public bool IsBullish => LastSwingHigh > PrevSwingHigh && LastSwingLow > PrevSwingLow;
        public bool IsBearish => LastSwingHigh < PrevSwingHigh && LastSwingLow < PrevSwingLow;
    }

    private static MarketStructure? ReadStructure(List<BinanceFuturesUsdtKline> k)
    {
        var highs = FindPivots(k, isHigh: true);
        var lows = FindPivots(k, isHigh: false);
        if (highs.Count < 2 || lows.Count < 2) return null;

        var h1 = highs[^1];
        var h0 = highs[^2];
        var l1 = lows[^1];
        var l0 = lows[^2];

        return new MarketStructure
        {
            LastSwingHigh = h1.price,
            PrevSwingHigh = h0.price,
            LastSwingLow = l1.price,
            PrevSwingLow = l0.price
        };
    }

    private static List<(int idx, decimal price)> FindPivots(List<BinanceFuturesUsdtKline> k, bool isHigh)
    {
        var list = new List<(int, decimal)>();
        int w = PivotWing;
        // Confirmed pivots only (need `w` bars to the right) — skip last `w` bars
        int last = k.Count - 1 - w;
        for (int i = w; i <= last; i++)
        {
            bool ok = true;
            decimal p = isHigh ? k[i].HighPrice : k[i].LowPrice;
            for (int j = i - w; j <= i + w; j++)
            {
                if (j == i) continue;
                decimal q = isHigh ? k[j].HighPrice : k[j].LowPrice;
                if (isHigh)
                {
                    if (q > p) { ok = false; break; }
                }
                else
                {
                    if (q < p) { ok = false; break; }
                }
            }
            if (ok) list.Add((i, p));
        }
        // Keep only recent pivots inside lookback window
        int minIdx = Math.Max(0, k.Count - SwingLookback);
        return list.Where(x => x.Item1 >= minIdx).ToList();
    }


    private static decimal EnforceMinRiskSl(bool isLong, decimal entry, decimal sl, decimal atr)
    {
        if (entry <= 0) return sl;
        decimal risk = Math.Abs(entry - sl);
        // Floor: max(ATR×MinRiskAtr, entry×MinRiskPct) — kills 0.23% micro-stops on BTC
        decimal minByAtr = atr > 0 ? atr * MinRiskAtr : 0m;
        decimal minByPct = entry * MinRiskPct;
        decimal minRisk = Math.Max(minByAtr, minByPct);
        decimal maxRisk = entry * MaxRiskPct;
        if (maxRisk > 0 && minRisk > maxRisk)
            minRisk = maxRisk; // if conflict, prefer not exploding size; PassRiskGeometry may reject
        if (risk >= minRisk) return sl;
        return isLong ? entry - minRisk : entry + minRisk;
    }

    private static bool RiskOk(decimal risk, decimal atr)
    {
        if (risk <= 0 || atr <= 0) return false;
        if (risk < atr * 0.70m) return false; // micro noise SL
        if (risk > atr * MaxRiskAtr) return false; // too wide vs ATR
        return true;
    }

    private static bool PassRiskGeometry(TradeSignal s, decimal atr, out string reason)
    {
        reason = "";
        decimal entry = s.EntryPrice;
        decimal risk = Math.Abs(entry - s.StopLoss);
        if (entry <= 0 || risk <= 0)
        {
            reason = "bad entry/SL";
            return false;
        }
        decimal riskPct = risk / entry;
        if (riskPct < MinRiskPct * 0.98m)
        {
            reason = $"MICRO_SL riskPct={riskPct:P2} < {MinRiskPct:P1} (noise stop banned)";
            return false;
        }
        if (riskPct > MaxRiskPct)
        {
            reason = $"SL too far riskPct={riskPct:P2} > {MaxRiskPct:P1}";
            return false;
        }
        if (atr > 0 && risk > atr * MaxRiskAtr)
        {
            reason = $"SL > {MaxRiskAtr:F1}×ATR";
            return false;
        }
        return true;
    }

    private static bool PassRsiGuard(List<BinanceFuturesUsdtKline> k, TradeSignal s, out string reason)
    {
        reason = "";
        // Include forming bar: last kline close is current/last traded (mark proxy)
        decimal rsi = CalcRsi(k, RsiPeriod);
        if (rsi <= 0) return true;
        bool isLong = s.Side == SignalSide.Buy;
        if (isLong && rsi > RsiLongMax)
        {
            reason = $"RSI FOMO LONG ({rsi:F1}) > {RsiLongMax}";
            return false;
        }
        if (!isLong && rsi < RsiShortMin)
        {
            reason = $"RSI FOMO SHORT ({rsi:F1}) < {RsiShortMin}";
            return false;
        }
        return true;
    }

    /// <summary>Wilder RSI on closes; last bar = live/forming close so intrabar heat is visible.</summary>
    private static decimal CalcRsi(List<BinanceFuturesUsdtKline> k, int period)
    {
        if (k == null || k.Count < period + 2) return 0;
        // Seed SMA of first `period` deltas
        decimal avgGain = 0, avgLoss = 0;
        int seedEnd = k.Count - period;
        if (seedEnd < 1) return 0;
        int seedStart = seedEnd - period;
        if (seedStart < 1) seedStart = 1;
        int n = 0;
        for (int i = seedStart; i < seedEnd; i++)
        {
            decimal d = k[i].ClosePrice - k[i - 1].ClosePrice;
            if (d >= 0) avgGain += d; else avgLoss -= d;
            n++;
        }
        if (n <= 0) return 0;
        avgGain /= n;
        avgLoss /= n;
        for (int i = seedEnd; i < k.Count; i++)
        {
            decimal d = k[i].ClosePrice - k[i - 1].ClosePrice;
            decimal g = d > 0 ? d : 0;
            decimal l = d < 0 ? -d : 0;
            avgGain = (avgGain * (period - 1) + g) / period;
            avgLoss = (avgLoss * (period - 1) + l) / period;
        }
        if (avgLoss <= 0) return 100m;
        if (avgGain <= 0) return 0m;
        decimal rs = avgGain / avgLoss;
        return 100m - (100m / (1m + rs));
    }

    
    /// <summary>
    /// Dynamic confidence: base 0.55 + structure quality. Only high scores clear MinExecute 0.58.
    /// extensionAtr = distance from swing / ATR (smaller = cleaner retest entry).
    /// </summary>
    private static decimal ScoreCoreConfidence(decimal extensionAtr, bool bodyWithTrend, bool emaAligned)
    {
        decimal conf = 0.55m;
        if (extensionAtr <= 0.60m) conf += 0.08m;      // tight retest
        else if (extensionAtr <= 1.00m) conf += 0.04m;
        else if (extensionAtr > 1.10m) conf -= 0.04m; // near extension cap
        if (bodyWithTrend) conf += 0.03m;
        if (emaAligned) conf += 0.03m;
        if (conf < 0.50m) conf = 0.50m;
        if (conf > 0.72m) conf = 0.72m;
        return conf;
    }

    private TradeSignal Make(
        string symbol, SignalSide side, decimal entry, decimal sl,
        IEnumerable<decimal> tps, decimal atr, string reason, decimal confidence)
    {
        var tpList = tps.ToList();

        bool isLongSide = side == SignalSide.Buy;
        sl = EnforceMinRiskSl(isLongSide, entry, sl, atr);
        {
            decimal fr = Math.Abs(entry - sl);
            if (fr > 0 && atr > 0)
                tpList = BuildTpLadder(isLongSide, entry, fr, atr).ToList();
        }

        // Trade-memory: after SL on THIS symbol → smarter SL/TP only. NEVER cut confidence.
        try
        {
            var adj = _journal?.GetAdjustments(_clientId, symbol);
            if (adj != null && (adj.SlPadAtr > 0 || (adj.TpScale > 0 && adj.TpScale < 1m)))
            {
                bool isLong = side == SignalSide.Buy;
                decimal riskBefore = Math.Abs(entry - sl);
                if (adj.SlPadAtr > 0 && atr > 0)
                {
                    if (isLong) sl -= atr * adj.SlPadAtr;
                    else sl += atr * adj.SlPadAtr;
                }
                decimal riskAfter = Math.Abs(entry - sl);
                if (tpList.Count > 0)
                {
                    decimal scale = (adj.TpScale > 0 && adj.TpScale < 1m) ? adj.TpScale : 1m;
                    if (riskBefore > 0 && riskAfter > riskBefore && adj.TpScale > 0 && adj.TpScale < 1m)
                        scale = Math.Min(1.15m, adj.TpScale * (riskAfter / riskBefore));
                    for (int ti = 0; ti < tpList.Count; ti++)
                    {
                        decimal dist = tpList[ti] - entry;
                        tpList[ti] = entry + dist * scale;
                    }
                }
                _log.LogInformation(
                    "[CORE-MEM] {sym} {note} slPadAtr={sp:F2} tpScale={ts:F2} conf untouched={cf:F2}",
                    symbol, adj.Note, adj.SlPadAtr, adj.TpScale, confidence);
                decimal fr2 = Math.Abs(entry - sl);
                if (fr2 > 0 && atr > 0)
                    tpList = BuildTpLadder(isLongSide, entry, fr2, atr).ToList();
            }
        }
        catch { /* never block signal emit */ }


        return new TradeSignal
        {
            Symbol = symbol.ToUpperInvariant(),
            Side = side,
            EntryPrice = entry,
            StopLoss = sl,
            TakeProfits = tpList,
            TakeProfit = tpList.Count > 0 ? tpList.First() : entry,
            Atr = atr,
            Timeframe = "FifteenMinutes",
            Time = DateTime.UtcNow,
            Reason = reason,
            Confidence = confidence,
            AiQuality = confidence,
            IsSuperSignal = confidence >= 0.68m
        };
    }


    /// <summary>
    /// Golden-middle TP ladder used by discretionary / prop-style desks.
    /// R-multiples first, then soft ATR ceilings so targets stay reachable
    /// within a realistic swing (not "days to TP1" on low-vol alts, not
    /// micro-scalp on high-vol names).
    /// </summary>
    private static decimal[] BuildTpLadder(bool isLong, decimal entry, decimal risk, decimal atr)
    {
        // Risk is structural. TP = pure R multiples (1R / 2R / 3R).
        // Soft ATR ceiling only prevents absurd targets on dead markets —
        // never shrinks TP below 1R.
        if (risk <= 0) risk = Math.Max(atr * 0.5m, entry * 0.003m);
        if (atr <= 0) atr = risk;

        decimal d1 = risk * Tp1Rr;
        decimal d2 = risk * Tp2Rr;
        decimal d3 = risk * Tp3Rr;

        // Soft ceiling: do not demand > ~4 ATR for TP3 on quiet pairs
        d2 = Math.Min(d2, Math.Max(d1 * 1.5m, atr * 3.0m));
        d3 = Math.Min(d3, Math.Max(d2 * 1.25m, atr * 4.5m));

        // Floors stay R-based (institutional min 1.5R to TP1)
        d1 = Math.Max(d1, risk * Tp1Rr);
        d2 = Math.Max(d2, risk * Tp2Rr);
        d3 = Math.Max(d3, risk * Tp3Rr);

        if (isLong)
            return new[] { entry + d1, entry + d2, entry + d3 };
        return new[] { entry - d1, entry - d2, entry - d3 };
    }

    private static bool EnforceMinRr(TradeSignal s)
    {
        if (s.TakeProfits == null || s.TakeProfits.Count == 0) return false;
        decimal risk = Math.Abs(s.EntryPrice - s.StopLoss);
        if (risk <= 0) return false;
        return Math.Abs(s.TakeProfits[0] - s.EntryPrice) / risk >= MinRr * 0.999m;
    }

    private static decimal Atr(List<BinanceFuturesUsdtKline> k, int period)
    {
        if (k.Count < period + 2) return 0;
        var trs = new List<decimal>();
        for (int i = 1; i < k.Count; i++)
        {
            decimal h = k[i].HighPrice, l = k[i].LowPrice, pc = k[i - 1].ClosePrice;
            trs.Add(Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc))));
        }
        return trs.Count < period ? 0 : trs.TakeLast(period).Average();
    }

    private static List<decimal> EmaSeries(List<decimal> src, int period)
    {
        var ema = new List<decimal>(src.Count);
        if (src.Count == 0) return ema;
        decimal k = 2m / (period + 1);
        decimal prev = src[0];
        for (int i = 0; i < src.Count; i++)
        {
            prev = i == 0 ? src[0] : (src[i] - prev) * k + prev;
            ema.Add(prev);
        }
        return ema;
    }
}
