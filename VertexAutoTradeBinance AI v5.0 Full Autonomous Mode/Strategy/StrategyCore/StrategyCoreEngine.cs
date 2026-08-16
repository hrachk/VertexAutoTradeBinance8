using System.Collections.Concurrent;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.MarketData;

namespace VertexAutoTradeBinance8.Strategy.StrategyCore;

/// <summary>
/// StrategyCore v1.1 — liquid majors only, wider structure SL, less noise.
///
/// Hard rules:
///   1. Allowlist: BTC/ETH + top liquid alts only (no micro-cap spam)
///   2. Setups: trend pullback (primary), breakout retest (BTC/ETH only)
///   3. SL min 1.8×ATR beyond structure + 0.5×ATR buffer
///   4. R:R ≥ 2.0 on TP1
///   5. Volume confirmation on entry bar
///   6. Cooldown 90m per symbol
/// </summary>
public sealed class StrategyCoreEngine
{
    private readonly ILogger<StrategyCoreEngine> _log;
    private MarketDataFacade? _md;

    public event Action<TradeSignal>? OnSignalGenerated;

    private readonly ConcurrentDictionary<string, DateTime> _cooldown = new(StringComparer.OrdinalIgnoreCase);

    private const decimal MinRr = 2.0m;
    private const decimal MinAtrPct = 0.0030m;  // 0.30%
    private const decimal MaxAtrPct = 0.035m;   // 3.5% — skip crazy alts
    private const decimal MinSlAtr = 1.80m;     // was 1.15 — too tight
    private const decimal StructurePadAtr = 0.50m;
    private const int EmaFast = 21;
    private const int EmaSlow = 50;
    private const int SwingLookback = 24;      // was 12
    private const int Donchian = 20;
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(90);
    private static readonly KlineInterval Tf = KlineInterval.FifteenMinutes;

    /// <summary>Only these symbols may emit CORE signals.</summary>
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
        _log.LogInformation("[CORE] v1.1 bound TF={tf} allowlist={n} minSlAtr={sl}",
            Tf, Allowlist.Count, MinSlAtr);
    }

    public void UnbindReactive()
    {
        if (_md == null) return;
        _md.WsClosedKline -= OnWsClosed;
        _md = null;
    }

    private void OnWsClosed(string symbol, KlineInterval interval, BinanceFuturesUsdtKline kline)
    {
        if (interval != Tf) return;
        if (string.IsNullOrWhiteSpace(symbol)) return;
        if (!Allowlist.Contains(symbol.Trim().ToUpperInvariant())) return;
        _ = EvaluateAsync(symbol.Trim().ToUpperInvariant());
    }

    private async Task EvaluateAsync(string symbol)
    {
        try
        {
            if (_md == null) return;
            if (InCooldown(symbol)) return;

            var klines = await _md.GetKlinesAsync(symbol, Tf, need: 120);
            if (klines == null || klines.Count < 70) return;

            var list = klines.OrderBy(k => k.OpenTime).ToList();
            var atr = Atr(list, 14);
            if (atr <= 0) return;

            var last = list[^1];
            var mid = (last.HighPrice + last.LowPrice) / 2m;
            if (mid <= 0) return;
            var atrPct = atr / mid;
            if (atrPct < MinAtrPct || atrPct > MaxAtrPct) return;

            // Volume: entry bar must not be dead
            if (!HasVolumeConfirm(list)) return;

            // Pullback only for all allowlist; breakout only BTC/ETH (cleaner)
            TradeSignal? signal = TryPullback(symbol, list, atr);
            if (signal == null && IsBtcEth(symbol))
                signal = TryBreakoutRetest(symbol, list, atr);

            if (signal == null) return;
            if (!EnforceMinRr(signal)) return;

            _cooldown[symbol] = DateTime.UtcNow;
            _log.LogInformation(
                "[CORE][{sym}] SIGNAL {side} e={e:F6} sl={sl:F6} tp1={tp:F6} conf={c:F2} {r}",
                symbol, signal.Side, signal.EntryPrice, signal.StopLoss,
                signal.TakeProfits.FirstOrDefault(), signal.Confidence, signal.Reason);

            OnSignalGenerated?.Invoke(signal);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[CORE][{sym}] evaluate failed", symbol);
        }
    }

    private static bool IsBtcEth(string s) =>
        s.StartsWith("BTC", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("ETH", StringComparison.OrdinalIgnoreCase);

    private static bool HasVolumeConfirm(List<BinanceFuturesUsdtKline> k)
    {
        if (k.Count < 25) return false;
        var avg = k.Skip(k.Count - 21).Take(20).Average(x => x.Volume);
        if (avg <= 0) return false;
        // entry bar at least 0.7× average (not a dead print)
        return k[^1].Volume >= avg * 0.70m;
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

        // Stronger trend filter: last 5 closes on correct side of EMA50
        bool upTrend = eF > eS
                       && closes[i - 1] > emaS[i - 1]
                       && closes[i - 3] > emaS[i - 3]
                       && closes[i - 5] > emaS[i - 5];
        bool dnTrend = eF < eS
                       && closes[i - 1] < emaS[i - 1]
                       && closes[i - 3] < emaS[i - 3]
                       && closes[i - 5] < emaS[i - 5];
        if (!upTrend && !dnTrend) return null;

        decimal zone = Math.Max(atr * 0.40m, close * 0.0018m);
        bool touchLong = low <= eF + zone && close >= eF - zone * 0.4m;
        bool touchShort = high >= eF - zone && close <= eF + zone * 0.4m;

        bool bullReject = close > open && close > eF
                          && (close - low) >= (high - low) * 0.60m;
        bool bearReject = close < open && close < eF
                          && (high - close) >= (high - low) * 0.60m;

        if (upTrend && touchLong && bullReject)
        {
            decimal swingLow = k.Skip(k.Count - SwingLookback).Min(x => x.LowPrice);
            decimal sl = Math.Min(swingLow, eS) - atr * StructurePadAtr;
            if (close - sl < atr * MinSlAtr)
                sl = close - atr * MinSlAtr;

            decimal risk = close - sl;
            if (risk <= 0) return null;
            return Make(symbol, SignalSide.Buy, close, sl,
                new[] { close + risk * MinRr, close + risk * (MinRr + 0.8m), close + risk * (MinRr + 1.6m) },
                atr, "CORE_PULLBACK_LONG", 0.64m);
        }

        if (dnTrend && touchShort && bearReject)
        {
            decimal swingHigh = k.Skip(k.Count - SwingLookback).Max(x => x.HighPrice);
            decimal sl = Math.Max(swingHigh, eS) + atr * StructurePadAtr;
            if (sl - close < atr * MinSlAtr)
                sl = close + atr * MinSlAtr;

            decimal risk = sl - close;
            if (risk <= 0) return null;
            return Make(symbol, SignalSide.Sell, close, sl,
                new[] { close - risk * MinRr, close - risk * (MinRr + 0.8m), close - risk * (MinRr + 1.6m) },
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

        bool brokeUp = k.Skip(k.Count - 4).Any(x => x.ClosePrice > chHigh);
        bool retestLong = cur.LowPrice <= chHigh + atr * 0.20m
                          && cur.ClosePrice > chHigh
                          && cur.ClosePrice > cur.OpenPrice
                          && (cur.ClosePrice - cur.LowPrice) >= (cur.HighPrice - cur.LowPrice) * 0.55m;

        if (brokeUp && retestLong)
        {
            decimal entry = cur.ClosePrice;
            // SL below retest low, not just channel — stop-hunt resistant
            decimal sl = Math.Min(cur.LowPrice, chHigh) - atr * StructurePadAtr;
            if (entry - sl < atr * MinSlAtr) sl = entry - atr * MinSlAtr;
            decimal risk = entry - sl;
            if (risk <= 0) return null;
            return Make(symbol, SignalSide.Buy, entry, sl,
                new[] { entry + risk * MinRr, entry + risk * (MinRr + 0.7m), entry + risk * (MinRr + 1.4m) },
                atr, "CORE_BREAKOUT_LONG", 0.60m);
        }

        bool brokeDn = k.Skip(k.Count - 4).Any(x => x.ClosePrice < chLow);
        bool retestShort = cur.HighPrice >= chLow - atr * 0.20m
                           && cur.ClosePrice < chLow
                           && cur.ClosePrice < cur.OpenPrice
                           && (cur.HighPrice - cur.ClosePrice) >= (cur.HighPrice - cur.LowPrice) * 0.55m;

        if (brokeDn && retestShort)
        {
            decimal entry = cur.ClosePrice;
            decimal sl = Math.Max(cur.HighPrice, chLow) + atr * StructurePadAtr;
            if (sl - entry < atr * MinSlAtr) sl = entry + atr * MinSlAtr;
            decimal risk = sl - entry;
            if (risk <= 0) return null;
            return Make(symbol, SignalSide.Sell, entry, sl,
                new[] { entry - risk * MinRr, entry - risk * (MinRr + 0.7m), entry - risk * (MinRr + 1.4m) },
                atr, "CORE_BREAKOUT_SHORT", 0.60m);
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
            Timeframe = Tf.ToString(),
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
