using System.Text.Json;
using Microsoft.Data.Sqlite;
using VertexAutoTradeBinance8.Web.Services.Auth;

namespace VertexAutoTradeBinance8.Web.Data;

/// <summary>One-shot: clients.json + demo-account.json → vertex_system.db</summary>
public sealed class SystemDbMigrator
{
    private readonly SystemDb _db;
    private readonly IConfiguration _cfg;
    private readonly ILogger<SystemDbMigrator> _log;

    public SystemDbMigrator(SystemDb db, IConfiguration cfg, ILogger<SystemDbMigrator> log)
    {
        _db = db;
        _cfg = cfg;
        _log = log;
    }

    public async Task MigrateIfNeededAsync(CancellationToken ct = default)
    {
        await _db.EnsureInitializedAsync(ct);

        var root = _cfg["SharedData:Root"] ?? "";
        var engines = _cfg["SharedData:EnginesRoot"];
        if (string.IsNullOrWhiteSpace(engines))
            engines = string.IsNullOrEmpty(root) ? null : Path.GetDirectoryName(root.TrimEnd('\\', '/'));
        engines ??= Path.GetDirectoryName(_db.DbPath) ?? ".";

        var clientsPath = Path.Combine(engines, "clients.json");
        var flag = Path.Combine(engines, "vertex_system.migrated");
        if (File.Exists(flag))
        {
            _log.LogDebug("[SYSTEM-DB] Migration already done ({flag})", flag);
            return;
        }

        int users = 0, demos = 0;
        if (File.Exists(clientsPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(clientsPath, ct);
                var list = JsonSerializer.Deserialize<List<ClientRecord>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                           ?? new();
                await using var conn = _db.OpenConnection();
                await conn.OpenAsync(ct);
                foreach (var c in list)
                {
                    if (string.IsNullOrWhiteSpace(c.Id)) continue;
                    await UpsertUserAsync(conn, c, ct);
                    await UpsertSettingsAsync(conn, c, ct);
                    users++;
                }
                _log.LogInformation("[SYSTEM-DB] Migrated {n} users from clients.json", users);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[SYSTEM-DB] clients.json migrate failed");
            }
        }

        // demo-account.json under engines/client_*
        try
        {
            if (Directory.Exists(engines))
            {
                foreach (var path in Directory.GetFiles(engines, "demo-account.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(path) ?? "";
                        var folder = Path.GetFileName(dir);
                        if (!folder.StartsWith("client_", StringComparison.OrdinalIgnoreCase)) continue;
                        var userId = folder["client_".Length..];
                        if (string.IsNullOrWhiteSpace(userId)) continue;
                        var json = await File.ReadAllTextAsync(path, ct);
                        using var doc = JsonDocument.Parse(json);
                        var el = doc.RootElement;
                        decimal bal = 10000m, eq = 10000m;
                        if (el.TryGetProperty("Balance", out var b) && b.TryGetDecimal(out var bv)) bal = bv;
                        if (el.TryGetProperty("Equity", out var e) && e.TryGetDecimal(out var ev)) eq = ev;
                        else eq = bal;
                        var pos = el.TryGetProperty("Positions", out var p) ? p.GetRawText() : "[]";
                        await _db.UpsertDemoBalanceAsync(userId, bal, eq, pos, ct);
                        demos++;
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "[SYSTEM-DB] skip demo file {p}", path);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[SYSTEM-DB] demo scan failed");
        }

        try
        {
            await File.WriteAllTextAsync(flag, DateTime.UtcNow.ToString("o"), ct);
        }
        catch { }

        _log.LogInformation("[SYSTEM-DB] Migration complete users={u} demos={d}", users, demos);
    }

    private static async Task UpsertUserAsync(SqliteConnection conn, ClientRecord c, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Users (Id, Email, DisplayName, PasswordHash, Role, IsActive, EmailVerified, ParallelDemoEnabled, CreatedAt, UpdatedAt)
            VALUES ($id, $email, $name, $hash, 'user', $active, $ver, $demo, $ca, $ua)
            ON CONFLICT(Id) DO UPDATE SET
              Email=excluded.Email, DisplayName=excluded.DisplayName, PasswordHash=excluded.PasswordHash,
              IsActive=excluded.IsActive, ParallelDemoEnabled=excluded.ParallelDemoEnabled, UpdatedAt=excluded.UpdatedAt
            """;
        var now = DateTime.UtcNow.ToString("o");
        cmd.Parameters.AddWithValue("$id", c.Id);
        cmd.Parameters.AddWithValue("$email", c.Email ?? "");
        cmd.Parameters.AddWithValue("$name", c.DisplayName ?? "");
        cmd.Parameters.AddWithValue("$hash", c.PasswordHash ?? "");
        cmd.Parameters.AddWithValue("$active", c.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$ver", c.IsEmailVerified ? 1 : 0);
        cmd.Parameters.AddWithValue("$demo", c.ParallelDemoEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$ca", c.CreatedAt == default ? now : c.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$ua", now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpsertSettingsAsync(SqliteConnection conn, ClientRecord c, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO UserSettings (UserId, ApiKeyEncrypted, ApiSecretEncrypted, RiskFrac, HardCapUsd, EnableMlSkipGate, DefaultLeverage, MaxOpenPositions, UpdatedAt)
            VALUES ($id, $key, $sec, 0.0075, 55, 0, 10, 5, $ts)
            ON CONFLICT(UserId) DO UPDATE SET
              ApiKeyEncrypted=excluded.ApiKeyEncrypted, ApiSecretEncrypted=excluded.ApiSecretEncrypted, UpdatedAt=excluded.UpdatedAt
            """;
        cmd.Parameters.AddWithValue("$id", c.Id);
        cmd.Parameters.AddWithValue("$key", (object?)c.BinanceApiKeyEnc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sec", (object?)c.BinanceApiSecretEnc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
