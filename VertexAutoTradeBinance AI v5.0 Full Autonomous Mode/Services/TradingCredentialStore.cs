using System;

namespace VertexAutoTradeBinance8.Services;

/// <summary>
/// Process-wide active LIVE trading credentials for the current user.
///
/// Source of truth for order placement:
///   1. ActivateLive(clientId, apiKey, apiSecret) — user switched to LIVE
///   2. Fallback: appsettings Binance:ApiKey/SecretKey (single-tenant engine)
///
/// Demo mode MUST call Deactivate() so no real orders can leak onto
/// another user's keys or the shared config keys by accident.
/// </summary>
public sealed class TradingCredentialStore
{
    private readonly object _gate = new();
    private string? _clientId;
    private string? _apiKey;
    private string? _apiSecret;
    private bool _active;

    /// <summary>Raised when active credentials change (factory must drop cached clients).</summary>
    public event Action? Changed;

    public bool IsLiveActive
    {
        get { lock (_gate) return _active && !string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(_apiSecret); }
    }

    public string? ActiveClientId
    {
        get { lock (_gate) return _active ? _clientId : null; }
    }

    public void ActivateLive(string clientId, string apiKey, string apiSecret)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("clientId required", nameof(clientId));
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
            throw new ArgumentException("apiKey/apiSecret required");

        lock (_gate)
        {
            _clientId  = clientId.Trim();
            _apiKey    = apiKey.Trim();
            _apiSecret = apiSecret.Trim();
            _active    = true;
        }
        Changed?.Invoke();
    }

    public void Deactivate()
    {
        bool wasActive;
        lock (_gate)
        {
            wasActive  = _active;
            _active    = false;
            _clientId  = null;
            _apiKey    = null;
            _apiSecret = null;
        }
        if (wasActive) Changed?.Invoke();
    }

    /// <summary>Returns true when LIVE user keys are active.</summary>
    public bool TryGet(out string clientId, out string apiKey, out string apiSecret)
    {
        lock (_gate)
        {
            if (_active && !string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(_apiSecret))
            {
                clientId  = _clientId ?? "";
                apiKey    = _apiKey!;
                apiSecret = _apiSecret!;
                return true;
            }
        }
        clientId = apiKey = apiSecret = "";
        return false;
    }
}
