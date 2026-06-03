namespace VertexAutoTradeBinance8.Configuration;

public class BinanceOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public bool IsTestNet { get; set; }
    public string[] Symbols { get; set; } = Array.Empty<string>();
    public bool UseFutures { get; set; } = true;

    public string AnoterApiKey { get; set; } = string.Empty;
    public string AnoterSecretKey { get; set; } = string.Empty;
}