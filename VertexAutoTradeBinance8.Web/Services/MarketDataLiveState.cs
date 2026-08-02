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

    public void RaisePriceTicked(string symbol, decimal price)
        => PriceTicked?.Invoke(symbol, price);

    public void RaiseKlineClosed(string symbol, string timeframe, KlineDto kline)
        => KlineClosed?.Invoke(symbol, timeframe, kline);

    public void RaisePositionChanged(string symbol, string side, string eventType)
        => PositionChanged?.Invoke(symbol, side, eventType);

    /// <summary>
    /// Fires when the Engine finishes loading on-demand kline history.
    /// (symbol, tf, barCount) — Web should refresh chart for this symbol+tf.
    /// </summary>
    public event Action<string, string, int>? KlineHistoryReady;

    public void RaiseKlineHistoryReady(string symbol, string tf, int barCount)
        => KlineHistoryReady?.Invoke(symbol, tf, barCount);
}
