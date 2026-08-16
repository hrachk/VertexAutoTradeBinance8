using System.Collections.Concurrent;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.MarketData;

namespace VertexAutoTradeBinance8.Strategy.StrategyCore;

/// <summary>
/// StrategyCore v1.2 — liquid majors, wider SL, REST periodic scan.
///
/// v1.1 only evaluated on WsClosedKline(15m). If a symbol was not in the
/// WS universe, it never produced signals → empty live_signals for days.
/// v1.2 adds a 60s REST scan over the allowlist so CORE does not depend
/// on WS subscription coverage.
/// </summary>
public sealed class StrategyCoreEngine
{
    private readonly ILogger<StrategyCoreEngine> _log;
    private MarketDataFacade? _md;
    private Timer? _scanTimer;
    private int _scanBusy;

    public event Action<TradeSignal>? OnSignalGenerated;

    private readonly ConcurrentDictionary<string, DateTime> _cooldown = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _lastBarOpenMs = new(StringComparer.OrdinalIgnoreCase);

    private const decimal MinRr = 2.0m;
    private const decimal MinAtrPct = 0.0025m;
    private const decimal MaxAtrPct = 0.040m;
    private const decimal MinSlAtr = 1.60m;
    private const decimal StructurePadAtr = 0.45m;
    private const int EmaFast = 21;
    private const int EmaSlow = 50;
    private const int SwingLookback = 20;
    private const int Donchian = 20;
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(60);
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

        // CRITICAL: do not rely only on WS coverage — scan allowlist via REST
        _scanTimer = new Timer(_ =>
        {
            _ = ScanAllowlistSafeAsync();
        }, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60));

        _log.LogInformation(
            "[CORE] v1.2 bound TF={tf} allowlist={n} minSlAtr={sl} REST-scan=60s",
            Tf, Allowlist.Count, MinSlAtr);
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
        _ = EvaluateAsync(sym, forceBarKey: (kline.OpenTime.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(DateTime.SpecifyKind(kline.OpenTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds()
            : new DateTimeOffset(kline.OpenTime).ToUnixTimeMilliseconds()));
    }

    private async Task ScanAllowlistSafeAsync()
    {
        if (Interlocked.Exchange(ref _scanBusy, 1) == 1) return;
        try
        {
            if (_md == null) return;
            int evaluated = 0, emitted = 0, skippedCd = 0, skippedBar = 0;

            // Round-robin a subset each tick to stay under REST limits
            var batch = Allowlist.OrderBy(_ => Guid.NewGuid()).Take(10).ToList();
            foreach (var sym in batch)
            {
                try
                {
                    var (didEval, didEmit, why) = await EvaluateAsync(sym, forceBarKey: null);
                    if (didEval) evaluated++;
                    if (didEmit) emitted++;
                    if (why == "cooldown") skippedCd++;
                    if (why == "same_bar") skippedBar++;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "[CORE] scan {sym} failed", sym);
                }
                await Task.Delay(80);
            }

            _log.LogInformation(
                "[CORE][SCAN] batch={b} evaluated={e} emitted={sig} cooldown={cd} sameBar={sb}",
                batch.Count, evaluated, emitted, skippedCd, skippedBar);
        }
        finally
        {
            Interlocked.Exchange(ref _scanBusy, 0);
        }
    }

    /// <returns>(evaluated, emitted, reasonIfSkipped)</returns>
    private async Task<(bool evaluated, bool emitted, string why)> EvaluateAsync(
        string symbol, long? forceBarKey)
    {
        if (_md == null) return (false, false, "no_md");
        if (InCooldown(symbol)) return (false, false, "cooldown");

        var klines = await _md.GetKlinesAsync(symbol, Tf, need: 120);
        if (klines == null || klines.Count < 70) return (false, false, "no_klines");

        var list = klines.OrderBy(k => k.OpenTime).ToList();
        var last = list[^1];
        long barKey = last.OpenTime.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(DateTime.SpecifyKind(last.OpenTime, DateTimeKind.Utc)).ToUnixTimeMilliseconds()
            : new DateTimeOffset(last.OpenTime).ToUnixTimeMilliseconds();

        // Only act once per closed/forming bar key unless WS forced same key
        if (forceBarKey == null)
        {
            if (_lastBarOpenMs.TryGetValue(symbol, out var prev) && prev == barKey)
                return (false, false, "same_bar");
        }

        var atr = Atr(list, 14);
        if (atr <= 0) return (false, false, "no_atr");

        var mid = (last.HighPrice + last.LowPrice) / 2m;
        if (mid <= 0) return (false, false, "bad_mid");
        var atrPct = atr / mid;
        if (atrPct < MinAtrPct || atrPct > MaxAtrPct) return (true, false, "atr_band");

        if (!HasVolumeConfirm(list)) return (true, false, "volume");

        TradeSignal? signal = TryPullback(symbol, list, atr);
        if (signal == null && IsBtcEth(symbol))
            signal = TryBreakoutRetest(symbol, list, atr);

        // Remember bar even if no setup — avoid re-scanning same bar every 60s
        _lastBarOpenMs[symbol] = barKey;

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

    private static bool IsBtcEth(string s) =>
        s.StartsWith("BTC", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("ETH", StringComparison.OrdinalIgnoreCase);

    private static bool HasVolumeConfirm(List<BinanceFuturesUsdtKline> k)
    {
        if (k.Count < 25) return false;
        var avg = k.Skip(k.Count - 21).Take(20).Average(x => x.Volume);
        if (avg <= 0) return false;
        return k[^1].Volume >= avg * 0.55m;
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

        decimal zone = Math.Max(atr * 0.45m, close * 0.002m);
        bool touchLong = low <= eF + zone && close >= eF - zone * 0.5m;
        bool touchShort = high >= eF - zone && close <= eF + zone * 0.5m;

        bool bullReject = close > open && close > eF && (close - low) >= (high - low) * 0.50m;
        bool bearReject = close < open && close < eF && (high - close) >= (high - low) * 0.50m;

        if (upTrend && touchLong && bullReject)
        {
            decimal swingLow = k.Skip(k.Count - SwingLookback).Min(x => x.LowPrice);
            decimal sl = Math.Min(swingLow, eS) - atr * StructurePadAtr;
            if (close - sl < atr * MinSlAtr) sl = close - atr * MinSlAtr;
            decimal risk = close - sl;
            if (risk <= 0) return null;
            return Make(symbol, SignalSide.Buy, close, sl,
                new[] { close + risk * MinRr, close + risk * (MinRr + 0.8m), close + risk * (MinRr + 1.6m) },
                atr, "CORE_PULLBACK_LONG", 0.62m);
        }

        if (dnTrend && touchShort && bearReject)
        {
            decimal swingHigh = k.Skip(k.Count - SwingLookback).Max(x => x.HighPrice);
            decimal sl = Math.Max(swingHigh, eS) + atr * StructurePadAtr;
            if (sl - close < atr * MinSlAtr) sl = close + atr * MinSlAtr;
            decimal risk = sl - close;
            if (risk <= 0) return null;
            return Make(symbol, SignalSide.Sell, close, sl,
                new[] { close - risk * MinRr, close - risk * (MinRr + 0.8m), close - risk * (MinRr + 1.6m) },
                atr, "CORE_PULLBACK_SHORT", 0.62m);
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
        bool retestLong = cur.LowPrice <= chHigh + atr * 0.25m
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
                new[] { entry + risk * MinRr, entry + risk * (MinRr + 0.7m), entry + risk * (MinRr + 1.4m) },
                atr, "CORE_BREAKOUT_LONG", 0.58m);
        }

        bool brokeDn = k.Skip(k.Count - 4).Any(x => x.ClosePrice < chLow);
        bool retestShort = cur.HighPrice >= chLow - atr * 0.25m
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
                new[] { entry - risk * MinRr, entry - risk * (MinRr + 0.7m), entry - risk * (MinRr + 1.4m) },
                atr, "CORE_BREAKOUT_SHORT", 0.58m);
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
