using Binance.Net;
using Binance.Net.Clients;
using CryptoExchange.Net.Authentication;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;

namespace VertexAutoTradeBinance8.Services;

public sealed class BinanceClientFactory
{
    private readonly BinanceOptions _options;
    private readonly ILogger<BinanceClientFactory> _logger;

    private BinanceRestClient? _cachedRest;

    public BinanceClientFactory(
        IOptions<BinanceOptions> options,
        ILogger<BinanceClientFactory> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    // =====================================================
    // CORE REST (CACHED, PRODUCTION)
    // =====================================================
    //public BinanceRestClient CreateRestClient()
    //{
    //    if (_cachedRest != null)
    //        return _cachedRest;

    //    if (string.IsNullOrWhiteSpace(_options.ApiKey) ||
    //        string.IsNullOrWhiteSpace(_options.SecretKey))
    //        throw new InvalidOperationException("Binance API credentials missing");

    //    _logger.LogInformation("[BINANCE] Creating REST client (cached)");

    //    _cachedRest = new BinanceRestClient(opt =>
    //    {
    //        opt.ApiCredentials = new ApiCredentials(
    //            _options.ApiKey,
    //            _options.SecretKey);

    //        opt.UsdFuturesOptions.AutoTimestamp = true;
    //        opt.RequestTimeout = TimeSpan.FromSeconds(15);
    //    });

    //    return _cachedRest;
    //}
    public BinanceRestClient CreateRestClient()
    {
        if (_cachedRest != null)
            return _cachedRest;

        if (string.IsNullOrWhiteSpace(_options.ApiKey) ||
            string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new InvalidOperationException("Binance API credentials missing");

        _logger.LogInformation(
            "[BINANCE] Creating REST client (cached) Mode={Mode}",
            _options.UseTestnet ? "TESTNET" : "LIVE");

        _cachedRest = new BinanceRestClient(opt =>
        {
            opt.ApiCredentials = new ApiCredentials(
                _options.ApiKey,
                _options.SecretKey);

            // ✅ ЕДИНСТВЕННО правильный способ переключения
            opt.Environment = _options.UseTestnet
                ? BinanceEnvironment.Testnet
                : BinanceEnvironment.Live;

            opt.AutoTimestamp = true;

            opt.RequestTimeout = TimeSpan.FromSeconds(15);
        });

        return _cachedRest;
    }



    // =====================================================
    // 🟡 SAFE REST CLIENT (UI / WS bootstrap / monitoring)
    // =====================================================
    public BinanceRestClient? TryCreateRestClient()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) ||
            string.IsNullOrWhiteSpace(_options.SecretKey))
        {
            _logger.LogWarning(
                "[BINANCE] API credentials missing → private endpoints disabled");
            return null;
        }

        return BuildRestClient(strict: false);
    }
    // =====================================================
    // 🔧 INTERNAL REST BUILDER (CANONICAL)
    // =====================================================
    private BinanceRestClient? BuildRestClient(bool strict)
    {
        try
        {
            _logger.LogInformation(
                "[BINANCE] Creating REST client Mode={Mode}",
                _options.UseTestnet ? "TESTNET" : "LIVE");

            return new BinanceRestClient(opt =>
            {
                opt.ApiCredentials = new ApiCredentials(
                    _options.ApiKey,
                    _options.SecretKey);

                // ✅ ЕДИНСТВЕННО правильный способ переключения
                opt.Environment = _options.UseTestnet
                    ? BinanceEnvironment.Testnet
                    : BinanceEnvironment.Live;

                // общий AutoTimestamp (правильное место)
                opt.AutoTimestamp = true;

                opt.RequestTimeout = TimeSpan.FromSeconds(15);
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
    //public BinanceSocketClient CreateSocketClient()
    //{
    //    _logger.LogInformation("[BINANCE] Creating WS client");

    //    return new BinanceSocketClient(opt =>
    //    {
    //        opt.ApiCredentials = new ApiCredentials(
    //            _options.ApiKey,
    //            _options.SecretKey);
    //    });
    //}
    public BinanceSocketClient CreateSocketClient()
    {
        _logger.LogInformation(
            "[BINANCE] Creating WS client Mode={Mode}",
            _options.UseTestnet ? "TESTNET" : "LIVE");

        return new BinanceSocketClient(opt =>
        {
            opt.ApiCredentials = new ApiCredentials(
                _options.ApiKey,
                _options.SecretKey);

            // ✅ Правильное переключение Testnet / Live
            opt.Environment = _options.UseTestnet
                ? BinanceEnvironment.Testnet
                : BinanceEnvironment.Live;
        });
    }

}
