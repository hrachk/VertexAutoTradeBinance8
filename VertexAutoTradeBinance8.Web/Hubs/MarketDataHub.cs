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
}
