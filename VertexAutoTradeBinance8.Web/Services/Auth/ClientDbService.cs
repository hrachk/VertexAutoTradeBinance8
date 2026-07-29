using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VertexAutoTradeBinance8.Web.Services.Auth;

/// <summary>
/// JSON-based client storage (clients.json).
/// Thread-safe via SemaphoreSlim.
/// Drop-in replacement when switching to DB later.
/// </summary>
public sealed class ClientDbService
{
    private readonly string _filePath;
    private readonly string _aesKey;   // 32-byte key from config
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<ClientDbService> _log;

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented    = true,
        PropertyNameCaseInsensitive = true,
    };

    public ClientDbService(IConfiguration cfg, ILogger<ClientDbService> log)
    {
        _log = log;
        var root = cfg["SharedData:Root"] ?? @"C:\Vertex\Engines\client_001";
        var dir  = Path.GetDirectoryName(root) ?? root;
        _filePath = Path.Combine(dir, "clients.json");
        _aesKey   = cfg["Auth:AesKey"] ?? "Vertex_AES_Key_Change_This_32__";
        if (_aesKey.Length < 32) _aesKey = _aesKey.PadRight(32, '_');
        if (_aesKey.Length > 32) _aesKey = _aesKey[..32];
        EnsureFileExists();
    }

    // ── Read all ─────────────────────────────────────────────
    public async Task<List<ClientRecord>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<List<ClientRecord>>(json, _json)
                   ?? new List<ClientRecord>();
        }
        finally { _lock.Release(); }
    }

    public async Task<ClientRecord?> FindByEmailAsync(string email)
    {
        var all = await GetAllAsync();
        return all.FirstOrDefault(c =>
            string.Equals(c.Email, email.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ClientRecord?> FindByIdAsync(string id)
    {
        var all = await GetAllAsync();
        return all.FirstOrDefault(c => c.Id == id);
    }

    // ── Register ──────────────────────────────────────────────
    public async Task<(bool ok, string error, ClientRecord? client)>
        RegisterAsync(string email, string password, string displayName)
    {
        email = email.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return (false, "Неверный формат email.", null);
        if (password.Length < 6)
            return (false, "Пароль минимум 6 символов.", null);

        var all = await GetAllAsync();

        if (all.Any(c => string.Equals(c.Email, email, StringComparison.OrdinalIgnoreCase)))
            return (false, "Email уже зарегистрирован.", null);

        // Assign next client ID
        int nextNum = all.Count == 0 ? 1
            : all.Select(c => {
                var parts = c.Id.Split('_');
                return parts.Length >= 2 && int.TryParse(parts[^1], out int n) ? n : 0;
              }).Max() + 1;

        var client = new ClientRecord
        {
            Id          = $"client_{nextNum:D3}",
            Email       = email,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? email.Split('@')[0] : displayName,
            PasswordHash = BCryptHash(password),
            Plan        = "demo",
            CreatedAt   = DateTime.UtcNow,
            DemoBalance = 10_000m,
            IsActive    = true,
        };

        all.Add(client);
        await SaveAllAsync(all);

        // Create client data folder
        try { Directory.CreateDirectory(client.DataFolder); } catch { }

        _log.LogInformation("[AUTH] New client registered: {Id} {Email}", client.Id, client.Email);
        return (true, "", client);
    }

    // ── Login ─────────────────────────────────────────────────
    public async Task<(bool ok, string error, ClientRecord? client)>
        LoginAsync(string email, string password)
    {
        email = email.Trim().ToLowerInvariant();
        var client = await FindByEmailAsync(email);

        if (client == null)
            return (false, "Email не найден.", null);
        if (!client.IsActive)
            return (false, "Аккаунт заблокирован.", null);
        if (!BCryptVerify(password, client.PasswordHash))
            return (false, "Неверный пароль.", null);

        client.LastLoginAt = DateTime.UtcNow;
        await UpdateAsync(client);

        return (true, "", client);
    }

    // ── Update ────────────────────────────────────────────────
    public async Task UpdateAsync(ClientRecord client)
    {
        var all = await GetAllAsync();
        var idx = all.FindIndex(c => c.Id == client.Id);
        if (idx >= 0) all[idx] = client;
        else all.Add(client);
        await SaveAllAsync(all);
    }

    // ── Binance keys ──────────────────────────────────────────
    public async Task SaveBinanceKeysAsync(string clientId, string apiKey, string secret)
    {
        var client = await FindByIdAsync(clientId);
        if (client == null) return;
        client.BinanceApiKeyEnc    = AesEncrypt(apiKey);
        client.BinanceApiSecretEnc = AesEncrypt(secret);
        client.IsLiveEnabled       = !string.IsNullOrEmpty(apiKey);
        await UpdateAsync(client);
    }

    public async Task<(string key, string secret)> GetBinanceKeysAsync(string clientId)
    {
        var client = await FindByIdAsync(clientId);
        if (client == null) return ("", "");
        var key    = string.IsNullOrEmpty(client.BinanceApiKeyEnc)    ? "" : AesDecrypt(client.BinanceApiKeyEnc);
        var secret = string.IsNullOrEmpty(client.BinanceApiSecretEnc) ? "" : AesDecrypt(client.BinanceApiSecretEnc);
        return (key, secret);
    }

    public async Task UpdateDemoBalanceAsync(string clientId, decimal balance)
    {
        var client = await FindByIdAsync(clientId);
        if (client == null) return;
        client.DemoBalance = balance;
        await UpdateAsync(client);
    }

    // ── Helpers ───────────────────────────────────────────────
    private void EnsureFileExists()
    {
        if (!File.Exists(_filePath))
            File.WriteAllText(_filePath, "[]");
    }

    private async Task SaveAllAsync(List<ClientRecord> clients)
    {
        await _lock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(clients, _json);
            await File.WriteAllTextAsync(_filePath, json);
        }
        finally { _lock.Release(); }
    }

    // BCrypt-like using PBKDF2 (no external NuGet needed)
    private static string BCryptHash(string password)
    {
        var salt  = RandomNumberGenerator.GetBytes(16);
        var hash  = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt,
            100_000, HashAlgorithmName.SHA256, 32);
        return $"$vx1${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool BCryptVerify(string password, string stored)
    {
        try
        {
            var parts = stored.Split('$');
            if (parts.Length < 4) return false;
            var salt  = Convert.FromBase64String(parts[2]);
            var hash  = Convert.FromBase64String(parts[3]);
            var check = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt,
                100_000, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(hash, check);
        }
        catch { return false; }
    }

    private string AesEncrypt(string plain)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(_aesKey);
        aes.GenerateIV();
        using var enc    = aes.CreateEncryptor();
        var plainBytes   = Encoding.UTF8.GetBytes(plain);
        var cipherBytes  = enc.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        var result       = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);
        return Convert.ToBase64String(result);
    }

    private string AesDecrypt(string cipherB64)
    {
        var data = Convert.FromBase64String(cipherB64);
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(_aesKey);
        var iv    = data[..16];
        var cipher = data[16..];
        aes.IV = iv;
        using var dec = aes.CreateDecryptor();
        var plain = dec.TransformFinalBlock(cipher, 0, cipher.Length);
        return Encoding.UTF8.GetString(plain);
    }
}
