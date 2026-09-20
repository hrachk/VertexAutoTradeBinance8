using Microsoft.Data.Sqlite;

namespace VertexAutoTradeBinance8.Web.Data;

/// <summary>
/// Shared SQLite system DB (users, sessions, settings, demo accounts). WAL mode.
/// Path: {EnginesRoot}/vertex_system.db
/// </summary>
public sealed class SystemDb
{
    private readonly string _dbPath;
    private readonly ILogger<SystemDb> _log;
    private readonly SemaphoreSlim _init = new(1, 1);
    private bool _ready;

    public string DbPath => _dbPath;

    public SystemDb(IConfiguration cfg, ILogger<SystemDb> log)
    {
        _log = log;
        var root = cfg["SharedData:EnginesRoot"];
        if (string.IsNullOrWhiteSpace(root))
        {
            var shared = cfg["SharedData:Root"] ?? Path.Combine(AppContext.BaseDirectory, "vertex-data");
            root = Path.GetDirectoryName(shared.TrimEnd('\\', '/')) ?? shared;
        }
        try { Directory.CreateDirectory(root); } catch { }
        _dbPath = Path.Combine(root, "vertex_system.db");
    }

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_ready) return;
        await _init.WaitAsync(ct);
        try
        {
            if (_ready) return;
            await using var conn = OpenConnection();
            await conn.OpenAsync(ct);
            await ExecAsync(conn, "PRAGMA journal_mode=WAL;", ct);
            await ExecAsync(conn, "PRAGMA busy_timeout=5000;", ct);
            await ExecAsync(conn, "PRAGMA synchronous=NORMAL;", ct);
            await ExecAsync(conn, "PRAGMA foreign_keys=ON;", ct);
            await ExecAsync(conn, """
                CREATE TABLE IF NOT EXISTS Users (
                  Id TEXT PRIMARY KEY,
                  Email TEXT NOT NULL COLLATE NOCASE,
                  DisplayName TEXT NOT NULL DEFAULT '',
                  PasswordHash TEXT NOT NULL DEFAULT '',
                  Role TEXT NOT NULL DEFAULT 'user',
                  IsActive INTEGER NOT NULL DEFAULT 1,
                  EmailVerified INTEGER NOT NULL DEFAULT 0,
                  ParallelDemoEnabled INTEGER NOT NULL DEFAULT 0,
                  CreatedAt TEXT NOT NULL,
                  UpdatedAt TEXT NOT NULL,
                  UNIQUE(Email)
                );
                CREATE INDEX IF NOT EXISTS IX_Users_Email ON Users(Email);

                CREATE TABLE IF NOT EXISTS UserSessions (
                  Id TEXT PRIMARY KEY,
                  UserId TEXT NOT NULL,
                  RefreshTokenHash TEXT NOT NULL,
                  AccessJti TEXT,
                  ExpiresAt TEXT NOT NULL,
                  CreatedAt TEXT NOT NULL,
                  RevokedAt TEXT,
                  IsRevoked INTEGER NOT NULL DEFAULT 0,
                  IpAddress TEXT,
                  UserAgent TEXT,
                  FOREIGN KEY(UserId) REFERENCES Users(Id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_Sessions_User ON UserSessions(UserId);

                CREATE TABLE IF NOT EXISTS UserSettings (
                  UserId TEXT PRIMARY KEY,
                  ApiKeyEncrypted TEXT,
                  ApiSecretEncrypted TEXT,
                  RiskFrac REAL NOT NULL DEFAULT 0.0075,
                  HardCapUsd REAL NOT NULL DEFAULT 55.0,
                  EnableMlSkipGate INTEGER NOT NULL DEFAULT 0,
                  DefaultLeverage INTEGER NOT NULL DEFAULT 10,
                  MaxOpenPositions INTEGER NOT NULL DEFAULT 5,
                  ExtraJson TEXT,
                  UpdatedAt TEXT NOT NULL,
                  FOREIGN KEY(UserId) REFERENCES Users(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS DemoAccounts (
                  UserId TEXT PRIMARY KEY,
                  Balance TEXT NOT NULL DEFAULT '10000.00',
                  Equity TEXT NOT NULL DEFAULT '10000.00',
                  Currency TEXT NOT NULL DEFAULT 'USDT',
                  PositionsJson TEXT NOT NULL DEFAULT '[]',
                  UpdatedAt TEXT NOT NULL,
                  FOREIGN KEY(UserId) REFERENCES Users(Id) ON DELETE CASCADE
                );
                """, ct);
            _ready = true;
            _log.LogInformation("[SYSTEM-DB] Ready {path} (WAL)", _dbPath);
        }
        finally { _init.Release(); }
    }

    public SqliteConnection OpenConnection()
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        return new SqliteConnection(cs);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Full user row + default settings + demo $10k (register / sync).</summary>
    public async Task UpsertFullUserAsync(
        string id,
        string email,
        string displayName,
        string passwordHash,
        bool isActive,
        bool emailVerified,
        bool parallelDemo,
        string? apiKeyEnc = null,
        string? apiSecretEnc = null,
        decimal? demoBalance = null,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var now = DateTime.UtcNow.ToString("o");
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO Users (Id, Email, DisplayName, PasswordHash, Role, IsActive, EmailVerified, ParallelDemoEnabled, CreatedAt, UpdatedAt)
                    VALUES ($id, $email, $name, $hash, 'user', $active, $ver, $demo, $now, $now)
                    ON CONFLICT(Id) DO UPDATE SET
                      Email=excluded.Email,
                      DisplayName=excluded.DisplayName,
                      PasswordHash=CASE WHEN excluded.PasswordHash='' THEN Users.PasswordHash ELSE excluded.PasswordHash END,
                      IsActive=excluded.IsActive,
                      EmailVerified=excluded.EmailVerified,
                      ParallelDemoEnabled=excluded.ParallelDemoEnabled,
                      UpdatedAt=excluded.UpdatedAt
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$email", email ?? "");
                cmd.Parameters.AddWithValue("$name", displayName ?? "");
                cmd.Parameters.AddWithValue("$hash", passwordHash ?? "");
                cmd.Parameters.AddWithValue("$active", isActive ? 1 : 0);
                cmd.Parameters.AddWithValue("$ver", emailVerified ? 1 : 0);
                cmd.Parameters.AddWithValue("$demo", parallelDemo ? 1 : 0);
                cmd.Parameters.AddWithValue("$now", now);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO UserSettings (UserId, ApiKeyEncrypted, ApiSecretEncrypted, RiskFrac, HardCapUsd, EnableMlSkipGate, DefaultLeverage, MaxOpenPositions, UpdatedAt)
                    VALUES ($id, $key, $sec, 0.0075, 55, 0, 10, 5, $now)
                    ON CONFLICT(UserId) DO UPDATE SET
                      ApiKeyEncrypted=COALESCE(excluded.ApiKeyEncrypted, UserSettings.ApiKeyEncrypted),
                      ApiSecretEncrypted=COALESCE(excluded.ApiSecretEncrypted, UserSettings.ApiSecretEncrypted),
                      UpdatedAt=excluded.UpdatedAt
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$key", (object?)apiKeyEnc ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sec", (object?)apiSecretEnc ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", now);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var bal = (demoBalance ?? 10_000m).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO DemoAccounts (UserId, Balance, Equity, Currency, PositionsJson, UpdatedAt)
                    VALUES ($id, $bal, $bal, 'USDT', '[]', $now)
                    ON CONFLICT(UserId) DO UPDATE SET UpdatedAt=excluded.UpdatedAt
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$bal", bal);
                cmd.Parameters.AddWithValue("$now", now);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            _log.LogInformation("[SYSTEM-DB] Upsert user {id} {email} parallelDemo={pd}", id, email, parallelDemo);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task SetParallelDemoAsync(string userId, bool enabled, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Users SET ParallelDemoEnabled=$v, UpdatedAt=$ts WHERE Id=$id";
        cmd.Parameters.AddWithValue("$v", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", userId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetApiKeysEncryptedAsync(string userId, string? keyEnc, string? secretEnc, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO UserSettings (UserId, ApiKeyEncrypted, ApiSecretEncrypted, RiskFrac, HardCapUsd, EnableMlSkipGate, DefaultLeverage, MaxOpenPositions, UpdatedAt)
            VALUES ($id, $key, $sec, 0.0075, 55, 0, 10, 5, $ts)
            ON CONFLICT(UserId) DO UPDATE SET
              ApiKeyEncrypted=excluded.ApiKeyEncrypted,
              ApiSecretEncrypted=excluded.ApiSecretEncrypted,
              UpdatedAt=excluded.UpdatedAt
            """;
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.Parameters.AddWithValue("$key", (object?)keyEnc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sec", (object?)secretEnc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertDemoBalanceAsync(string userId, decimal balance, decimal equity, string positionsJson, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);
        // Ensure parent user exists (FK) — minimal stub if missing
        await using (var ensure = conn.CreateCommand())
        {
            ensure.CommandText = """
                INSERT OR IGNORE INTO Users (Id, Email, DisplayName, PasswordHash, Role, IsActive, EmailVerified, ParallelDemoEnabled, CreatedAt, UpdatedAt)
                VALUES ($id, $email, '', '', 'user', 1, 0, 1, $ts, $ts)
                """;
            ensure.Parameters.AddWithValue("$id", userId);
            ensure.Parameters.AddWithValue("$email", userId + "@local.demo");
            ensure.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            await ensure.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DemoAccounts (UserId, Balance, Equity, Currency, PositionsJson, UpdatedAt)
            VALUES ($id, $bal, $eq, 'USDT', $pos, $ts)
            ON CONFLICT(UserId) DO UPDATE SET
              Balance = excluded.Balance,
              Equity = excluded.Equity,
              PositionsJson = excluded.PositionsJson,
              UpdatedAt = excluded.UpdatedAt
            """;
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.Parameters.AddWithValue("$bal", balance.ToString(System.Globalization.CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$eq", equity.ToString(System.Globalization.CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$pos", positionsJson ?? "[]");
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<decimal> GetMaxParallelDemoBalanceAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Balance FROM DemoAccounts d
            INNER JOIN Users u ON u.Id = d.UserId
            WHERE u.IsActive = 1 AND u.ParallelDemoEnabled = 1
            """;
        decimal best = 0m;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            if (decimal.TryParse(r.GetString(0), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var b) && b > best)
                best = b;
        }
        return best;
    }

    public async Task<int> CountUsersAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Users";
        var o = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(o);
    }
}
