using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// In-process event bus for live market data pushed in from the Engine process
/// via MarketDataHub. Blazor Server components subscribe directly to these
/// events (no extra browser-side SignalR connection needed — Blazor Server
/// already re-renders the circuit on StateHasChanged).
/// </summary>
public sealed class MarketDataLiveState
{
    /// <summary>Fires on every realtime price tick (symbol, price).</summary>
    public event Action<string, decimal>? PriceTicked;

    /// <summary>Fires when a candle closes (symbol, timeframe, kline).</summary>
    public event Action<string, string, KlineDto>? KlineClosed;

    /// <summary>
    /// Fires immediately when a position is opened or closed by the Engine.
    /// Payload: (symbol, side, eventType) where eventType = "OPENED" | "CLOSED" | "UPDATED".
    /// Web uses this to trigger an immediate Binance positions refresh
    /// instead of waiting for the 30-second polling timer.
    /// </summary>
    public event Action<string, string, string>? PositionChanged;

    /// <summary>
    /// Fires when the Engine finishes loading on-demand kline history.
    /// (symbol, tf, barCount) — Web should refresh chart for this symbol+tf.
    /// </summary>
    public event Action<string, string, int>? KlineHistoryReady;

    // Per-symbol throttle: drop ticks faster than ~8/sec to protect Blazor circuits
    static readonly ConcurrentDictionary<string, long> _lastTickTicks = new(StringComparer.OrdinalIgnoreCase);
    const long MinTickIntervalTicks = TimeSpan.TicksPerMillisecond * 120; // ~8 Hz max per symbol

    public void RaisePriceTicked(string symbol, decimal price)
    {
        if (string.IsNullOrEmpty(symbol)) return;
        var now = DateTime.UtcNow.Ticks;
        if (_lastTickTicks.TryGetValue(symbol, out var prev) && (now - prev) < MinTickIntervalTicks)
            return;
        _lastTickTicks[symbol] = now;
        PriceTicked?.Invoke(symbol, price);
    }

    public void RaiseKlineClosed(string symbol, string timeframe, KlineDto kline)
        => KlineClosed?.Invoke(symbol, timeframe, kline);

    public void RaisePositionChanged(string symbol, string side, string eventType)
        => PositionChanged?.Invoke(symbol, side, eventType);

    public void RaiseKlineHistoryReady(string symbol, string tf, int barCount)
        => KlineHistoryReady?.Invoke(symbol, tf, barCount);
}
