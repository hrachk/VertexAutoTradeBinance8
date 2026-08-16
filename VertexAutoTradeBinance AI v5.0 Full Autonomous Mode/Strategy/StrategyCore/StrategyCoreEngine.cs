using System.Collections.Concurrent;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.MarketData;

namespace VertexAutoTradeBinance8.Strategy.StrategyCore;

/// <summary>
/// StrategyCore v1.3 — closed-bar evaluation + REST scan.
/// Fix: v1.2 evaluated the FORMING candle (list[^1]) so setups almost never
/// confirmed; now uses last CLOSED 15m bar. Auto mode also gets legacy trend
/// fallback in the router so the pipeline cannot go silent.
/// </summary>
public sealed class StrategyCoreEngine
{
    private readonly ILogger<StrategyCoreEngine> _log;
    private MarketDataFacade? _md;
    private Timer? _scanTimer;
    private int _scanBusy;

    public event Action<TradeSignal>? OnSignalGenerated;

    private readonly ConcurrentDictionary<string, DateTime> _cooldown = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _lastSignalBarMs = new(StringComparer.OrdinalIgnoreCase);

    private const decimal MinRr = 1.8m;
    private const decimal MinAtrPct = 0.0020m;
    private const decimal MaxAtrPct = 0.050m;
    private const decimal MinSlAtr = 1.50m;
    private const decimal StructurePadAtr = 0.40m;
    private const int EmaFast = 21;
    private const int EmaSlow = 50;
    private const int SwingLookback = 18;
    private const int Donchian = 20;
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(45);
    private static readonly KlineInterval Tf = KlineInterval.FifteenMinutes;

    private static readonly HashSet<string> Allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "BTCUSDT", "ETHUSDT", "BNBUSDT", "SOLUSDT", "XRPUSDT",
        "DOGEUSDT", "ADAUSDT", "AVAXUSDT", "LINKUSDT", "DOTUSDT",
        "LTCUSDT", "BCHUSDT", "NEARUSDT", "ATOMUSDT", "UNIUSDT",
        "AAVEUSDT", "OPUSDT", "ARBUSDT", "SUIUSDT", "TIAUSDT",
        "INJUSDT", "APTUSDT", "FILUSDT", "RENDERUSDT", "WLDUSDT",
        "TONUSDT", "TRXUSDT", "XMRUSDT", "ETCUSDT", "SEIUSDT"
    };

    public StrategyCoreEngine(ILogger<StrategyCoreEngine> log) => _log = log;

    public void BindReactive(MarketDataFacade marketData)
    {
        UnbindReactive();
        _md = marketData;
        _md.WsClosedKline += OnWsClosed;
        _scanTimer = new Timer(_ => { _ = ScanAllowlistSafeAsync(); },
            null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45));
        _log.LogInformation("[CORE] v1.3 bound TF={tf} allowlist={n} CLOSED-bar + REST scan", Tf, Allowlist.Count);
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
        if (!Allowlist.Contains(sym)) return;
        _ = EvaluateAsync(sym);
    }

    private async Task ScanAllowlistSafeAsync()
    {
        if (Interlocked.Exchange(ref _scanBusy, 1) == 1) return;
        try
        {
            if (_md == null) return;
            int evaluated = 0, emitted = 0;
            var batch = Allowlist.OrderBy(_ => Guid.NewGuid()).Take(12).ToList();
            foreach (var sym in batch)
            {
                try
                {
                    var (ev, em) = await EvaluateAsync(sym);
                    if (ev) evaluated++;
                    if (em) emitted++;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "[CORE] scan {sym} failed", sym);
                }
                await Task.Delay(60);
            }
            _log.LogInformation("[CORE][SCAN] batch={b} evaluated={e} emitted={sig}",
                batch.Count, evaluated, emitted);
        }
        finally { Interlocked.Exchange(ref _scanBusy, 0); }
    }

    private async Task<(bool evaluated, bool emitted)> EvaluateAsync(string symbol)
    {
        if (_md == null) return (false, false);
        if (InCooldown(symbol)) return (false, false);

        var klines = await _md.GetKlinesAsync(symbol, Tf, need: 120);
        if (klines == null || klines.Count < 70) return (false, false);

        var list = klines.OrderBy(k => k.OpenTime).ToList();

        // ★ CRITICAL FIX: use last CLOSED bar, not the forming candle
        int closedIdx = list.Count - 1;
        var barOpen = list[closedIdx].OpenTime;
        if (barOpen.Kind == DateTimeKind.Unspecified)
            barOpen = DateTime.SpecifyKind(barOpen, DateTimeKind.Utc);
        var ageMin = (DateTime.UtcNow - barOpen.ToUniversalTime()).TotalMinutes;
        if (ageMin < 14.0 && list.Count >= 2)
            closedIdx = list.Count - 2;

        var closed = list[closedIdx];
        long barKey = ToMs(closed.OpenTime);

        // One signal per closed bar
        if (_lastSignalBarMs.TryGetValue(symbol, out var prev) && prev == barKey)
            return (false, false);

        // Slice ending at closed bar for indicators
        var slice = list.Take(closedIdx + 1).ToList();
        if (slice.Count < 70) return (false, false);

        var atr = Atr(slice, 14);
        if (atr <= 0) return (false, false);

        var mid = (closed.HighPrice + closed.LowPrice) / 2m;
        if (mid <= 0) return (false, false);
        var atrPct = atr / mid;
        if (atrPct < MinAtrPct || atrPct > MaxAtrPct)
        {
            _lastSignalBarMs[symbol] = barKey; // don't re-check this bar
            return (true, false);
        }

        if (!HasVolumeConfirm(slice))
        {
            _lastSignalBarMs[symbol] = barKey;
            return (true, false);
        }

        TradeSignal? signal = TryPullback(symbol, slice, atr);
        if (signal == null && IsBtcEth(symbol))
            signal = TryBreakoutRetest(symbol, slice, atr);

        _lastSignalBarMs[symbol] = barKey;

        if (signal == null) return (true, false);
        if (!EnforceMinRr(signal)) return (true, false);

        _cooldown[symbol] = DateTime.UtcNow;
        _log.LogInformation(
            "[CORE][{sym}] SIGNAL {side} e={e:F6} sl={sl:F6} tp1={tp:F6} conf={c:F2} {r}",
            symbol, signal.Side, signal.EntryPrice, signal.StopLoss,
            signal.TakeProfits.FirstOrDefault(), signal.Confidence, signal.Reason);

        OnSignalGenerated?.Invoke(signal);
        return (true, true);
    }

    private static long ToMs(DateTime dt)
    {
        if (dt.Kind == DateTimeKind.Unspecified)
            dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return new DateTimeOffset(dt.ToUniversalTime()).ToUnixTimeMilliseconds();
    }

    private static bool IsBtcEth(string s) =>
        s.StartsWith("BTC", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("ETH", StringComparison.OrdinalIgnoreCase);

    private static bool HasVolumeConfirm(List<BinanceFuturesUsdtKline> k)
    {
        if (k.Count < 25) return false;
        var avg = k.Skip(k.Count - 21).Take(20).Average(x => x.Volume);
        if (avg <= 0) return false;
        return k[^1].Volume >= avg * 0.50m;
    }

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
                new[] { close + risk * MinRr, close + risk * (MinRr + 0.8m), close + risk * (MinRr + 1.5m) },
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
                new[] { close - risk * MinRr, close - risk * (MinRr + 0.8m), close - risk * (MinRr + 1.5m) },
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
        bool retestLong = cur.LowPrice <= chHigh + atr * 0.30m
                          && cur.ClosePrice > chHigh
                          && cur.ClosePrice > cur.OpenPrice;

        if (brokeUp && retestLong)
        {
            decimal entry = cur.ClosePrice;
            decimal sl = Math.Min(cur.LowPrice, chHigh) - atr * StructurePadAtr;
            if (entry - sl < atr * MinSlAtr) sl = entry - atr * MinSlAtr;
            decimal risk = entry - sl;
            if (risk <= 0) return null;
            return Make(symbol, SignalSide.Buy, entry, sl,
                new[] { entry + risk * MinRr, entry + risk * (MinRr + 0.7m), entry + risk * (MinRr + 1.3m) },
                atr, "CORE_BREAKOUT_LONG", 0.56m);
        }

        bool brokeDn = k.Skip(k.Count - 4).Any(x => x.ClosePrice < chLow);
        bool retestShort = cur.HighPrice >= chLow - atr * 0.30m
                           && cur.ClosePrice < chLow
                           && cur.ClosePrice < cur.OpenPrice;

        if (brokeDn && retestShort)
        {
            decimal entry = cur.ClosePrice;
            decimal sl = Math.Max(cur.HighPrice, chLow) + atr * StructurePadAtr;
            if (sl - entry < atr * MinSlAtr) sl = entry + atr * MinSlAtr;
            decimal risk = sl - entry;
            if (risk <= 0) return null;
            return Make(symbol, SignalSide.Sell, entry, sl,
                new[] { entry - risk * MinRr, entry - risk * (MinRr + 0.7m), entry - risk * (MinRr + 1.3m) },
                atr, "CORE_BREAKOUT_SHORT", 0.56m);
        }

        return null;
    }

    private static TradeSignal Make(
        string symbol, SignalSide side, decimal entry, decimal sl,
        IEnumerable<decimal> tps, decimal atr, string reason, decimal confidence)
    {
        var tpList = tps.ToList();
        return new TradeSignal
        {
            Symbol = symbol.ToUpperInvariant(),
            Side = side,
            EntryPrice = entry,
            StopLoss = sl,
            TakeProfits = tpList,
            TakeProfit = tpList.First(),
            Atr = atr,
            Timeframe = "FifteenMinutes",
            Time = DateTime.UtcNow,
            Reason = reason,
            Confidence = confidence,
            AiQuality = confidence,
            IsSuperSignal = confidence >= 0.68m
        };
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
