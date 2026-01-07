namespace VertexAutoTradeBinance8.Configuration;

public class TestModeOptions
{
    public bool Enabled { get; set; } = false;
    public string Level { get; set; } = "off";

    public bool AllowSoftEntryAlways { get; set; } = false;
    public bool RelaxRR { get; set; } = false;
    public bool RelaxPatternBlock { get; set; } = false;
    public bool RelaxLiquidity { get; set; } = false;
    public bool IgnoreCorrelation { get; set; } = false;
    public bool LowerRegimeThreshold { get; set; } = false;
}
