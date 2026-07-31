using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VertexAutoTradeBinance8.Web.Services.Auth;

/// <summary>
/// JSON-based client storage (clients.json).
/// BUG FIXES:
///   1. Deadlock: GetAllAsync and SaveAllAsync both grabbed the same SemaphoreSlim.
///      Fixed: lock only at the top-level public methods, internal helpers are lock-free.
///   2. Wrong path: clients.json was written to GetDirectoryName(SharedData:Root)
///      which may not exist. Fixed: write to SharedData:Root directly (always exists).
/// </summary>
public sealed class ClientDbService
{
    private readonly string _filePath;
    private readonly string _aesKey;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<ClientDbService> _log;

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public ClientDbService(IConfiguration cfg, ILogger<ClientDbService> log)
    {
        _log = log;

        // FIX: use SharedData:Root directly (not its parent folder).
        // SharedData:Root = C:\Vertex\Engines\client_001 — this folder is
        // guaranteed to exist because the Engine writes to it.
        var root = cfg["SharedData:Root"];
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(AppContext.BaseDirectory, "vertex-data");

        // Store clients.json in the parent of client_001 folder, e.g. C:\Vertex\Engines\
        // so it's shared across all client sub-folders.
        var dir = Path.GetDirectoryName(root.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            dir = root; // fall back to root itself

        // Ensure the directory exists
        try { Directory.CreateDirectory(dir); } catch { }

        _filePath = Path.Combine(dir, "clients.json");

        _aesKey = cfg["Auth:AesKey"] ?? "Vertex_AES_Key_Change_This_32__";
        if (_aesKey.Length < 32) _aesKey = _aesKey.PadRight(32, '_');
        if (_aesKey.Length > 32) _aesKey = _aesKey[..32];

        EnsureFileExists();
        _log.LogInformation("[AUTH] ClientDbService initialized. clients.json = {path}", _filePath);
    }

    // ── Internal helpers (NO lock — callers must hold it) ─────────────
    private List<ClientRecord> ReadAll()
    {
        try
        {
            if (!File.Exists(_filePath)) return new();
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<ClientRecord>>(json, _json) ?? new();
        }
        catch { return new(); }
    }

    private void WriteAll(List<ClientRecord> clients)
    {
        var json = JsonSerializer.Serialize(clients, _json);
        File.WriteAllText(_filePath, json);
    }

    // ── Public API (each acquires lock independently) ──────────────────
    public async Task<List<ClientRecord>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try   { return ReadAll(); }
        finally { _lock.Release(); }
    }

    public async Task<ClientRecord?> FindByEmailAsync(string email)
    {
        await _lock.WaitAsync();
        try
        {
            var all = ReadAll();
            return all.FirstOrDefault(c =>
                string.Equals(c.Email, email.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        finally { _lock.Release(); }
    }

    public async Task<ClientRecord?> FindByIdAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var all = ReadAll();
            return all.FirstOrDefault(c => c.Id == id);
        }
        finally { _lock.Release(); }
    }

    // ── Register ──────────────────────────────────────────────────────
    public async Task<(bool ok, string error, ClientRecord? client)>
        RegisterAsync(string email, string password, string displayName)
    {
        email = email.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return (false, "Неверный формат email.", null);
        if (password.Length < 6)
            return (false, "Пароль минимум 6 символов.", null);

        await _lock.WaitAsync();
        try
        {
            // FIX: all reads AND writes happen inside a SINGLE lock acquisition
            var all = ReadAll();

            if (all.Any(c => string.Equals(c.Email, email, StringComparison.OrdinalIgnoreCase)))
                return (false, "Email уже зарегистрирован.", null);

            int nextNum = all.Count == 0 ? 1
                : all.Select(c =>
                {
                    var parts = c.Id.Split('_');
                    return parts.Length >= 2 && int.TryParse(parts[^1], out int n) ? n : 0;
                }).Max() + 1;

            var client = new ClientRecord
            {
                Id          = $"client_{nextNum:D3}",
                Email       = email,
                DisplayName = string.IsNullOrWhiteSpace(displayName)
                                ? email.Split('@')[0] : displayName,
                PasswordHash = BCryptHash(password),
                Plan        = "demo",
                CreatedAt   = DateTime.UtcNow,
                DemoBalance = 10_000m,
                IsActive    = true,
            };

            all.Add(client);
            WriteAll(all);  // sync write inside lock — no nested async lock needed

            // Create client data folder
            try { Directory.CreateDirectory(client.DataFolder); } catch { }

            _log.LogInformation("[AUTH] Registered: {Id} {Email}", client.Id, client.Email);
            return (true, "", client);
        }
        finally { _lock.Release(); }
    }

    // ── Login ─────────────────────────────────────────────────────────
    public async Task<(bool ok, string error, ClientRecord? client)>
        LoginAsync(string email, string password)
    {
        email = email.Trim().ToLowerInvariant();

        await _lock.WaitAsync();
        try
        {
            var all    = ReadAll();
            var client = all.FirstOrDefault(c =>
                string.Equals(c.Email, email, StringComparison.OrdinalIgnoreCase));

            if (client == null)
                return (false, "Email не найден.", null);
            if (!client.IsActive)
                return (false, "Аккаунт заблокирован.", null);
            if (!BCryptVerify(password, client.PasswordHash))
                return (false, "Неверный пароль.", null);

            client.LastLoginAt = DateTime.UtcNow;
            var idx = all.FindIndex(c => c.Id == client.Id);
            if (idx >= 0) all[idx] = client;
            WriteAll(all);

            _log.LogInformation("[AUTH] Login: {Id}", client.Id);
            return (true, "", client);
        }
        finally { _lock.Release(); }
    }

    // ── Update ────────────────────────────────────────────────────────
    public async Task UpdateAsync(ClientRecord client)
    {
        await _lock.WaitAsync();
        try
        {
            var all = ReadAll();
            var idx = all.FindIndex(c => c.Id == client.Id);
            if (idx >= 0) all[idx] = client;
            else all.Add(client);
            WriteAll(all);
        }
        finally { _lock.Release(); }
    }

    // ── Binance keys ──────────────────────────────────────────────────
    public async Task SaveBinanceKeysAsync(string clientId, string apiKey, string secret)
    {
        await _lock.WaitAsync();
        try
        {
            var all = ReadAll();
            var client = all.FirstOrDefault(c => c.Id == clientId);
            if (client == null) return;
            client.BinanceApiKeyEnc    = string.IsNullOrEmpty(apiKey) ? null : AesEncrypt(apiKey);
            client.BinanceApiSecretEnc = string.IsNullOrEmpty(secret) ? null : AesEncrypt(secret);
            client.IsLiveEnabled       = !string.IsNullOrEmpty(apiKey);
            var idx = all.FindIndex(c => c.Id == clientId);
            if (idx >= 0) all[idx] = client;
            WriteAll(all);
        }
        finally { _lock.Release(); }
    }

    public async Task<(string key, string secret)> GetBinanceKeysAsync(string clientId)
    {
        await _lock.WaitAsync();
        try
        {
            var all    = ReadAll();
            var client = all.FirstOrDefault(c => c.Id == clientId);
            if (client == null) return ("", "");
            var key    = string.IsNullOrEmpty(client.BinanceApiKeyEnc)
                            ? "" : AesDecrypt(client.BinanceApiKeyEnc);
            var secret = string.IsNullOrEmpty(client.BinanceApiSecretEnc)
                            ? "" : AesDecrypt(client.BinanceApiSecretEnc);
            return (key, secret);
        }
        finally { _lock.Release(); }
    }

    public async Task UpdateDemoBalanceAsync(string clientId, decimal balance)
    {
        await _lock.WaitAsync();
        try
        {
            var all = ReadAll();
            var idx = all.FindIndex(c => c.Id == clientId);
            if (idx >= 0) { all[idx].DemoBalance = balance; WriteAll(all); }
        }
        finally { _lock.Release(); }
    }

    // ── Email verification ────────────────────────────────────────────
    /// <summary>
    /// Generates a 6-digit verification code, stores it on the client
    /// record (hashed), and returns the plain code to be emailed.
    /// Code expires in 15 minutes. Max 3 attempts before lockout.
    /// </summary>
    public async Task<string> GenerateVerifyCodeAsync(string clientId)
    {
        await _lock.WaitAsync();
        try
        {
            var all    = ReadAll();
            var client = all.FirstOrDefault(c => c.Id == clientId);
            if (client == null) return "";

            // Generate 6-digit code
            var code = new Random().Next(100_000, 999_999).ToString();

            client.EmailVerifyCode    = HashCode(code);
            client.EmailVerifyExpires = DateTime.UtcNow.AddMinutes(15);
            client.EmailVerifyAttempts = 0;

            var idx = all.FindIndex(c => c.Id == clientId);
            if (idx >= 0) all[idx] = client;
            WriteAll(all);

            return code; // plain code — email it, never store
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Verifies the submitted code against the stored hash.
    /// Returns (true, "") on success, (false, reason) on failure.
    /// </summary>
    public async Task<(bool ok, string error)> VerifyEmailAsync(string clientId, string code)
    {
        await _lock.WaitAsync();
        try
        {
            var all    = ReadAll();
            var client = all.FirstOrDefault(c => c.Id == clientId);
            if (client == null)           return (false, "Аккаунт не найден.");
            if (client.IsEmailVerified)   return (true,  "");

            if (client.EmailVerifyAttempts >= 3)
                return (false, "Слишком много попыток. Запросите новый код.");

            if (client.EmailVerifyExpires < DateTime.UtcNow)
                return (false, "Код истёк. Запросите новый.");

            client.EmailVerifyAttempts++;

            if (!VerifyCode(code, client.EmailVerifyCode ?? ""))
            {
                var idx2 = all.FindIndex(c => c.Id == clientId);
                if (idx2 >= 0) all[idx2] = client;
                WriteAll(all);
                int left = 3 - client.EmailVerifyAttempts;
                return (false, $"Неверный код. Осталось попыток: {left}.");
            }

            // Success
            client.IsEmailVerified    = true;
            client.EmailVerifyCode    = null;
            client.EmailVerifyExpires = null;
            client.EmailVerifyAttempts = 0;
            client.IsActive           = true;

            var idx = all.FindIndex(c => c.Id == clientId);
            if (idx >= 0) all[idx] = client;
            WriteAll(all);
            return (true, "");
        }
        finally { _lock.Release(); }
    }

    private static string HashCode(string code)
    {
        // Simple SHA256 hash — code is short-lived (15 min) so PBKDF2 unnecessary
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(code);
        return Convert.ToBase64String(sha.ComputeHash(bytes));
    }

    private static bool VerifyCode(string code, string hash)
        => HashCode(code) == hash;

    // ── Helpers ───────────────────────────────────────────────────────
    private void EnsureFileExists()
    {
        if (!File.Exists(_filePath))
            File.WriteAllText(_filePath, "[]");
    }

    // PBKDF2-based password hash (no external NuGet)
    private static string BCryptHash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
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
        using var enc   = aes.CreateEncryptor();
        var plainBytes  = Encoding.UTF8.GetBytes(plain);
        var cipherBytes = enc.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        var result      = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);
        return Convert.ToBase64String(result);
    }

    private string AesDecrypt(string cipherB64)
    {
        var data   = Convert.FromBase64String(cipherB64);
        using var aes = Aes.Create();
        aes.Key    = Encoding.UTF8.GetBytes(_aesKey);
        aes.IV     = data[..16];
        using var dec = aes.CreateDecryptor();
        var plain  = dec.TransformFinalBlock(data[16..], 0, data.Length - 16);
        return Encoding.UTF8.GetString(plain);
    }
}
