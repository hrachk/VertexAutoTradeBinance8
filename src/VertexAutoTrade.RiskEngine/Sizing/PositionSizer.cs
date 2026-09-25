using VertexAutoTrade.RiskEngine.Abstractions;

namespace VertexAutoTrade.RiskEngine.Sizing;

/// <summary>
/// Classic fixed-fractional size from stop distance.
/// PositionSize = (Equity * RiskFraction) / |Entry − SL|
/// Never expands stop; rejects invalid / zero distance.
/// </summary>
public sealed class PositionSizer : IPositionSizer
{
    private readonly decimal _hardCapRiskUsd;

    public PositionSizer(decimal hardCapRiskUsd = 0m)
    {
        _hardCapRiskUsd = Math.Max(0m, hardCapRiskUsd);
    }

    public PositionSizeResult Calculate(PositionSizeRequest request)
    {
        if (request.EquityUsd <= 0)
            return Fail("Equity must be > 0");

        if (request.EntryPrice <= 0 || request.StopLossPrice <= 0)
            return Fail("Entry and stop must be > 0");

        if (request.RiskFraction <= 0)
            return Fail("RiskFraction must be > 0");

        var stopDistance = Math.Abs(request.EntryPrice - request.StopLossPrice);
        if (stopDistance <= 0)
            return new PositionSizeResult(false, 0, 0, 0, "Invalid stop distance (entry == SL)");

        // Relative distance guard: stop must be meaningful vs price (avoid dust math)
        var rel = stopDistance / request.EntryPrice;
        if (rel < 0.00005m) // < 0.005%
            return new PositionSizeResult(false, 0, 0, stopDistance, "Stop distance too tight vs price");

        var riskUsd = request.EquityUsd * request.RiskFraction;
        if (_hardCapRiskUsd > 0 && riskUsd > _hardCapRiskUsd)
            riskUsd = _hardCapRiskUsd;

        var qty = riskUsd / stopDistance;

        if (request.StepSize is { } step && step > 0)
            qty = Math.Floor(qty / step) * step;

        if (request.MinQty is { } minQ && minQ > 0 && qty < minQ)
            return new PositionSizeResult(false, 0, riskUsd, stopDistance,
                $"Qty {qty} < minQty {minQ}");

        if (request.MinNotional is { } minN && minN > 0)
        {
            var notional = qty * request.EntryPrice;
            if (notional < minN)
                return new PositionSizeResult(false, 0, riskUsd, stopDistance,
                    $"Notional {notional:F2} < minNotional {minN}");
        }

        if (qty <= 0)
            return new PositionSizeResult(false, 0, riskUsd, stopDistance, "Qty rounded to zero");

        return new PositionSizeResult(true, qty, riskUsd, stopDistance);
    }

    private static PositionSizeResult Fail(string reason) =>
        new(false, 0, 0, 0, reason);
}
