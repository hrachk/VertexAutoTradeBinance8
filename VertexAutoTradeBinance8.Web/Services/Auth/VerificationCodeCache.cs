using System.Collections.Concurrent;

namespace VertexAutoTradeBinance8.Web.Services.Auth;

/// <summary>
/// Short-lived store for plaintext email verification codes.
/// Used to (1) show the code on the verify page when SMTP is not configured
/// or delivery failed, and (2) write an audit line to email-outbox.log.
/// Codes expire in 15 minutes and are never written to clients.json.
/// </summary>
public sealed class VerificationCodeCache
{
    private readonly ConcurrentDictionary<string, (string code, DateTime exp)> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _outboxPath;
    private readonly ILogger<VerificationCodeCache> _log;

    public VerificationCodeCache(IConfiguration cfg, ILogger<VerificationCodeCache> log)
    {
        _log = log;
        var root = cfg["SharedData:Root"];
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(AppContext.BaseDirectory, "vertex-data");
        var dir = Path.GetDirectoryName(root.TrimEnd('\\', '/')) ?? root;
        try { Directory.CreateDirectory(dir); } catch { }
        _outboxPath = Path.Combine(dir, "email-outbox.log");
    }

    public void Store(string clientId, string email, string code, TimeSpan ttl)
    {
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(code)) return;
        _map[clientId] = (code, DateTime.UtcNow.Add(ttl));
        try
        {
            var line = $"{DateTime.UtcNow:O}\t{clientId}\t{email}\tCODE={code}\tTTL={ttl.TotalMinutes}m\n";
            File.AppendAllText(_outboxPath, line);
            _log.LogInformation("[VERIFY] Code stored for {email} (outbox={path})", email, _outboxPath);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[VERIFY] Outbox write failed");
        }
    }

    public string? Peek(string clientId)
    {
        if (!_map.TryGetValue(clientId, out var e)) return null;
        if (DateTime.UtcNow > e.exp)
        {
            _map.TryRemove(clientId, out _);
            return null;
        }
        return e.code;
    }

    public void Invalidate(string clientId) => _map.TryRemove(clientId, out _);
}
