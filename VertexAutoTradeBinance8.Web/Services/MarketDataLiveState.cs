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

    public void RaisePriceTicked(string symbol, decimal price)
        => PriceTicked?.Invoke(symbol, price);

    public void RaiseKlineClosed(string symbol, string timeframe, KlineDto kline)
        => KlineClosed?.Invoke(symbol, timeframe, kline);
}
