using VertexAutoTrade.Core.Pipeline;

namespace VertexAutoTrade.Regime;

/// <summary>
/// Pauses alt entries when BTC (or ETH) 15m absolute move exceeds threshold.
/// Caller feeds last price and price 15m ago.
/// </summary>
public sealed class BtcCorrelationGuard
{
    private readonly RegimeOptions _opt;
    private DateTime? _pauseUntilUtc;
    private string? _reason;

    public BtcCorrelationGuard(RegimeOptions? opt = null) => _opt = opt ?? new RegimeOptions();

    public void ObserveBtcMove(decimal priceNow, decimal price15mAgo, DateTime utcNow)
    {
        if (price15mAgo <= 0) return;
        var pct = Math.Abs((priceNow - price15mAgo) / price15mAgo) * 100m;
        if (pct >= _opt.BtcSpikePct15m)
        {
            _pauseUntilUtc = utcNow.ToUniversalTime().AddMinutes(_opt.BtcSpikePauseMinutes);
            _reason = $"BTC_SPIKE {pct:F2}% /15m ≥ {_opt.BtcSpikePct15m}%";
        }
    }

    public GateDecision EvaluateAltEntry(string symbol, DateTime utcNow)
    {
        var u = symbol.ToUpperInvariant();
        if (u.StartsWith("BTC") || u.StartsWith("ETH"))
            return GateDecision.Ok(GateLayer.BtcCorrelation, "major — spike guard N/A");

        utcNow = utcNow.ToUniversalTime();
        if (_pauseUntilUtc is { } until && utcNow < until)
            return GateDecision.Reject(GateLayer.BtcCorrelation, "BTC_SPIKE_PAUSE", _reason ?? "BTC spike pause", until);

        if (_pauseUntilUtc is not null && utcNow >= _pauseUntilUtc)
        {
            _pauseUntilUtc = null;
            _reason = null;
        }
        return GateDecision.Ok(GateLayer.BtcCorrelation);
    }
}
