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

    // ❌ ОСТАВЛЯЕМ для TradingWorker / core (где ключи ГАРАНТИРОВАНЫ)
    public BinanceRestClient CreateRestClient()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) ||
            string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new InvalidOperationException("Binance API credentials missing");

        return new BinanceRestClient(opt =>
        {
            opt.ApiCredentials = new ApiCredentials(
                _options.ApiKey,
                _options.SecretKey);

            opt.UsdFuturesOptions.AutoTimestamp = true;
            
        });
    }


    // ✅ НОВЫЙ МЕТОД — ДЛЯ WEB / UI / LIVE-PNL
    public BinanceRestClient? TryCreateRestClient()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) ||
            string.IsNullOrWhiteSpace(_options.SecretKey))
        {
            _logger.LogWarning("[BINANCE] API credentials missing → private endpoints disabled");
            return null;
        }

        return new BinanceRestClient(opt =>
        {
            opt.ApiCredentials = new ApiCredentials(
                _options.ApiKey,
                _options.SecretKey);

            opt.UsdFuturesOptions.AutoTimestamp = true; 
        });
    }
}
