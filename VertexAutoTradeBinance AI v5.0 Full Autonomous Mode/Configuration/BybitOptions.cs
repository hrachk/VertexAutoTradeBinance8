namespace VertexAutoTradeBinance8.Configuration;

/// <summary>
/// Bybit USDT-perpetual credentials and environment.
/// Parallel to BinanceOptions — both can be active (Exchanges:Mode = Dual).
/// </summary>
public class BybitOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    /// <summary>true = Bybit testnet / demo trading environment.</summary>
    public bool UseTestnet { get; set; } = false;
}
