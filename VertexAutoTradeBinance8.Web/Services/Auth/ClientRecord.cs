using System.Text.Json.Serialization;

namespace VertexAutoTradeBinance8.Web.Services.Auth;

/// <summary>
/// One registered user. Stored as element of clients.json.
/// Supports: Email/Password + Google OAuth + Telegram + Apple ID
/// </summary>
public sealed class ClientRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("email")]
    public string Email { get; set; } = "";

    [JsonPropertyName("passwordHash")]
    public string PasswordHash { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("plan")]
    public string Plan { get; set; } = "demo";

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; } = true;

    [JsonPropertyName("isEmailVerified")]
    public bool IsEmailVerified { get; set; } = false;

    [JsonPropertyName("emailVerifyCode")]
    public string? EmailVerifyCode { get; set; }

    [JsonPropertyName("emailVerifyExpires")]
    public DateTime? EmailVerifyExpires { get; set; }

    [JsonPropertyName("emailVerifyAttempts")]
    public int EmailVerifyAttempts { get; set; } = 0;

    // ── OAuth provider links ────────────────────────────────────────
    /// <summary>Google subject ID from JWT (sub claim)</summary>
    [JsonPropertyName("googleId")]
    public string? GoogleId { get; set; }

    /// <summary>Telegram user ID (numeric)</summary>
    [JsonPropertyName("telegramId")]
    public string? TelegramId { get; set; }

    /// <summary>Apple subject ID from identity token</summary>
    [JsonPropertyName("appleId")]
    public string? AppleId { get; set; }

    /// <summary>Which providers are linked to this account</summary>
    [JsonPropertyName("authProviders")]
    public List<string> AuthProviders { get; set; } = new(); // "email","google","telegram","apple"

    // ── Account data ────────────────────────────────────────────────
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("lastLoginAt")]
    public DateTime? LastLoginAt { get; set; }

    [JsonPropertyName("demoBalance")]
    public decimal DemoBalance { get; set; } = 10_000m;

    [JsonPropertyName("binanceApiKeyEnc")]
    public string? BinanceApiKeyEnc { get; set; }

    [JsonPropertyName("binanceApiSecretEnc")]
    public string? BinanceApiSecretEnc { get; set; }

    [JsonPropertyName("isLiveEnabled")]
    public bool IsLiveEnabled { get; set; } = false;

    [JsonIgnore]
    public string DataFolder => Path.Combine(@"C:\Vertex\Engines", Id);

    [JsonIgnore]
    public bool NeedsEmailVerification =>
        !IsEmailVerified && AuthProviders.Contains("email");

    [JsonIgnore]
    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);
}
