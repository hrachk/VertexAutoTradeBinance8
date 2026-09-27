using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services.SignalQuality;

public sealed class SignalAuction
{
    private readonly SignalQualityEvaluator _eval;
    private readonly OrderbookImbalanceGuard _ob;
    private readonly AuctionTelemetry _tel;
    private readonly IOptionsMonitor<SignalQualityOptions> _opt;
    private readonly ILogger<SignalAuction> _log;

    public SignalAuction(
        SignalQualityEvaluator eval,
        OrderbookImbalanceGuard ob,
        AuctionTelemetry tel,
        IOptionsMonitor<SignalQualityOptions> opt,
        ILogger<SignalAuction> log)
    {
        _eval = eval;
        _ob = ob;
        _tel = tel;
        _opt = opt;
        _log = log;
    }

    public sealed record Ranked(TradeSignal Signal, SignalQualityBreakdown Quality);

    public async Task<IReadOnlyList<Ranked>> SelectAsync(
        IReadOnlyList<TradeSignal> batch,
        int availableSlots,
        CancellationToken ct = default)
    {
        var o = _opt.CurrentValue;
        decimal minQ = o.MinQualityThreshold;
        string strategy = o.SignalSelectionStrategy ?? "BestScoreAuction";
        if (availableSlots < 0) availableSlots = 0;

        var ranked = new List<Ranked>(batch.Count);
        foreach (var s in batch)
        {
            if (s == null) continue;
            var q = await _eval.EvaluateAsync(s, ct).ConfigureAwait(false);
            ranked.Add(new Ranked(s, q));
        }

        if (ranked.Count == 0) return ranked;

        _log.LogInformation(
            "[SignalAuction] Received {n} candidate signals for {slots} available slots.",
            ranked.Count, availableSlots);

        bool auction = strategy.Equals("BestScoreAuction", StringComparison.OrdinalIgnoreCase);
        if (auction)
            ranked = ranked.OrderByDescending(x => x.Quality.Composite).ToList();

        var winners = new List<Ranked>();
        int i = 0;
        foreach (var r in ranked)
        {
            i++;
            var s = r.Signal;
            var q = r.Quality;
            string side = s.Side.ToString();

            void Tel(string result) => _tel.Log(new AuctionTelemetryRecord
            {
                TimestampUtc = DateTime.UtcNow,
                Symbol = s.Symbol,
                Direction = side,
                CompositeScore = q.Composite,
                Trend = q.Trend,
                Derivatives = q.Derivatives,
                Volume = q.Volume,
                RiskReward = q.RiskReward,
                AuctionResult = result
            });

            if (q.Composite < minQ)
            {
                _log.LogInformation(
                    "  {rank}. {sym} ({side}) | CompositeScore: {sc:F1}% [{br}] -> REJECTED (Below MinQualityThreshold {min}%)",
                    i, s.Symbol, side, q.Composite, q.Summary, minQ);
                Tel("BELOW_THRESHOLD");
                continue;
            }

            if (winners.Count >= availableSlots && availableSlots > 0)
            {
                _log.LogInformation(
                    "  {rank}. {sym} ({side}) | CompositeScore: {sc:F1}% [{br}] -> REJECTED (Outperformed in auction)",
                    i, s.Symbol, side, q.Composite, q.Summary);
                Tel("OUTPERFORMED");
                continue;
            }

            // Orderbook spread / depth guard
            var ob = await _ob.EvaluateAsync(s, ct).ConfigureAwait(false);
            if (ob.Reject)
            {
                _log.LogInformation(
                    "  {rank}. {sym} ({side}) | CompositeScore: {sc:F1}% -> REJECTED ({reason})",
                    i, s.Symbol, side, q.Composite, ob.Reason);
                Tel($"REJECTED_{ob.Reason}");
                continue;
            }

            decimal factor = _eval.SizeFactorFromScore(q.Composite);
            if (ob.SizeMult < 1m) factor *= ob.SizeMult;
            s.SizeMultiplier = Math.Clamp(s.SizeMultiplier * factor, 0.25m, 1.0m);

            winners.Add(r);
            _log.LogInformation(
                "  {rank}. {sym} ({side}) | CompositeScore: {sc:F1}% [{br}] -> APPROVED (Slot {slot}, size×{sf:F2}, spread={sp:P3})",
                i, s.Symbol, side, q.Composite, q.Summary, winners.Count, s.SizeMultiplier, ob.SpreadPct);
            Tel("APPROVED");
        }

        return winners;
    }

    /// <summary>Sync wrapper for older call sites.</summary>
    public IReadOnlyList<Ranked> Select(IReadOnlyList<TradeSignal> batch, int availableSlots)
        => SelectAsync(batch, availableSlots).GetAwaiter().GetResult();
}
