namespace VertexAutoTrade.RiskEngine.Options;

/// <summary>
/// Hard-mode toggles for ML skip and news gate.
/// Bound from appsettings / appsettings.runtime.json section "Institutional".
/// Web Settings → Risk tab writes the same keys into runtime override.
/// </summary>
public sealed class InstitutionalGateOptions
{
    public const string SectionName = "Institutional";

    /// <summary>When true, low P(win) rejects the entry (INST_MlClassifier).</summary>
    public bool EnableMlHardReject { get; set; } = false;

    /// <summary>When true, critical bearish news can hard-block new entries.</summary>
    public bool EnableNewsHardMode { get; set; } = false;

    public decimal MlSkipThreshold { get; set; } = 0.42m;
    public decimal MlProbeThreshold { get; set; } = 0.55m;
}
