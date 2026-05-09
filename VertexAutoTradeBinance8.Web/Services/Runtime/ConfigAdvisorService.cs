using VertexAutoTradeBinance8.Web.Models.Config;

namespace VertexAutoTradeBinance8.Web.Services;

public sealed class AdvisorResult
{
    public HedgeKillConfig Config { get; init; } = new();
    public string Reason { get; init; } = "";
}

public sealed class ConfigAdvisorService
{
    // сейчас rule-based (детерминированно).
    // потом подключим AiSelfLearningService/engine_state.json/decision_trace для полноценного ML.
    public Task<AdvisorResult> RecommendHedgeKillAsync(HedgeKillConfig cur)
    {
        var rec = Clone(cur);

        // Normalize invariants
        if (rec.GivebackMinUsd <= 0) rec.GivebackMinUsd = 3.5m;
        if (rec.GivebackMaxUsd < rec.GivebackMinUsd) rec.GivebackMaxUsd = rec.GivebackMinUsd + 10m;

        // PRO default: cooldown OFF in production unless you observe hedge-churn
        // но для Safe-mode включаем мягко.
        if (string.Equals(rec.Mode, "Safe", StringComparison.OrdinalIgnoreCase))
        {
            rec.UseCooldown = true;
            rec.CooldownMinutes = Math.Clamp(rec.CooldownMinutes, 10, 20);

            rec.GivebackBucketLow = 0.14m;
            rec.GivebackBucketMid = 0.22m;
            rec.GivebackBucketHigh = 0.30m;

            rec.LoserCloseFraction = 0.55m;
            return Task.FromResult(new AdvisorResult
            {
                Config = rec,
                Reason = "Safe preset: cooldown ON, tighter giveback, softer kill"
            });
        }

        // Hybrid PRO baseline (best default for prod)
        if (string.Equals(rec.Mode, "Hybrid", StringComparison.OrdinalIgnoreCase))
        {
            rec.UseCooldown = false;
            rec.CooldownMinutes = Math.Clamp(rec.CooldownMinutes, 8, 15);

            rec.GivebackBucketLow = 0.18m;
            rec.GivebackBucketMid = 0.28m;
            rec.GivebackBucketHigh = 0.40m;

            rec.LoserCloseFraction = 0.60m;

            // keep hard gates sane
            rec.NetOkUsd = Math.Clamp(rec.NetOkUsd, 2m, 5m);
            rec.HardNetUsd = Math.Clamp(rec.HardNetUsd, -20m, -5m);
            rec.HardLoserUsd = Math.Clamp(rec.HardLoserUsd, 12m, 30m);
            rec.HardLoserAtrMult = Math.Clamp(rec.HardLoserAtrMult, 1.2m, 2.2m);

            return Task.FromResult(new AdvisorResult
            {
                Config = rec,
                Reason = "Hybrid PRO preset: cooldown OFF, bucket-weighted giveback with bounds, 60% loser trim"
            });
        }

        // Aggressive: меньше ограничений giveback, быстрее режем loser
        rec.UseCooldown = false;
        rec.GivebackBucketLow = 0.22m;
        rec.GivebackBucketMid = 0.34m;
        rec.GivebackBucketHigh = 0.50m;
        rec.LoserCloseFraction = 0.70m;

        return Task.FromResult(new AdvisorResult
        {
            Config = rec,
            Reason = "Aggressive preset: larger giveback allowance, faster loser reduction"
        });
    }

    private static HedgeKillConfig Clone(HedgeKillConfig x) => new()
    {
        Mode = x.Mode,
        NetOkUsd = x.NetOkUsd,
        HardNetUsd = x.HardNetUsd,
        HardLoserUsd = x.HardLoserUsd,
        HardLoserAtrMult = x.HardLoserAtrMult,
        GivebackMinUsd = x.GivebackMinUsd,
        GivebackMaxUsd = x.GivebackMaxUsd,
        GivebackBucketLow = x.GivebackBucketLow,
        GivebackBucketMid = x.GivebackBucketMid,
        GivebackBucketHigh = x.GivebackBucketHigh,
        SlopeWeak = x.SlopeWeak,
        SlopeStrong = x.SlopeStrong,
        AtrPctExtreme = x.AtrPctExtreme,
        UseCooldown = x.UseCooldown,
        CooldownMinutes = x.CooldownMinutes,
        LoserCloseFraction = x.LoserCloseFraction
    };
}
