namespace VertexAutoTrade.Core.Pipeline;

public enum GateLayer
{
    HardRisk = 1,
    AntiTilt = 2,
    Regime = 3,
    BtcCorrelation = 4,
    NewsMacro = 5,
    MlClassifier = 6,
    Sizing = 7
}

public sealed record GateDecision(
    bool Allowed,
    GateLayer Layer,
    string Code,
    string Message,
    decimal SizeMult = 1m,
    decimal? EffectiveRiskFraction = null,
    DateTime? BlockedUntilUtc = null)
{
    public static GateDecision Ok(GateLayer layer, string msg = "OK", decimal sizeMult = 1m, decimal? riskFrac = null) =>
        new(true, layer, "OK", msg, sizeMult, riskFrac);

    public static GateDecision Reject(GateLayer layer, string code, string msg, DateTime? until = null) =>
        new(false, layer, code, msg, 0m, 0m, until);
}
