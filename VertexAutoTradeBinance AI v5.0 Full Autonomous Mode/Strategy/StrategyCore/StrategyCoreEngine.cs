using System.Collections.Concurrent;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.MarketData;

namespace VertexAutoTradeBinance8.Strategy.StrategyCore;

/// <summary>
/// StrategyCore v1 — professional minimal engine.
///
/// Replaces the legacy multi-setup StrategyEngine as the DEFAULT signal source.
/// Design rules (hard):
///   1. Only TWO setups: trend pullback, breakout retest
///   2. Minimum reward:risk = 2.0 (TP1 distance >= 2 × SL distance)
///   3. SL beyond structure (swing / broken level), never a tight noise stop
///   4. ATR band filter — skip dead and chaotic symbols
///   5. Cooldown per symbol — no spam
///
/// Reason prefixes: CORE_PULLBACK_* / CORE_BREAKOUT_*
/// Pipeline treats CORE_ as authoritative (see TradingWorker AI bypass).
/// </summary>
public sealed class StrategyCoreEngine
{
    private readonly ILogger<StrategyCoreEngine> _log;
    private MarketDataFacade? _md;

    public event Action<TradeSignal>? OnSignalGenerated;

    private readonly ConcurrentDictionary<string, DateTime> _cooldown = new(StringComparer.OrdinalIgnoreCase);

    // Tunables (v1 constants — keep simple, no config maze)
    private const decimal MinRr = 2.0m;
    private const decimal MinAtrPct = 0.0025m;   // 0.25%
    private const decimal MaxAtrPct = 0.045m;    // 4.5%
    private const int EmaFast = 21;
    private const int EmaSlow = 50;
    private const int SwingLookback = 12;
    private const int Donchian = 20;
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(45);
    private static readonly KlineInterval Tf = KlineInterval.FifteenMinutes;

    public StrategyCoreEngine(ILogger<StrategyCoreEngine> log)
    {
        _log = log;
    }

    public void BindReactive(MarketDataFacade marketData)
    {
        UnbindReactive();
        _md = marketData;
        _md.WsClosedKline += OnWsClosed;
        _log.LogInformation("[CORE] Bound reactive (TF={tf})", Tf);
    }

    public void UnbindReactive()
    {
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
        _ = EvaluateAsync(symbol);
    }

    private async Task EvaluateAsync(string symbol)
    {
        try
        {
            if (_md == null) return;
            if (InCooldown(symbol)) return;

            var klines = await _md.GetKlinesAsync(symbol, Tf, need: 120);
            if (klines == null || klines.Count < 60) return;

            var list = klines.OrderBy(k => k.OpenTime).ToList();
            var atr = Atr(list, 14);
            if (atr <= 0) return;

            var last = list[^1];
            var mid = (last.HighPrice + last.LowPrice) / 2m;
            if (mid <= 0) return;
            var atrPct = atr / mid;
            if (atrPct < MinAtrPct || atrPct > MaxAtrPct) return;

            // Setup priority: pullback first (higher quality in trends), then breakout
            var signal = TryPullback(symbol, list, atr) ?? TryBreakoutRetest(symbol, list, atr);
            if (signal == null) return;

            if (!EnforceMinRr(signal))
            {
                _log.LogDebug("[CORE][{sym}] rejected — R:R < {rr}", symbol, MinRr);
                return;
            }

            _cooldown[symbol] = DateTime.UtcNow;
            _log.LogInformation(
                "[CORE][{sym}] SIGNAL {side} entry={e:F6} sl={sl:F6} tp1={tp:F6} conf={c:F2} reason={r}",
                symbol, signal.Side, signal.EntryPrice, signal.StopLoss,
                signal.TakeProfits.FirstOrDefault(), signal.Confidence, signal.Reason);

            OnSignalGenerated?.Invoke(signal);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[CORE][{sym}] evaluate failed", symbol);
        }
    }

    private bool InCooldown(string symbol)
        => _cooldown.TryGetValue(symbol, out var t) && DateTime.UtcNow - t < Cooldown;

    // ── Setup 1: Trend pullback to EMA21 in EMA21/50 trend ─────────────
    private TradeSignal? TryPullback(string symbol, List<BinanceFuturesUsdtKline> k, decimal atr)
    {
        var closes = k.Select(x => x.ClosePrice).ToList();
        var emaF = EmaSeries(closes, EmaFast);
        var emaS = EmaSeries(closes, EmaSlow);
        int i = closes.Count - 1;
        if (i < EmaSlow + 5) return null;

        decimal eF = emaF[i], eS = emaS[i], prevF = emaF[i - 1];
        var bar = k[i];
        decimal close = bar.ClosePrice, open = bar.OpenPrice;
        decimal high = bar.HighPrice, low = bar.LowPrice;

        bool upTrend = eF > eS && closes[i - 3] > emaS[i - 3];
        bool dnTrend = eF < eS && closes[i - 3] < emaS[i - 3];
        if (!upTrend && !dnTrend) return null;

        // Touch EMA21 zone (body or wick within 0.35 * ATR of EMA)
        decimal zone = Math.Max(atr * 0.35m, close * 0.0015m);
        bool touchLong = low <= eF + zone && close >= eF - zone * 0.5m;
        bool touchShort = high >= eF - zone && close <= eF + zone * 0.5m;

        // Rejection candle
        bool bullReject = close > open && close > eF && (close - low) >= (high - low) * 0.55m;
        bool bearReject = close < open && close < eF && (high - close) >= (high - low) * 0.55m;

        if (upTrend && touchLong && bullReject)
        {
            decimal swingLow = k.Skip(k.Count - SwingLookback).Min(x => x.LowPrice);
            decimal sl = Math.Min(swingLow, eS) - atr * 0.25m;
            // Structure floor: at least 1.15 ATR from entry
            if (close - sl < atr * 1.15m)
                sl = close - atr * 1.15m;

            decimal risk = close - sl;
            if (risk <= 0) return null;
            decimal tp1 = close + risk * MinRr;
            decimal tp2 = close + risk * (MinRr + 0.8m);
            decimal tp3 = close + risk * (MinRr + 1.6m);

            return Make(symbol, SignalSide.Buy, close, sl, new[] { tp1, tp2, tp3 }, atr,
                "CORE_PULLBACK_LONG", confidence: 0.62m);
        }

        if (dnTrend && touchShort && bearReject)
        {
            decimal swingHigh = k.Skip(k.Count - SwingLookback).Max(x => x.HighPrice);
            decimal sl = Math.Max(swingHigh, eS) + atr * 0.25m;
            if (sl - close < atr * 1.15m)
                sl = close + atr * 1.15m;

            decimal risk = sl - close;
            if (risk <= 0) return null;
            decimal tp1 = close - risk * MinRr;
            decimal tp2 = close - risk * (MinRr + 0.8m);
            decimal tp3 = close - risk * (MinRr + 1.6m);

            return Make(symbol, SignalSide.Sell, close, sl, new[] { tp1, tp2, tp3 }, atr,
                "CORE_PULLBACK_SHORT", confidence: 0.62m);
        }

        return null;
    }

    // ── Setup 2: Donchian breakout + retest hold ───────────────────────
    private TradeSignal? TryBreakoutRetest(string symbol, List<BinanceFuturesUsdtKline> k, decimal atr)
    {
        if (k.Count < Donchian + 6) return null;
        int i = k.Count - 1;
        // Channel from bars [i-Donchian-1 .. i-2] (exclude last two)
        var window = k.Skip(k.Count - Donchian - 3).Take(Donchian).ToList();
        if (window.Count < Donchian) return null;
        decimal chHigh = window.Max(x => x.HighPrice);
        decimal chLow = window.Min(x => x.LowPrice);

        var prev = k[i - 1];
        var cur = k[i];

        // Long: broke above within last 3 bars, now retesting (low near chHigh, close back above)
        bool brokeUp = k.Skip(k.Count - 4).Any(x => x.ClosePrice > chHigh);
        bool retestLong = cur.LowPrice <= chHigh + atr * 0.15m
                          && cur.ClosePrice > chHigh
                          && cur.ClosePrice > cur.OpenPrice;

        if (brokeUp && retestLong)
        {
            decimal entry = cur.ClosePrice;
            decimal sl = chHigh - atr * 0.55m;
            if (entry - sl < atr * 1.1m) sl = entry - atr * 1.1m;
            decimal risk = entry - sl;
            if (risk <= 0) return null;
            decimal tp1 = entry + risk * MinRr;
            decimal tp2 = entry + risk * (MinRr + 0.7m);
            decimal tp3 = entry + risk * (MinRr + 1.4m);
            return Make(symbol, SignalSide.Buy, entry, sl, new[] { tp1, tp2, tp3 }, atr,
                "CORE_BREAKOUT_LONG", confidence: 0.58m);
        }

        bool brokeDn = k.Skip(k.Count - 4).Any(x => x.ClosePrice < chLow);
        bool retestShort = cur.HighPrice >= chLow - atr * 0.15m
                           && cur.ClosePrice < chLow
                           && cur.ClosePrice < cur.OpenPrice;

        if (brokeDn && retestShort)
        {
            decimal entry = cur.ClosePrice;
            decimal sl = chLow + atr * 0.55m;
            if (sl - entry < atr * 1.1m) sl = entry + atr * 1.1m;
            decimal risk = sl - entry;
            if (risk <= 0) return null;
            decimal tp1 = entry - risk * MinRr;
            decimal tp2 = entry - risk * (MinRr + 0.7m);
            decimal tp3 = entry - risk * (MinRr + 1.4m);
            return Make(symbol, SignalSide.Sell, entry, sl, new[] { tp1, tp2, tp3 }, atr,
                "CORE_BREAKOUT_SHORT", confidence: 0.58m);
        }

        return null;
    }

    private static TradeSignal Make(
        string symbol, SignalSide side, decimal entry, decimal sl,
        IEnumerable<decimal> tps, decimal atr, string reason, decimal confidence)
    {
        return new TradeSignal
        {
            Symbol = symbol.ToUpperInvariant(),
            Side = side,
            EntryPrice = entry,
            StopLoss = sl,
            TakeProfits = tps.ToList(),
            TakeProfit = tps.First(),
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
        decimal reward = Math.Abs(s.TakeProfits[0] - s.EntryPrice);
        return reward / risk >= MinRr * 0.98m; // tiny float tolerance
    }

    private static decimal Atr(List<BinanceFuturesUsdtKline> k, int period)
    {
        if (k.Count < period + 2) return 0;
        var trs = new List<decimal>();
        for (int i = 1; i < k.Count; i++)
        {
            decimal h = k[i].HighPrice, l = k[i].LowPrice, pc = k[i - 1].ClosePrice;
            decimal tr = Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
            trs.Add(tr);
        }
        if (trs.Count < period) return 0;
        return trs.TakeLast(period).Average();
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
