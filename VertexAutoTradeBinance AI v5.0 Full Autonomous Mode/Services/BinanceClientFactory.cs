using Binance.Net;
using Binance.Net.Clients;
using CryptoExchange.Net.Authentication;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;

namespace VertexAutoTradeBinance8.Services;

/// <summary>
/// Один долгоживущий BinanceRestClient на процесс.
/// Раньше клиент создавался на КАЖДЫЙ вызов (десятки в секунду) → новые HttpClient,
/// исчерпание сокетов, потеря внутреннего rate-limiter'а и игнор IsTestNet.
/// Вызовы `using var client = ...` по коду удалены — клиент не должен диспоузиться.
/// </summary>
public sealed class BinanceClientFactory : IDisposable
{
    private readonly BinanceOptions _options;
    private readonly ILogger<BinanceClientFactory>? _logger;
    private readonly Lazy<BinanceRestClient> _client;

    public BinanceClientFactory(IOptions<BinanceOptions> options, ILogger<BinanceClientFactory>? logger = null)
    {
        _options = options.Value;
        _logger = logger;

        // Fallback: классические имена переменных окружения
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            _options.ApiKey = System.Environment.GetEnvironmentVariable("BINANCE_API_KEY") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            _options.SecretKey = System.Environment.GetEnvironmentVariable("BINANCE_API_SECRET") ?? string.Empty;

        _client = new Lazy<BinanceRestClient>(Build, isThreadSafe: true);
    }

    public string EnvironmentName => _options.ResolvedEnvironment;
    public bool HasCredentials => _options.HasCredentials;

    private BinanceRestClient Build()
    {
        var envName = _options.ResolvedEnvironment;
        var env = envName.ToLowerInvariant() switch
        {
            "testnet" => BinanceEnvironment.Testnet,
            "demo" => BinanceEnvironment.Demo,
            _ => BinanceEnvironment.Live
        };

        if (!_options.HasCredentials)
            _logger?.LogCritical("[BINANCE] API key/secret NOT configured — private endpoints will fail. " +
                                 "Set env Binance__ApiKey / Binance__SecretKey.");

        _logger?.LogWarning("[BINANCE] REST client environment = {env}", envName);

        return new BinanceRestClient(o =>
        {
            o.Environment = env;
            if (_options.HasCredentials)
                o.ApiCredentials = new ApiCredentials(_options.ApiKey, _options.SecretKey);
            o.RequestTimeout = TimeSpan.FromSeconds(15);
            o.AutoTimestamp = true;
        });
    }

    /// <summary>Возвращает общий клиент. НЕ оборачивайте в using.</summary>
    public BinanceRestClient CreateRestClient() => _client.Value;

    public void Dispose()
    {
        if (_client.IsValueCreated) _client.Value.Dispose();
    }
}
