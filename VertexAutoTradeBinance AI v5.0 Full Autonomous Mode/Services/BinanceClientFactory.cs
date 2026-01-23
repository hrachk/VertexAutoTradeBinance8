using Binance.Net.Clients;
using Binance.Net.Enums;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;

namespace VertexAutoTradeBinance8.Services;

public sealed class BinanceClientFactory
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

    // =========================================================
    // CORE REST CLIENT (TRADING / SUPERVISOR) — KEYS REQUIRED
    // =========================================================
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
            opt.UsdFuturesOptions.TradeRulesBehaviour =
                TradeRulesBehaviour.AutoComply;
        });
    }

    // =========================================================
    // OPTIONAL REST CLIENT (WEB / UI / LIVE-PNL)
    // =========================================================
    public BinanceRestClient? TryCreateRestClient()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) ||
            string.IsNullOrWhiteSpace(_options.SecretKey))
        {
            _logger.LogWarning(
                "[BINANCE] API credentials missing → private REST disabled");
            return null;
        }

        return new BinanceRestClient(opt =>
        {
            opt.ApiCredentials = new ApiCredentials(
                _options.ApiKey,
                _options.SecretKey);

            opt.UsdFuturesOptions.AutoTimestamp = true;
            opt.UsdFuturesOptions.TradeRulesBehaviour =
                TradeRulesBehaviour.AutoComply;
        });
    }

    // =========================================================
    // PUBLIC WS CLIENT (KLINES / MARK / DEPTH) — NO KEYS
    // =========================================================
    public BinanceSocketClient CreatePublicSocketClient()
    {
        return new BinanceSocketClient(_ =>
        {
            // ❗ НИКАКИХ ApiCredentials
            // Public market data only
        });
    }

    // =========================================================
    // PRIVATE WS CLIENT (USER DATA) — KEYS REQUIRED
    // =========================================================
    public BinanceSocketClient CreatePrivateSocketClient()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) ||
            string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new InvalidOperationException("Binance API credentials missing");

        return new BinanceSocketClient(opt =>
        {
            opt.ApiCredentials = new ApiCredentials(
                _options.ApiKey,
                _options.SecretKey);
        });
    }
}
