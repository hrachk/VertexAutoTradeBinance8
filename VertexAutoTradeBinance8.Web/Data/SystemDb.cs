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

    public async Task UpsertDemoBalanceAsync(string userId, decimal balance, decimal equity, string positionsJson, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = OpenConnection();
        await conn.OpenAsync(ct);
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
}
