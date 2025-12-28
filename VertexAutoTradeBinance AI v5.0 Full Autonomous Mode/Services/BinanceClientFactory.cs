using Binance.Net.Clients;
using CryptoExchange.Net.Authentication;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;

namespace VertexAutoTradeBinance8.Services;

public class BinanceClientFactory
{
    private readonly BinanceOptions _options;
    private readonly ILogger<BinanceClientFactory> _logger;

    public BinanceClientFactory(
        IOptions<BinanceOptions> options,
        ILogger<BinanceClientFactory> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    // =====================================================
    // 🔴 CORE REST CLIENT (TradingWorker / critical paths)
    // =====================================================
    public BinanceRestClient CreateRestClient()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) ||
            string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new InvalidOperationException("Binance API credentials missing");

        return BuildRestClient(strict: true)!;
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
            _logger.LogInformation("[BINANCE] Creating REST client");

            return new BinanceRestClient(opt =>
            {
                opt.ApiCredentials = new ApiCredentials(
                    _options.ApiKey,
                    _options.SecretKey);

                // ✅ КОРРЕКТНО ДЛЯ Binance.Net 11.x
                opt.UsdFuturesOptions.AutoTimestamp = true;

                // Safe timeout (не ломает твою архитектуру)
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
    // 🔵 WS CLIENT (UserData / Streams)
    // =====================================================
    public BinanceSocketClient CreateSocketClient()
    {
        _logger.LogInformation("[BINANCE] Creating WS client");

        return new BinanceSocketClient(opt =>
        {
            opt.ApiCredentials = new ApiCredentials(
                _options.ApiKey,
                _options.SecretKey);
        });
    }
}
