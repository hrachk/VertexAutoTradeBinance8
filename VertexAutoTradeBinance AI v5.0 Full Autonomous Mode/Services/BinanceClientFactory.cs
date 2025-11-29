using Binance.Net.Clients;
using CryptoExchange.Net.Authentication;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;

namespace VertexAutoTradeBinance8.Services;

public class BinanceClientFactory
{
    private readonly BinanceOptions _options;

    public BinanceClientFactory(IOptions<BinanceOptions> options)
    {
        _options = options.Value;
    }

    public BinanceRestClient CreateRestClient()
    {
        var client = new BinanceRestClient();
        client.SetApiCredentials(new ApiCredentials(_options.ApiKey, _options.SecretKey));
        return client;
    }
}