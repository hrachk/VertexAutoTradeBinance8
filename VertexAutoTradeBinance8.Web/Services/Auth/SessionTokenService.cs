using System.Security.Cryptography;
using System.Text.Json;

namespace VertexAutoTradeBinance8.Web.Services.Auth;

/// <summary>
/// Manages persistent login tokens stored in sessions.json.
/// Each token maps to a client ID and has an expiry.
/// Used to restore the session from a browser cookie on page reload.
/// Token lifetime: 30 days (Remember Me) or 8 hours (session only).
/// </summary>
public sealed class SessionTokenService
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<SessionTokenService> _log;

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public const int RememberMeDays  = 30;
    public const int SessionHours    = 8;

    public SessionTokenService(IConfiguration cfg, ILogger<SessionTokenService> log)
    {
        _log = log;
        var root = cfg["SharedData:Root"] ?? AppContext.BaseDirectory;
        var dir  = Path.GetDirectoryName(root.TrimEnd('\\', '/')) ?? root;
        try { Directory.CreateDirectory(dir); } catch { }
        _filePath = Path.Combine(dir, "sessions.json");
        Cleanup(); // remove expired on startup
    }

    // ── Create token ─────────────────────────────────────────
    public async Task<string> CreateAsync(string clientId, bool rememberMe)
    {
        var token   = GenerateToken();
        var expires = rememberMe
            ? DateTime.UtcNow.AddDays(RememberMeDays)
            : DateTime.UtcNow.AddHours(SessionHours);

        await _lock.WaitAsync();
        try
        {
            var sessions = Read();
            // Remove old sessions for this client to avoid accumulation
            sessions.RemoveAll(s => s.ClientId == clientId && !s.RememberMe);
            sessions.Add(new SessionEntry(token, clientId, expires, rememberMe));
            Write(sessions);
        }
        finally { _lock.Release(); }

        _log.LogInformation("[TOKEN] Created for {id}, expires {exp:u}, rememberMe={rm}",
            clientId, expires, rememberMe);
        return token;
    }

    // ── Validate token ────────────────────────────────────────
    public async Task<string?> ValidateAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        await _lock.WaitAsync();
        try
        {
            var sessions = Read();
            var entry    = sessions.FirstOrDefault(s => s.Token == token);
            if (entry == null) return null;
            if (entry.Expires < DateTime.UtcNow)
            {
                sessions.Remove(entry);
                Write(sessions);
                return null;
            }
            // Slide expiry for RememberMe tokens
            if (entry.RememberMe && entry.Expires < DateTime.UtcNow.AddDays(RememberMeDays - 1))
            {
                entry = entry with { Expires = DateTime.UtcNow.AddDays(RememberMeDays) };
                var idx = sessions.FindIndex(s => s.Token == token);
                if (idx >= 0) sessions[idx] = entry;
                Write(sessions);
            }
            return entry.ClientId;
        }
        finally { _lock.Release(); }
    }

    // ── Revoke token ──────────────────────────────────────────
    public async Task RevokeAsync(string token)
    {
        await _lock.WaitAsync();
        try
        {
            var sessions = Read();
            sessions.RemoveAll(s => s.Token == token);
            Write(sessions);
        }
        finally { _lock.Release(); }
    }

    // ── Revoke all for client ─────────────────────────────────
    public async Task RevokeAllAsync(string clientId)
    {
        await _lock.WaitAsync();
        try
        {
            var sessions = Read();
            sessions.RemoveAll(s => s.ClientId == clientId);
            Write(sessions);
        }
        finally { _lock.Release(); }
    }

    // ── Cleanup expired ───────────────────────────────────────
    public void Cleanup()
    {
        _lock.Wait();
        try
        {
            if (!File.Exists(_filePath)) return;
            var sessions = Read();
            var before   = sessions.Count;
            sessions.RemoveAll(s => s.Expires < DateTime.UtcNow);
            if (sessions.Count != before) Write(sessions);
        }
        finally { _lock.Release(); }
    }

    // ── Helpers ───────────────────────────────────────────────
    private static string GenerateToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .Replace('+', '-').Replace('/', '_').Replace('=', '');

    private List<SessionEntry> Read()
    {
        if (!File.Exists(_filePath)) return new();
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<SessionEntry>>(json, _json) ?? new();
        }
        catch { return new(); }
    }

    private void Write(List<SessionEntry> sessions)
    {
        File.WriteAllText(_filePath, JsonSerializer.Serialize(sessions, _json));
    }

    private record SessionEntry(
        string   Token,
        string   ClientId,
        DateTime Expires,
        bool     RememberMe
    );
}
