using System.Text.Json.Serialization;

namespace VertexAutoTradeBinance8.Web.Services.Auth;

/// <summary>
/// One registered user. Stored as element of clients.json.
/// Passwords: PBKDF2. Binance keys: AES-256. Email: verified via 6-digit code.
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
    public bool NeedsEmailVerification => !IsEmailVerified && !string.IsNullOrEmpty(EmailVerifyCode);
}
