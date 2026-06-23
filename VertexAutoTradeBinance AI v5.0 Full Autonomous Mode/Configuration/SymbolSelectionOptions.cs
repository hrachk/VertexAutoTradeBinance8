namespace VertexAutoTradeBinance8.Configuration
{
    /// <summary>
    /// Strongly-typed mirror of the "SymbolSelection" appsettings.json
    /// section. Previously this section was only read ad-hoc via
    /// IConfiguration.GetValue("SymbolSelection:Auto:...") inside
    /// SymbolLiquidityScanner — this class gives the Settings page a
    /// typed surface to read/write the same values through the same
    /// live-reloading IOptionsMonitor mechanism used elsewhere in v9.
    /// </summary>
    public sealed class SymbolSelectionOptions
    {
        public string Mode { get; set; } = "Auto";
        public List<string> Pinned { get; set; } = new();
        public AutoSelectionOptions Auto { get; set; } = new();
        public DynamicCapOptions DynamicCap { get; set; } = new();

        public sealed class AutoSelectionOptions
        {
            public int RefreshInterval { get; set; } = 1;
            public int ScannerCacheSeconds { get; set; } = 45;
            public int TopVolumeCount { get; set; } = 12;
            public int FinalUniverseCap { get; set; } = 10;
            public int TotalUniverseCap { get; set; } = 10;
            public int StabilityMaxAdds { get; set; } = 1;
            public decimal MinPrice { get; set; } = 0.045m;
            public decimal Min24hVolumeLong { get; set; } = 1_800_000m;
            public decimal Min24hVolumeShort { get; set; } = 1_800_000m;
            public decimal AiWeight { get; set; } = 0.53m;
            public decimal MomentumWeight { get; set; } = 0.35m;
            public decimal MomentumCapPercent { get; set; } = 18m;
            public bool EnableBtcFilter { get; set; } = true;
            public decimal BtcDumpThreshold { get; set; } = -5.0m;
            public decimal BtcSqueezeThreshold { get; set; } = 6.0m;
            public int DryRunLogLimit { get; set; } = 20;
        }

        public sealed class DynamicCapOptions
        {
            public bool Enabled { get; set; } = true;
            public decimal LowVolPct { get; set; } = 1.2m;
            public decimal MidVolPct { get; set; } = 2.5m;
            public int CapLowVol { get; set; } = 18;
            public int CapMidVol { get; set; } = 20;
            public int CapHighVol { get; set; } = 22;
        }
    }
}
