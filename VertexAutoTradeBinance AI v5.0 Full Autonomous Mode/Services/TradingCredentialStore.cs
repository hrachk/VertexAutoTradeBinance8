using System;

namespace VertexAutoTradeBinance8.Services;

/// <summary>
/// Process-wide LIVE credentials.
/// Binance and Bybit are independent so Dual mode can hold both.
/// Demo mode MUST call Deactivate() / DeactivateBybit() as appropriate.
/// </summary>
public sealed class TradingCredentialStore
{
    private readonly object _gate = new();

    // Binance
    private string? _clientId;
    private string? _apiKey;
    private string? _apiSecret;
    private bool _active;

    // Bybit
    private string? _bybitClientId;
    private string? _bybitKey;
    private string? _bybitSecret;
    private bool _bybitActive;

    public event Action? Changed;

    public bool IsLiveActive
    {
        get { lock (_gate) return _active && !string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(_apiSecret); }
    }

    public bool IsBybitLiveActive
    {
        get { lock (_gate) return _bybitActive && !string.IsNullOrWhiteSpace(_bybitKey) && !string.IsNullOrWhiteSpace(_bybitSecret); }
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

    public void ActivateBybit(string clientId, string apiKey, string apiSecret)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("clientId required", nameof(clientId));
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
            throw new ArgumentException("apiKey/apiSecret required");

        lock (_gate)
        {
            _bybitClientId = clientId.Trim();
            _bybitKey      = apiKey.Trim();
            _bybitSecret   = apiSecret.Trim();
            _bybitActive   = true;
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

    public void DeactivateBybit()
    {
        bool wasActive;
        lock (_gate)
        {
            wasActive      = _bybitActive;
            _bybitActive   = false;
            _bybitClientId = null;
            _bybitKey      = null;
            _bybitSecret   = null;
        }
        if (wasActive) Changed?.Invoke();
    }

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

    public bool TryGetBybit(out string clientId, out string apiKey, out string apiSecret)
    {
        lock (_gate)
        {
            if (_bybitActive && !string.IsNullOrWhiteSpace(_bybitKey) && !string.IsNullOrWhiteSpace(_bybitSecret))
            {
                clientId  = _bybitClientId ?? "";
                apiKey    = _bybitKey!;
                apiSecret = _bybitSecret!;
                return true;
            }
        }
        clientId = apiKey = apiSecret = "";
        return false;
    }
}
