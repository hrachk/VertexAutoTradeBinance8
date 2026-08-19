using Bybit.Net.Clients;
using CryptoExchange.Net.Authentication;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;

namespace VertexAutoTradeBinance8.Services;

/// <summary>
/// Creates Bybit REST clients for USDT linear perps.
/// Credential order:
///   1. TradingCredentialStore Bybit LIVE keys
///   2. appsettings Bybit:ApiKey / SecretKey
/// </summary>
public sealed class BybitClientFactory
{
    private readonly IOptionsMonitor<BybitOptions> _options;
    private readonly TradingCredentialStore _creds;
    private readonly ILogger<BybitClientFactory> _logger;

    private readonly object _restLock = new();
    private BybitRestClient? _cachedRest;
    private string? _cachedKeyFingerprint;

    public BybitClientFactory(
        IOptionsMonitor<BybitOptions> options,
        TradingCredentialStore creds,
        ILogger<BybitClientFactory> logger)
    {
        _options = options;
        _creds = creds;
        _logger = logger;
        _creds.Changed += InvalidateCache;
    }

    public void InvalidateCache()
    {
        lock (_restLock)
        {
            try { _cachedRest?.Dispose(); } catch { /* ignore */ }
            _cachedRest = null;
            _cachedKeyFingerprint = null;
        }
        _logger.LogInformation("[BYBIT] Client cache invalidated");
    }

    private bool ResolveCredentials(out string apiKey, out string apiSecret, out string source)
    {
        if (_creds.TryGetBybit(out var clientId, out apiKey, out apiSecret))
        {
            source = $"user:{clientId}";
            return true;
        }

        var opt = _options.CurrentValue;
        apiKey = opt.ApiKey ?? "";
        apiSecret = opt.SecretKey ?? "";
        source = "appsettings";
        return !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret);
    }

    public bool HasCredentials()
    {
        return ResolveCredentials(out _, out _, out _);
    }

    public BybitRestClient CreateRestClient()
    {
        if (!ResolveCredentials(out var apiKey, out var apiSecret, out var source))
            throw new InvalidOperationException(
                "Bybit API credentials missing. Save Bybit keys in LIVE settings or set Bybit:ApiKey in config.");

        var fp = source + ":" + apiKey.GetHashCode();
        lock (_restLock)
        {
            if (_cachedRest != null && _cachedKeyFingerprint == fp)
                return _cachedRest;

            try { _cachedRest?.Dispose(); } catch { }

            var useTestnet = _options.CurrentValue.UseTestnet;
            var client = new BybitRestClient(opts =>
            {
                opts.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
                if (useTestnet)
                    opts.Environment = Bybit.Net.BybitEnvironment.Testnet;
            });

            _cachedRest = client;
            _cachedKeyFingerprint = fp;
            _logger.LogInformation("[BYBIT] REST client created source={src} testnet={tn}", source, useTestnet);
            return client;
        }
    }

    public BybitRestClient? TryCreateRestClient()
    {
        try { return CreateRestClient(); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[BYBIT] TryCreateRestClient failed");
            return null;
        }
    }
}
