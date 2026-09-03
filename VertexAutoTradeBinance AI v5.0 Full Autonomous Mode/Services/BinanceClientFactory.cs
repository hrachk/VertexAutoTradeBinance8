using Binance.Net;
using Binance.Net.Clients;
using CryptoExchange.Net.Authentication;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;

namespace VertexAutoTradeBinance8.Services;

/// <summary>
/// Creates Binance REST/WS clients.
/// Credential resolution order:
///   1. TradingCredentialStore (per-user LIVE keys from registration)
///   2. appsettings BinanceOptions (legacy single-tenant / engine bootstrap)
/// </summary>
public sealed class BinanceClientFactory
{
    private readonly IOptionsMonitor<BinanceOptions> _options;
    private readonly TradingCredentialStore _creds;
    private readonly ILogger<BinanceClientFactory> _logger;

    private BinanceRestClient? _cachedRest;
    private string? _cachedKeyFingerprint;
    private readonly object _restLock = new();

    public BinanceClientFactory(
        IOptionsMonitor<BinanceOptions> options,
        TradingCredentialStore creds,
        ILogger<BinanceClientFactory> logger)
    {
        _options = options;
        _creds = creds;
        _logger = logger;
        _creds.Changed += InvalidateCache;
    }

    /// <summary>Drop cached REST client so next call uses current credentials.</summary>
    public void InvalidateCache()
    {
        lock (_restLock)
        {
            try { _cachedRest?.Dispose(); } catch { /* ignore */ }
            _cachedRest = null;
            _cachedKeyFingerprint = null;
        }
        _logger.LogInformation("[BINANCE] Client cache invalidated (credentials changed)");
    }

    public bool TryGetCredentials(out string apiKey, out string apiSecret)
    {
        var ok = ResolveCredentials(out apiKey, out apiSecret, out _);
        if (ok)
        {
            apiKey = (apiKey ?? "").Trim();
            apiSecret = (apiSecret ?? "").Trim();
        }
        return ok && !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret);
    }

    private bool ResolveCredentials(out string apiKey, out string apiSecret, out string source)
    {
        if (_creds.TryGet(out var clientId, out apiKey, out apiSecret))
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

    public BinanceRestClient CreateRestClient()
    {
        if (!ResolveCredentials(out var apiKey, out var apiSecret, out var source))
            throw new InvalidOperationException(
                "Binance API credentials missing. Switch to LIVE and save API keys, or set Binance:ApiKey in config.");

        var fp = source + ":" + apiKey.GetHashCode();
        if (_cachedRest != null && _cachedKeyFingerprint == fp)
            return _cachedRest;

        lock (_restLock)
        {
            if (_cachedRest != null && _cachedKeyFingerprint == fp)
                return _cachedRest;

            try { _cachedRest?.Dispose(); } catch { }

            var opt = _options.CurrentValue;
            _logger.LogInformation(
                "[BINANCE] Creating REST client source={src} Mode={Mode}",
                source, opt.UseTestnet ? "TESTNET" : "LIVE");

            _cachedRest = new BinanceRestClient(cfg =>
            {
                cfg.ApiCredentials = new BinanceCredentials(apiKey, apiSecret);
                cfg.Environment = opt.UseTestnet
                    ? BinanceEnvironment.Testnet
                    : BinanceEnvironment.Live;
                cfg.AutoTimestamp = true;
                cfg.RequestTimeout = TimeSpan.FromSeconds(15);
            });
            _cachedKeyFingerprint = fp;
            return _cachedRest;
        }
    }

    public BinanceRestClient? TryCreateRestClient()
    {
        if (!ResolveCredentials(out _, out _, out _))
        {
            _logger.LogWarning("[BINANCE] API credentials missing → private endpoints disabled");
            return null;
        }
        try { return CreateRestClient(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BINANCE] REST client creation failed");
            return null;
        }
    }

    /// <summary>
    /// Socket client for market-data streams (klines, book ticker, etc.).
    /// Public streams do NOT require API keys — so missing credentials must
    /// not crash host startup (WsKlineSubscriber is constructed at boot).
    /// When LIVE user keys (or appsettings keys) are present they are attached
    /// for private user-data streams.
    /// </summary>
    
    /// <summary>
    /// Public market-data REST client (tickers, klines, exchangeInfo).
    /// Does NOT require API keys — SymbolLiquidityScanner must use this so
    /// quality universe still fills when LIVE keys are missing/invalid.
    /// </summary>
    public BinanceRestClient CreatePublicRestClient()
    {
        var opt = _options.CurrentValue;
        return new BinanceRestClient(cfg =>
        {
            cfg.Environment = opt.UseTestnet
                ? BinanceEnvironment.Testnet
                : BinanceEnvironment.Live;
            cfg.AutoTimestamp = true;
            // no ApiCredentials — public endpoints only
        });
    }

    public BinanceSocketClient CreateSocketClient()
    {
        var opt = _options.CurrentValue;
        var hasCreds = ResolveCredentials(out var apiKey, out var apiSecret, out var source);

        _logger.LogInformation(
            "[BINANCE] Creating WS client source={src} Mode={Mode}",
            hasCreds ? source : "public",
            opt.UseTestnet ? "TESTNET" : "LIVE");

        return new BinanceSocketClient(cfg =>
        {
            if (hasCreds)
                cfg.ApiCredentials = new BinanceCredentials(apiKey, apiSecret);

            cfg.Environment = opt.UseTestnet
                ? BinanceEnvironment.Testnet
                : BinanceEnvironment.Live;
        });
    }
}
