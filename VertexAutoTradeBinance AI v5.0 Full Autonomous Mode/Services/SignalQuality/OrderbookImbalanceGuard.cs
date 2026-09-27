using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.Services.SignalQuality;

public sealed record OrderbookGuardResult(
    bool Reject,
    decimal SizeMult,
    decimal SpreadPct,
    decimal DepthRatio,
    string Reason);

/// <summary>
/// Spread + near-touch depth ratio guard (slippage protection before auction APPROVED).
/// </summary>
public sealed class OrderbookImbalanceGuard
{
    private readonly MarketDataService _md;
    private readonly IOptionsMonitor<SignalQualityOptions> _opt;
    private readonly ILogger<OrderbookImbalanceGuard> _log;

    public OrderbookImbalanceGuard(
        MarketDataService md,
        IOptionsMonitor<SignalQualityOptions> opt,
        ILogger<OrderbookImbalanceGuard> log)
    {
        _md = md;
        _opt = opt;
        _log = log;
    }

    public async Task<OrderbookGuardResult> EvaluateAsync(TradeSignal signal, CancellationToken ct)
    {
        var o = _opt.CurrentValue;
        if (!o.OrderbookGuardEnabled)
            return new OrderbookGuardResult(false, 1m, 0m, 1m, "disabled");

        OrderBookSnapshot? book = null;
        try
        {
            book = await _md.GetOrderBookAsync(signal.Symbol, 20).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[OB-GUARD] depth fetch failed {sym} — fail-open", signal.Symbol);
            return new OrderbookGuardResult(false, 1m, 0m, 1m, "no_book");
        }

        if (book is null || book.Bids.Count == 0 || book.Asks.Count == 0)
            return new OrderbookGuardResult(false, 1m, 0m, 1m, "empty_book");

        decimal bid = book.Bids[0].price;
        decimal ask = book.Asks[0].price;
        if (bid <= 0 || ask <= 0 || ask < bid)
            return new OrderbookGuardResult(false, 1m, 0m, 1m, "bad_quotes");

        decimal mid = (bid + ask) / 2m;
        decimal spreadPct = (ask - bid) / mid;
        decimal maxSpread = o.MaxSpreadPct > 0 ? o.MaxSpreadPct : 0.0008m;

        if (spreadPct > maxSpread)
        {
            _log.LogWarning(
                "[OB-GUARD] REJECT {sym} spread={sp:P3} > max={mx:P3}",
                signal.Symbol, spreadPct, maxSpread);
            return new OrderbookGuardResult(true, 0m, spreadPct, 0m, $"WIDE_SPREAD:{spreadPct:P3}");
        }

        // Depth within band of mid
        decimal band = mid * (o.DepthBandPct > 0 ? o.DepthBandPct : 0.005m);
        decimal bidDepth = book.Bids.Where(x => mid - x.price <= band).Sum(x => x.qty * x.price);
        decimal askDepth = book.Asks.Where(x => x.price - mid <= band).Sum(x => x.qty * x.price);

        bool isLong = signal.Side == SignalSide.Buy;
        // For LONG we need asks to absorb buy; thin asks → cut size
        decimal ratio = isLong
            ? (bidDepth > 0 ? askDepth / bidDepth : 0m)
            : (askDepth > 0 ? bidDepth / askDepth : 0m);

        decimal sizeMult = 1m;
        if (ratio < 0.35m) sizeMult = 0.50m;
        else if (ratio < 0.55m) sizeMult = 0.70m;
        else if (ratio < 0.75m) sizeMult = 0.85m;

        if (sizeMult < 1m)
        {
            _log.LogInformation(
                "[OB-GUARD] {sym} depth ratio={r:F2} → size×{m:F2} (spread={sp:P3})",
                signal.Symbol, ratio, sizeMult, spreadPct);
        }

        return new OrderbookGuardResult(false, sizeMult, spreadPct, ratio, "ok");
    }
}
