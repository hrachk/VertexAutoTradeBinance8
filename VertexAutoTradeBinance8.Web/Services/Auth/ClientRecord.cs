using System.Text.Json.Serialization;

namespace VertexAutoTradeBinance8.Web.Services.Auth;

/// <summary>
/// One registered user. Stored as an element of clients.json.
/// Passwords are bcrypt-hashed (never stored in plain text).
/// Binance keys are AES-256 encrypted.
/// </summary>
public sealed class ClientRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";             // "client_001", "client_002" …

    [JsonPropertyName("email")]
    public string Email { get; set; } = "";

    [JsonPropertyName("passwordHash")]
    public string PasswordHash { get; set; } = "";   // BCrypt hash

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("plan")]
    public string Plan { get; set; } = "demo";       // "demo" | "live" | "pro"

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; } = true;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("lastLoginAt")]
    public DateTime? LastLoginAt { get; set; }

    [JsonPropertyName("demoBalance")]
    public decimal DemoBalance { get; set; } = 10_000m;

    // Binance API keys — stored AES-256 encrypted
    [JsonPropertyName("binanceApiKeyEnc")]
    public string? BinanceApiKeyEnc { get; set; }

    [JsonPropertyName("binanceApiSecretEnc")]
    public string? BinanceApiSecretEnc { get; set; }

    [JsonPropertyName("isLiveEnabled")]
    public bool IsLiveEnabled { get; set; } = false;

    // Derived: client data folder
    [JsonIgnore]
    public string DataFolder => Path.Combine(
        @"C:\Vertex\Engines", Id);
}
