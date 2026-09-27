using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services.SignalQuality;

public sealed class SignalAuction
{
    private readonly SignalQualityEvaluator _eval;
    private readonly IOptionsMonitor<SignalQualityOptions> _opt;
    private readonly ILogger<SignalAuction> _log;

    public SignalAuction(
        SignalQualityEvaluator eval,
        IOptionsMonitor<SignalQualityOptions> opt,
        ILogger<SignalAuction> log)
    {
        _eval = eval;
        _opt = opt;
        _log = log;
    }

    public sealed record Ranked(TradeSignal Signal, SignalQualityBreakdown Quality);

    /// <summary>
    /// Rank batch by CompositeScore. Apply MinQualityThreshold.
    /// Return at most <paramref name="availableSlots"/> winners (BestScoreAuction)
    /// or FIFO survivors above threshold (FirstComeFirstServed).
    /// </summary>
    public IReadOnlyList<Ranked> Select(
        IReadOnlyList<TradeSignal> batch,
        int availableSlots)
    {
        var o = _opt.CurrentValue;
        decimal minQ = o.MinQualityThreshold;
        string strategy = o.SignalSelectionStrategy ?? "BestScoreAuction";
        if (availableSlots < 0) availableSlots = 0;

        var ranked = new List<Ranked>(batch.Count);
        foreach (var s in batch)
        {
            if (s == null) continue;
            var q = _eval.Evaluate(s);
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
            if (q.Composite < minQ)
            {
                _log.LogInformation(
                    "  {rank}. {sym} ({side}) | CompositeScore: {sc:F1}% [{br}] -> REJECTED (Below MinQualityThreshold {min}%)",
                    i, s.Symbol, side, q.Composite, q.Summary, minQ);
                continue;
            }

            if (winners.Count >= availableSlots && availableSlots > 0)
            {
                _log.LogInformation(
                    "  {rank}. {sym} ({side}) | CompositeScore: {sc:F1}% [{br}] -> REJECTED (Outperformed in auction)",
                    i, s.Symbol, side, q.Composite, q.Summary);
                continue;
            }

            // Dynamic sizing from score
            decimal factor = _eval.SizeFactorFromScore(q.Composite);
            s.SizeMultiplier = Math.Clamp(s.SizeMultiplier * factor, 0.25m, 1.0m);

            winners.Add(r);
            _log.LogInformation(
                "  {rank}. {sym} ({side}) | CompositeScore: {sc:F1}% [{br}] -> APPROVED (Slot {slot}, size×{sf:F2})",
                i, s.Symbol, side, q.Composite, q.Summary, winners.Count, factor);
        }

        return winners;
    }
}
