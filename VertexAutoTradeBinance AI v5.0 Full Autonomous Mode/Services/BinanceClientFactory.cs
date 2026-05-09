using Binance.Net;
using Binance.Net.Clients;
using CryptoExchange.Net.Authentication;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;

namespace VertexAutoTradeBinance8.Services;

public sealed class BinanceClientFactory
{
    private readonly IOptionsMonitor<BinanceOptions> _options;
    private readonly ILogger<BinanceClientFactory> _logger;

    private BinanceRestClient? _cachedRest;
    private readonly object _restLock = new();


    public BinanceClientFactory(
         IOptionsMonitor<BinanceOptions> options,
        ILogger<BinanceClientFactory> logger)
    {
        _options = options;
        _logger = logger;
    }
    // =====================================================
    // REST CLIENT (cached)
    // =====================================================
    public BinanceRestClient CreateRestClient()
    {
        var opt = _options.CurrentValue;

        if (_cachedRest != null)
            return _cachedRest;

        lock (_restLock)
        {
            if (_cachedRest != null)
                return _cachedRest;

            if (string.IsNullOrWhiteSpace(opt.ApiKey) ||
                string.IsNullOrWhiteSpace(opt.SecretKey))
                throw new InvalidOperationException("Binance API credentials missing");

            _logger.LogInformation(
                "[BINANCE] Creating REST client (cached) Mode={Mode}",
                opt.UseTestnet ? "TESTNET" : "LIVE");

            _cachedRest = new BinanceRestClient(cfg =>
            {
                cfg.ApiCredentials = new ApiCredentials(
                    opt.ApiKey,
                    opt.SecretKey);

                cfg.Environment = opt.UseTestnet
                    ? BinanceEnvironment.Testnet
                    : BinanceEnvironment.Live;

                cfg.AutoTimestamp = true;
                cfg.RequestTimeout = TimeSpan.FromSeconds(15);
            });

            return _cachedRest;
        }
    }


    // =====================================================
    // SAFE REST CLIENT (optional)
    // =====================================================
    public BinanceRestClient? TryCreateRestClient()
    {
        var opt = _options.CurrentValue;

        if (string.IsNullOrWhiteSpace(opt.ApiKey) ||
            string.IsNullOrWhiteSpace(opt.SecretKey))
        {
            _logger.LogWarning(
                "[BINANCE] API credentials missing → private endpoints disabled");

            return null;
        }

        return BuildRestClient(strict: false);
    }


    // =====================================================
    // INTERNAL REST BUILDER
    // =====================================================
    private BinanceRestClient? BuildRestClient(bool strict)
    {
        var opt = _options.CurrentValue;

        try
        {
            _logger.LogInformation(
                "[BINANCE] Creating REST client Mode={Mode}",
                opt.UseTestnet ? "TESTNET" : "LIVE");

            return new BinanceRestClient(cfg =>
            {
                cfg.ApiCredentials = new ApiCredentials(
                    opt.ApiKey,
                    opt.SecretKey);

                cfg.Environment = opt.UseTestnet
                    ? BinanceEnvironment.Testnet
                    : BinanceEnvironment.Live;

                cfg.AutoTimestamp = true;
                cfg.RequestTimeout = TimeSpan.FromSeconds(15);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BINANCE] REST client creation failed");

            if (strict)
                throw;

            return null;
        }
    }


    // =====================================================
    // WS CLIENT
    // =====================================================
    public BinanceSocketClient CreateSocketClient()
    {
        var opt = _options.CurrentValue;

        _logger.LogInformation(
            "[BINANCE] Creating WS client Mode={Mode}",
            opt.UseTestnet ? "TESTNET" : "LIVE");

        return new BinanceSocketClient(cfg =>
        {
            cfg.ApiCredentials = new ApiCredentials(
                opt.ApiKey,
                opt.SecretKey);

            cfg.Environment = opt.UseTestnet
                ? BinanceEnvironment.Testnet
                : BinanceEnvironment.Live;
        });
    }
}
