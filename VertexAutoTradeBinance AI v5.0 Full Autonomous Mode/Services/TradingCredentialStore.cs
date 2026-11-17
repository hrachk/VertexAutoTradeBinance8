using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace VertexAutoTradeBinance8.Services;

/// <summary>
/// LIVE credentials per registered user.
/// A circuit sets <see cref="SetAmbientClient"/> so the UI uses that user's keys.
/// Background engine code may use TryGet only when exactly one user is live.
/// </summary>
public sealed class TradingCredentialStore
{
    private readonly object _gate = new();
    private static readonly AsyncLocal<string?> AmbientClient = new();

    private sealed class Keys
    {
        public string ClientId = "";
        public string ApiKey = "";
        public string ApiSecret = "";
        public bool Active;
    }

    private readonly Dictionary<string, Keys> _binance = new(StringComparer.OrdinalIgnoreCase);

    // Bybit stays single-slot; the product path in use is Binance per user.
    private string? _bybitClientId;
    private string? _bybitKey;
    private string? _bybitSecret;
    private bool _bybitActive;

    public event Action? Changed;

    public static void SetAmbientClient(string? clientId) =>
        AmbientClient.Value = string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim();

    public static void ClearAmbientClient() => AmbientClient.Value = null;

    public bool IsLiveActive
    {
        get { lock (_gate) return _binance.Values.Any(k => k.Active); }
    }

    public bool IsBybitLiveActive
    {
        get { lock (_gate) return _bybitActive && !string.IsNullOrWhiteSpace(_bybitKey) && !string.IsNullOrWhiteSpace(_bybitSecret); }
    }

    public string? ActiveClientId
    {
        get
        {
            lock (_gate)
            {
                if (TryPick(out var keys)) return keys.ClientId;
                return null;
            }
        }
    }

    public void ActivateLive(string clientId, string apiKey, string apiSecret)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("clientId required", nameof(clientId));
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
            throw new ArgumentException("apiKey/apiSecret required");

        lock (_gate)
        {
            _binance[clientId.Trim()] = new Keys
            {
                ClientId = clientId.Trim(),
                ApiKey = apiKey.Trim(),
                ApiSecret = apiSecret.Trim(),
                Active = true,
            };
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

    /// <summary>Remove every Binance user. Prefer <see cref="Deactivate(string)"/>.</summary>
    public void Deactivate()
    {
        bool wasActive;
        lock (_gate)
        {
            wasActive = _binance.Count > 0;
            _binance.Clear();
        }
        if (wasActive) Changed?.Invoke();
    }

    public void Deactivate(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            Deactivate();
            return;
        }
        bool removed;
        lock (_gate) removed = _binance.Remove(clientId.Trim());
        if (removed) Changed?.Invoke();
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
            if (TryPick(out var keys))
            {
                clientId = keys.ClientId;
                apiKey = keys.ApiKey;
                apiSecret = keys.ApiSecret;
                return true;
            }
        }
        clientId = apiKey = apiSecret = "";
        return false;
    }

    private bool TryPick(out Keys keys)
    {
        var ambient = AmbientClient.Value;
        if (!string.IsNullOrEmpty(ambient)
            && _binance.TryGetValue(ambient, out var mine)
            && mine.Active)
        {
            keys = mine;
            return true;
        }

        var active = _binance.Values.Where(k => k.Active).ToList();
        if (active.Count == 1)
        {
            keys = active[0];
            return true;
        }

        keys = null!;
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
