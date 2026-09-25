using VertexAutoTrade.Core.Pipeline;

namespace VertexAutoTrade.NewsMacro;

public enum NewsImpactLevel { Low = 1, Medium = 2, High = 3, Critical = 4 }

public sealed class NewsSentimentSnapshot
{
    public string Headline { get; init; } = "";
    public NewsImpactLevel Impact { get; init; }
    public decimal Sentiment { get; init; } // -1..+1
    public DateTime WindowEndUtc { get; init; }
    public bool ShadowMode { get; init; } = true;
}

/// <summary>
/// Maps active news directive to gate: Critical → reject (if not shadow) or size×0.5 in shadow log path.
/// </summary>
public sealed class NewsSentimentGate
{
    public GateDecision Evaluate(NewsSentimentSnapshot? active, bool hardMode)
    {
        if (active is null)
            return GateDecision.Ok(GateLayer.NewsMacro, "no active news");

        if (active.Impact >= NewsImpactLevel.Critical && active.Sentiment <= -0.5m)
        {
            if (hardMode && !active.ShadowMode)
                return GateDecision.Reject(GateLayer.NewsMacro, "NEWS_CRITICAL_BEAR",
                    active.Headline, active.WindowEndUtc);
            return GateDecision.Ok(GateLayer.NewsMacro, "NEWS_SHADOW size×0.5", sizeMult: 0.5m);
        }

        if (active.Impact >= NewsImpactLevel.High)
            return GateDecision.Ok(GateLayer.NewsMacro, "NEWS_HIGH size×0.7", sizeMult: 0.7m);

        return GateDecision.Ok(GateLayer.NewsMacro, sizeMult: 1m);
    }
}
