using System.Collections.Concurrent;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.Learning;
using Microsoft.Extensions.Configuration;
using VertexAutoTradeBinance8.Services.MarketData;

namespace VertexAutoTradeBinance8.Strategy.StrategyCore;

/// <summary>
/// StrategyCore v1.5 — resilient signal generation.
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
    private const decimal Tp1Rr = 1.20m;
    private const decimal Tp2Rr = 1.70m;
    private const decimal Tp3Rr = 2.40m;
    private const decimal MinRr = Tp1Rr; // EnforceMinRr uses TP1
    private const decimal MinAtrPct = 0.0015m;
    private const decimal MaxAtrPct = 0.060m;
    private const decimal MinSlAtr = 1.30m;
    private const decimal StructurePadAtr = 0.35m;
    private const int EmaFast = 21;
    private const int EmaSlow = 50;
    private const int SwingLookback = 18;
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
            "[CORE] v1.5 bound TF={tf} REST-fallback=ON simple-trend=ON scan=30s",
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
        if (!EnforceMinRr(signal)) return (true, false, "rr");

        _cooldown[symbol] = DateTime.UtcNow;
        _log.LogInformation(
            "[CORE][{sym}] SIGNAL {side} e={e:F6} sl={sl:F6} tp1={tp:F6} conf={c:F2} {r}",
            symbol, signal.Side, signal.EntryPrice, signal.StopLoss,
            signal.TakeProfits.FirstOrDefault(), signal.Confidence, signal.Reason);

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
    private TradeSignal? TrySimpleTrend(string symbol, List<BinanceFuturesUsdtKline> k, decimal atr)
    {
        var closes = k.Select(x => x.ClosePrice).ToList();
        var emaF = EmaSeries(closes, EmaFast);
        var emaS = EmaSeries(closes, EmaSlow);
        int i = closes.Count - 1;
        if (i < EmaSlow + 3) return null;

        decimal close = closes[i], eF = emaF[i], eS = emaS[i];
        var bar = k[i];

        // Long: bullish stack + momentum vs 3 bars ago + green close
        bool longOk = eF > eS
                      && close > eF
                      && close > closes[i - 3]
                      && bar.ClosePrice >= bar.OpenPrice;

        // Short: bearish stack
        bool shortOk = eF < eS
                       && close < eF
                       && close < closes[i - 3]
                       && bar.ClosePrice <= bar.OpenPrice;

        if (longOk)
        {
            decimal sl = close - atr * MinSlAtr;
            decimal risk = close - sl;
            return Make(symbol, SignalSide.Buy, close, sl,
                BuildTpLadder(isLong: true, entry: close, risk: risk, atr: atr),
                atr, "CORE_TREND_LONG", 0.55m);
        }

        if (shortOk)
        {
            decimal sl = close + atr * MinSlAtr;
            decimal risk = sl - close;
            return Make(symbol, SignalSide.Sell, close, sl,
                BuildTpLadder(isLong: false, entry: close, risk: risk, atr: atr),
                atr, "CORE_TREND_SHORT", 0.55m);
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

        bool upTrend = eF > eS && closes[i - 1] > emaS[i - 1] && closes[i - 3] > emaS[i - 3];
        bool dnTrend = eF < eS && closes[i - 1] < emaS[i - 1] && closes[i - 3] < emaS[i - 3];
        if (!upTrend && !dnTrend) return null;

        decimal zone = Math.Max(atr * 0.50m, close * 0.0025m);
        bool touchLong = low <= eF + zone && close >= eF - zone * 0.6m;
        bool touchShort = high >= eF - zone && close <= eF + zone * 0.6m;
        bool bullReject = close > open && (close - low) >= (high - low) * 0.45m;
        bool bearReject = close < open && (high - close) >= (high - low) * 0.45m;

        if (upTrend && touchLong && bullReject)
        {
            decimal swingLow = k.Skip(Math.Max(0, k.Count - SwingLookback)).Min(x => x.LowPrice);
            decimal sl = Math.Min(swingLow, eS) - atr * StructurePadAtr;
            if (close - sl < atr * MinSlAtr) sl = close - atr * MinSlAtr;
            decimal risk = close - sl;
            if (risk <= 0) return null;
            return Make(symbol, SignalSide.Buy, close, sl,
                BuildTpLadder(isLong: true, entry: close, risk: risk, atr: atr),
                atr, "CORE_PULLBACK_LONG", 0.60m);
        }

        if (dnTrend && touchShort && bearReject)
        {
            decimal swingHigh = k.Skip(Math.Max(0, k.Count - SwingLookback)).Max(x => x.HighPrice);
            decimal sl = Math.Max(swingHigh, eS) + atr * StructurePadAtr;
            if (sl - close < atr * MinSlAtr) sl = close + atr * MinSlAtr;
            decimal risk = sl - close;
            if (risk <= 0) return null;
            return Make(symbol, SignalSide.Sell, close, sl,
                BuildTpLadder(isLong: false, entry: close, risk: risk, atr: atr),
                atr, "CORE_PULLBACK_SHORT", 0.60m);
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

        bool brokeUp = k.Skip(k.Count - 4).Any(x => x.ClosePrice > chHigh);
        bool retestLong = cur.LowPrice <= chHigh + atr * 0.30m && cur.ClosePrice > chHigh && cur.ClosePrice > cur.OpenPrice;
        if (brokeUp && retestLong)
        {
            decimal entry = cur.ClosePrice;
            decimal sl = Math.Min(cur.LowPrice, chHigh) - atr * StructurePadAtr;
            if (entry - sl < atr * MinSlAtr) sl = entry - atr * MinSlAtr;
            decimal risk = entry - sl;
            if (risk <= 0) return null;
            return Make(symbol, SignalSide.Buy, entry, sl,
                BuildTpLadder(isLong: true, entry: entry, risk: risk, atr: atr),
                atr, "CORE_BREAKOUT_LONG", 0.56m);
        }

        bool brokeDn = k.Skip(k.Count - 4).Any(x => x.ClosePrice < chLow);
        bool retestShort = cur.HighPrice >= chLow - atr * 0.30m && cur.ClosePrice < chLow && cur.ClosePrice < cur.OpenPrice;
        if (brokeDn && retestShort)
        {
            decimal entry = cur.ClosePrice;
            decimal sl = Math.Max(cur.HighPrice, chLow) + atr * StructurePadAtr;
            if (sl - entry < atr * MinSlAtr) sl = entry + atr * MinSlAtr;
            decimal risk = sl - entry;
            if (risk <= 0) return null;
            return Make(symbol, SignalSide.Sell, entry, sl,
                BuildTpLadder(isLong: false, entry: entry, risk: risk, atr: atr),
                atr, "CORE_BREAKOUT_SHORT", 0.56m);
        }
        return null;
    }

    private static TradeSignal Make(
        string symbol, SignalSide side, decimal entry, decimal sl,
        IEnumerable<decimal> tps, decimal atr, string reason, decimal confidence)
    {
        var tpList = tps.ToList();

        // Trade-memory feedback (per client): after SL → wider SL / closer TP / softer conf;
        // after wins → hold or slight ease (never more aggressive).
        try
        {
            var adj = _journal?.GetAdjustments(_clientId, symbol);
            if (adj != null && (adj.SlPadAtr != 0 || adj.TpScale != 1m || adj.ConfMult != 1m))
            {
                bool isLong = side == SignalSide.Buy;
                if (adj.SlPadAtr > 0 && atr > 0)
                {
                    if (isLong) sl -= atr * adj.SlPadAtr;
                    else sl += atr * adj.SlPadAtr;
                }
                if (adj.TpScale > 0 && adj.TpScale < 1m && tpList.Count > 0)
                {
                    for (int ti = 0; ti < tpList.Count; ti++)
                    {
                        decimal dist = tpList[ti] - entry;
                        tpList[ti] = entry + dist * adj.TpScale;
                    }
                }
                confidence = Math.Max(0.05m, confidence * adj.ConfMult);
                _log.LogInformation(
                    "[CORE-MEM] {sym} {note} sizeMult={sm:F2} slPad={sp:F2} tpScale={ts:F2} confMult={cm:F2}",
                    symbol, adj.Note, adj.SizeMult, adj.SlPadAtr, adj.TpScale, adj.ConfMult);
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
        if (risk <= 0) risk = Math.Max(atr * 0.5m, entry * 0.003m);
        if (atr <= 0) atr = risk;

        // R legs
        decimal d1 = risk * Tp1Rr;
        decimal d2 = risk * Tp2Rr;
        decimal d3 = risk * Tp3Rr;

        // Soft ATR ceilings (professional band)
        d1 = Math.Min(d1, atr * 1.55m);
        d2 = Math.Min(d2, atr * 2.35m);
        d3 = Math.Min(d3, atr * 3.20m);

        // Floors: never tighter than ~1R / progressive stack
        d1 = Math.Max(d1, risk * 1.00m);
        d2 = Math.Max(d2, d1 * 1.30m);
        d3 = Math.Max(d3, d2 * 1.25m);

        if (isLong)
            return new[] { entry + d1, entry + d2, entry + d3 };
        return new[] { entry - d1, entry - d2, entry - d3 };
    }

    private static bool EnforceMinRr(TradeSignal s)
    {
        if (s.TakeProfits == null || s.TakeProfits.Count == 0) return false;
        decimal risk = Math.Abs(s.EntryPrice - s.StopLoss);
        if (risk <= 0) return false;
        return Math.Abs(s.TakeProfits[0] - s.EntryPrice) / risk >= MinRr * 0.98m;
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
