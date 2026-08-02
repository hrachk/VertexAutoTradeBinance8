using Microsoft.AspNetCore.SignalR;
using VertexAutoTradeBinance8.Web.Models;
using VertexAutoTradeBinance8.Web.Services;

namespace VertexAutoTradeBinance8.Web.Hubs;

/// <summary>
/// Receiving endpoint for the Engine process (connects in as a SignalR client).
/// Not used by browsers — Blazor Server components get updates via
/// MarketDataLiveState directly, in-process.
/// </summary>
public class MarketDataHub : Hub
{
    private readonly MarketDataLiveState _state;

    public MarketDataHub(MarketDataLiveState state)
    {
        _state = state;
    }

    public Task PushPrice(string symbol, decimal price)
    {
        _state.RaisePriceTicked(symbol, price);
        return Task.CompletedTask;
    }

    public Task PushKlineClosed(
        string symbol,
        string timeframe,
        long openTime,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        decimal volume)
    {
        _state.RaiseKlineClosed(
            symbol,
            timeframe,
            new KlineDto(openTime, open, high, low, close, volume));

        return Task.CompletedTask;
    }
    /// <summary>
    /// Called by the Engine immediately when a position is opened or closed.
    /// Triggers an instant positions refresh in the Web UI, bypassing the
    /// 30-second polling timer so the user sees position changes in real time.
    /// eventType: "OPENED" | "CLOSED" | "UPDATED"
    /// </summary>
    public Task PushPositionEvent(string symbol, string side, string eventType)
    {
        _state.RaisePositionChanged(symbol, side, eventType);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called by the Engine when on-demand kline history is ready.
    /// Fires KlineHistoryReady event → Web MarketSnapshot refreshes chart.
    /// symbol: e.g. "BTCUSDT", tf: e.g. "15m", barCount: how many bars loaded.
    /// </summary>
    public Task PushKlineHistoryReady(string symbol, string tf, int barCount)
    {
        _state.RaiseKlineHistoryReady(symbol, tf, barCount);
        return Task.CompletedTask;
    }
}
