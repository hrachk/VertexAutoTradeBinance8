namespace VertexAutoTradeBinance8.Configuration;

/// <summary>
/// Which venues receive CORE live execution.
/// Signals stay unified; execution fans out per enabled exchange.
/// </summary>
public class ExchangeRuntimeOptions
{
    public const string SectionName = "Exchanges";

    /// <summary>Binance | Bybit | Dual</summary>
    public string Mode { get; set; } = "Binance";

    public bool EnableBinance { get; set; } = true;
    public bool EnableBybit { get; set; } = false;

    public bool IsBinanceActive =>
        EnableBinance && (
            string.Equals(Mode, "Binance", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Mode, "Dual", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Mode, "Both", StringComparison.OrdinalIgnoreCase));

    public bool IsBybitActive =>
        EnableBybit && (
            string.Equals(Mode, "Bybit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Mode, "Dual", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Mode, "Both", StringComparison.OrdinalIgnoreCase));
}
