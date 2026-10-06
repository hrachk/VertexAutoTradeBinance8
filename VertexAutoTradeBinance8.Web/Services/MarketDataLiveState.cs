using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// In-process event bus: Engine → Hub → Store + subscribers.
/// Data plane (MarketTickStore) always receives every tick.
/// UI subscribers decide how to paint (JS chart path preferred).
/// </summary>
public sealed class MarketDataLiveState
{
    readonly MarketTickStore _store;

    public MarketDataLiveState(MarketTickStore store) => _store = store;

    public event Action<string, decimal>? PriceTicked;
    public event Action<string, string, KlineDto>? KlineClosed;
    public event Action<string, string, string>? PositionChanged;
    public event Action<string, string, int>? KlineHistoryReady;

    public MarketTickStore Store => _store;

    public void RaisePriceTicked(string symbol, decimal price)
    {
        if (string.IsNullOrEmpty(symbol) || price <= 0) return;
        // Full fidelity: every tick lands in the store
        _store.SetLast(symbol, price);
        PriceTicked?.Invoke(symbol, price);
    }

    public void RaiseKlineClosed(string symbol, string timeframe, KlineDto kline)
        => KlineClosed?.Invoke(symbol, timeframe, kline);

    public void RaisePositionChanged(string symbol, string side, string eventType)
        => PositionChanged?.Invoke(symbol, side, eventType);

    public void RaiseKlineHistoryReady(string symbol, string tf, int barCount)
        => KlineHistoryReady?.Invoke(symbol, tf, barCount);
}
