namespace VertexAutoTradeBinance8.Configuration;

public class BinanceOptions
{
    /// <summary>
    /// НЕ храните ключи в appsettings.json в репозитории.
    /// Источники (по приоритету): переменные окружения Binance__ApiKey / BINANCE_API_KEY,
    /// dotnet user-secrets, appsettings.Local.json (в .gitignore).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Legacy флаг. true = Testnet. Используйте Environment.</summary>
    public bool IsTestNet { get; set; }

    /// <summary>"Live" | "Testnet" | "Demo". Пусто → по IsTestNet.</summary>
    public string Environment { get; set; } = string.Empty;

    public string[] Symbols { get; set; } = Array.Empty<string>();
    public bool UseFutures { get; set; } = true;

    public string ResolvedEnvironment =>
        !string.IsNullOrWhiteSpace(Environment) ? Environment.Trim() : (IsTestNet ? "Testnet" : "Live");

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(SecretKey);
}
