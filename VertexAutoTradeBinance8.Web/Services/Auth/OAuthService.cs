using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VertexAutoTradeBinance8.Web.Services.Auth;

/// <summary>
/// Validates OAuth tokens from Google, Telegram, Apple.
/// Returns a normalized OAuthUserInfo that ClientDbService uses to
/// find-or-create the ClientRecord.
/// </summary>
public sealed class OAuthService
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<OAuthService> _log;
    private readonly HttpClient _http;

    public OAuthService(IConfiguration cfg, ILogger<OAuthService> log, IHttpClientFactory factory)
    {
        _cfg  = cfg;
        _log  = log;
        _http = factory.CreateClient("oauth");
    }

    // ── Google ─────────────────────────────────────────────────────
    /// <summary>
    /// Validates Google ID token (returned from Google One Tap / OAuth popup).
    /// Uses Google tokeninfo endpoint — no secret needed, client-side flow.
    /// </summary>
    public async Task<OAuthUserInfo?> ValidateGoogleTokenAsync(string idToken)
    {
        try
        {
            var url  = $"https://oauth2.googleapis.com/tokeninfo?id_token={Uri.EscapeDataString(idToken)}";
            var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadFromJsonAsync<GoogleTokenInfo>();
            if (json == null) return null;

            // Verify audience matches our client ID
            var expectedAud = _cfg["OAuth:Google:ClientId"] ?? "";
            if (!string.IsNullOrEmpty(expectedAud) && json.Aud != expectedAud)
            {
                _log.LogWarning("[GOOGLE] Token audience mismatch: {aud}", json.Aud);
                return null;
            }

            if (json.EmailVerified != "true") return null;

            return new OAuthUserInfo(
                Provider:    "google",
                ProviderId:  json.Sub,
                Email:       json.Email ?? "",
                DisplayName: json.Name ?? json.Email ?? "",
                AvatarUrl:   json.Picture);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[GOOGLE] Token validation failed");
            return null;
        }
    }

    // ── Telegram ───────────────────────────────────────────────────
    /// <summary>
    /// Validates Telegram Login Widget data (HMAC-SHA256 signature).
    /// Data comes from Telegram Widget callback as URL-encoded key=value pairs.
    /// </summary>
    public OAuthUserInfo? ValidateTelegramLogin(Dictionary<string, string> data)
    {
        try
        {
            var botToken = _cfg["OAuth:Telegram:BotToken"] ?? "";
            if (string.IsNullOrEmpty(botToken))
            {
                _log.LogWarning("[TELEGRAM] BotToken not configured");
                return null;
            }

            if (!data.TryGetValue("hash", out var hash)) return null;

            // Build check string: sorted key=value pairs (excluding hash)
            var checkParts = data
                .Where(kv => kv.Key != "hash")
                .OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}={kv.Value}");
            var checkString = string.Join("\n", checkParts);

            // HMAC-SHA256 with SHA256(botToken) as key
            using var sha256   = SHA256.Create();
            var secretKey      = sha256.ComputeHash(Encoding.UTF8.GetBytes(botToken));
            using var hmac     = new HMACSHA256(secretKey);
            var computedHash   = Convert.ToHexString(
                hmac.ComputeHash(Encoding.UTF8.GetBytes(checkString))).ToLowerInvariant();

            if (computedHash != hash.ToLowerInvariant())
            {
                _log.LogWarning("[TELEGRAM] Hash mismatch — possible forgery");
                return null;
            }

            // Check auth_date is recent (within 24h)
            if (data.TryGetValue("auth_date", out var authDateStr) &&
                long.TryParse(authDateStr, out var authDate))
            {
                var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - authDate;
                if (age > 86_400) // 24 hours
                {
                    _log.LogWarning("[TELEGRAM] Auth data expired ({age}s old)", age);
                    return null;
                }
            }

            var id        = data.GetValueOrDefault("id", "");
            var firstName = data.GetValueOrDefault("first_name", "");
            var lastName  = data.GetValueOrDefault("last_name", "");
            var username  = data.GetValueOrDefault("username", "");
            var photo     = data.GetValueOrDefault("photo_url");

            var displayName = string.IsNullOrEmpty(username)
                ? $"{firstName} {lastName}".Trim()
                : $"@{username}";

            // Telegram doesn't provide email — use placeholder
            var email = $"tg_{id}@telegram.vertex";

            return new OAuthUserInfo(
                Provider:    "telegram",
                ProviderId:  id,
                Email:       email,
                DisplayName: displayName,
                AvatarUrl:   photo);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TELEGRAM] Validation failed");
            return null;
        }
    }

    // ── Apple ──────────────────────────────────────────────────────
    /// <summary>
    /// Validates Apple ID token (Sign in with Apple).
    /// Decodes JWT without full verification for now (verification
    /// requires fetching Apple's public keys — add if needed).
    /// Apple only sends email on FIRST login — subsequent logins have no email.
    /// </summary>
    public OAuthUserInfo? ValidateAppleToken(string identityToken, string? userJson = null)
    {
        try
        {
            // Decode JWT payload (base64url middle part)
            var parts = identityToken.Split('.');
            if (parts.Length < 2) return null;

            var payload = parts[1];
            // Fix base64url padding
            payload += new string('=', (4 - payload.Length % 4) % 4);
            payload = payload.Replace('-', '+').Replace('_', '/');

            var json    = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            var claims  = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (claims == null) return null;

            var sub   = claims.GetValueOrDefault("sub").GetString() ?? "";
            var email = claims.GetValueOrDefault("email").GetString() ?? "";

            // Apple may send user info JSON on first login
            string? displayName = null;
            if (!string.IsNullOrEmpty(userJson))
            {
                try
                {
                    var user = JsonSerializer.Deserialize<AppleUser>(userJson);
                    var fn   = user?.Name?.FirstName ?? "";
                    var ln   = user?.Name?.LastName  ?? "";
                    displayName = $"{fn} {ln}".Trim();
                }
                catch { }
            }

            if (string.IsNullOrEmpty(sub)) return null;

            // Use Apple ID as email placeholder if no email provided
            if (string.IsNullOrEmpty(email))
                email = $"apple_{sub}@apple.vertex";

            return new OAuthUserInfo(
                Provider:    "apple",
                ProviderId:  sub,
                Email:       email,
                DisplayName: displayName ?? email.Split('@')[0],
                AvatarUrl:   null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[APPLE] Token validation failed");
            return null;
        }
    }

    // ── Models ─────────────────────────────────────────────────────
    private sealed class GoogleTokenInfo
    {
        [JsonPropertyName("sub")]   public string? Sub   { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("name")]  public string? Name  { get; set; }
        [JsonPropertyName("picture")] public string? Picture { get; set; }
        [JsonPropertyName("aud")]   public string? Aud   { get; set; }
        [JsonPropertyName("email_verified")] public string? EmailVerified { get; set; }
    }

    private sealed class AppleUser
    {
        [JsonPropertyName("name")] public AppleName? Name { get; set; }
    }
    private sealed class AppleName
    {
        [JsonPropertyName("firstName")] public string? FirstName { get; set; }
        [JsonPropertyName("lastName")]  public string? LastName  { get; set; }
    }
}

/// <summary>Normalized user info from any OAuth provider.</summary>
public sealed record OAuthUserInfo(
    string  Provider,
    string  ProviderId,
    string  Email,
    string  DisplayName,
    string? AvatarUrl
);
