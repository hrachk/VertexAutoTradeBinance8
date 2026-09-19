using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Services.Learning;

namespace VertexAutoTradeBinance8.Services.Infra;

/// <summary>
/// Production journal store (SQLite). WAL mode, shared cache, explicit TRACE logs.
/// </summary>
public sealed class SqliteJournalStore
{
    private readonly string _dbPath;
    private readonly string _sharedRoot;
    private readonly ILogger<SqliteJournalStore> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteJournalStore(IConfiguration cfg, ILogger<SqliteJournalStore> log)
    {
        _sharedRoot = cfg["SharedData:Root"] ?? @"C:\Vertex\Engines\client_001";
        Directory.CreateDirectory(_sharedRoot);
        _dbPath = Path.Combine(_sharedRoot, "vertex_journal.db");
        _log = log;
        EnsureSchema();
        RunIntegrityCheck();
        MaybeRotateBackup();
        TryMigrateJsonOnce();
        _log.LogInformation("[SQLITE-TRACE] store ready path={p} size={mb:F3}MB", _dbPath, DbSizeMb);
    }

    public string DbPath => _dbPath;

    public double DbSizeMb
    {
        get
        {
            try
            {
                if (!File.Exists(_dbPath)) return 0;
                return new FileInfo(_dbPath).Length / (1024.0 * 1024.0);
            }
            catch { return 0; }
        }
    }

    public string IntegrityStatus { get; private set; } = "unknown";

    private SqliteConnection Open()
    {
        // Mode=ReadWriteCreate + Cache=Shared so Engine + Web can open concurrently (WAL).
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        return c;
    }

    private void EnsureSchema()
    {
        using var c = Open();
        using (var pragma = c.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Trades (
  Id TEXT PRIMARY KEY,
  ClientId TEXT,
  Source TEXT,
  Symbol TEXT NOT NULL,
  Side TEXT,
  EntryPrice REAL,
  ExitPrice REAL,
  Qty REAL,
  Leverage INTEGER,
  RealizedPnL REAL,
  RealizedR REAL,
  InitialRiskPrice REAL,
  ExitReason TEXT,
  StrategyName TEXT,
  EntryTime TEXT,
  ExitTime TEXT
);
CREATE INDEX IF NOT EXISTS IX_Trades_Symbol ON Trades(Symbol);
CREATE INDEX IF NOT EXISTS IX_Trades_ExitTime ON Trades(ExitTime);

CREATE TABLE IF NOT EXISTS SymbolMemory (
  Symbol TEXT PRIMARY KEY,
  SizeMult REAL,
  SoftSkip INTEGER,
  AvgR REAL,
  WinRate REAL,
  Note TEXT,
  LastUpdated TEXT
);

CREATE TABLE IF NOT EXISTS SignalLogs (
  Id TEXT PRIMARY KEY,
  Symbol TEXT,
  SignalType TEXT,
  Action TEXT,
  BlockReason TEXT,
  Timestamp TEXT
);
";
        cmd.ExecuteNonQuery();
        _log.LogInformation("[SQLITE-TRACE] schema ensured (WAL) path={p}", _dbPath);
    }

    private void RunIntegrityCheck()
    {
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            var result = cmd.ExecuteScalar()?.ToString() ?? "fail";
            IntegrityStatus = string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase) ? "ok" : result;
            if (IntegrityStatus == "ok")
                _log.LogInformation("[SQLITE-JOURNAL] PRAGMA quick_check=ok path={p}", _dbPath);
            else
                _log.LogError("[SQLITE-JOURNAL] PRAGMA quick_check FAILED: {r} path={p}", IntegrityStatus, _dbPath);
        }
        catch (Exception ex)
        {
            IntegrityStatus = "error:" + ex.Message;
            _log.LogWarning(ex, "[SQLITE-JOURNAL] integrity check failed");
        }
    }

    private void MaybeRotateBackup()
    {
        try
        {
            if (!File.Exists(_dbPath)) return;
            var len = new FileInfo(_dbPath).Length;
            const long threshold = 32L * 1024 * 1024;
            if (len < threshold) return;
            var bak = Path.Combine(_sharedRoot, $"vertex_journal_{DateTime.UtcNow:yyyyMMdd_HHmmss}.db.bak");
            File.Copy(_dbPath, bak, overwrite: true);
            _log.LogWarning("[SQLITE-JOURNAL] size={mb:F1}MB ≥32MB → backup {bak}", len / (1024.0 * 1024.0), bak);
            foreach (var f in Directory.GetFiles(_sharedRoot, "vertex_journal_*.db.bak").OrderByDescending(x => x).Skip(5))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[SQLITE-JOURNAL] backup skipped");
        }
    }

    private void TryMigrateJsonOnce()
    {
        try
        {
            // Migrate both root and client_* journals
            var candidates = new List<string>();
            var rootJson = Path.Combine(_sharedRoot, "trade-journal.json");
            if (File.Exists(rootJson)) candidates.Add(rootJson);
            if (Directory.Exists(_sharedRoot))
            {
                foreach (var d in Directory.GetDirectories(_sharedRoot, "client_*"))
                {
                    var p = Path.Combine(d, "trade-journal.json");
                    if (File.Exists(p)) candidates.Add(p);
                }
            }

            int total = 0;
            foreach (var jsonPath in candidates)
            {
                var bak = jsonPath + ".bak";
                if (File.Exists(bak)) continue;
                try
                {
                    var text = File.ReadAllText(jsonPath);
                    var file = JsonSerializer.Deserialize<TradeJournalFile>(text) ?? new TradeJournalFile();
                    foreach (var e in file.Entries)
                    {
                        InsertTrade(e);
                        total++;
                    }
                    File.Move(jsonPath, bak, overwrite: true);
                    _log.LogWarning("[SQLITE-TRACE] migrated {n} trades from {json}", file.Entries.Count, jsonPath);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[SQLITE-TRACE] migrate failed for {p}", jsonPath);
                }
            }
            if (total > 0)
                _log.LogWarning("[SQLITE-JOURNAL] migrated total {n} trades → {db}", total, _dbPath);

            // Migrate symbol-memory.json → SymbolMemory table
            int memN = 0;
            var memCandidates = new List<string>();
            var rootMem = Path.Combine(_sharedRoot, "symbol-memory.json");
            if (File.Exists(rootMem)) memCandidates.Add(rootMem);
            if (Directory.Exists(_sharedRoot))
            {
                foreach (var d in Directory.GetDirectories(_sharedRoot, "client_*"))
                {
                    var mp = Path.Combine(d, "symbol-memory.json");
                    if (File.Exists(mp)) memCandidates.Add(mp);
                }
            }
            foreach (var mp in memCandidates)
            {
                try
                {
                    var text = File.ReadAllText(mp);
                    var mem = JsonSerializer.Deserialize<SymbolMemoryFile>(text);
                    if (mem?.BySymbol == null) continue;
                    foreach (var kv in mem.BySymbol)
                    {
                        var a = kv.Value;
                        UpsertSymbolMemory(
                            kv.Key,
                            a.SizeMult <= 0 ? 1m : a.SizeMult,
                            a.SoftSkip,
                            0m,
                            a.RecentTrades > 0 ? (decimal)a.RecentWins / a.RecentTrades : 0m,
                            a.Note ?? "");
                        memN++;
                    }
                    _log.LogInformation("[SQLITE-TRACE] migrated SymbolMemory {n} from {p}", mem.BySymbol.Count, mp);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[SQLITE-TRACE] symbol-memory migrate failed {p}", mp);
                }
            }
            if (memN > 0)
                _log.LogWarning("[SQLITE-JOURNAL] migrated {n} SymbolMemory rows → {db}", memN, _dbPath);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[SQLITE-JOURNAL] migrate skipped");
        }
    }

    public void InsertTrade(TradeJournalEntry e)
    {
        if (e == null) return;
        var id = string.IsNullOrWhiteSpace(e.Id) ? Guid.NewGuid().ToString("N") : e.Id;
        _log.LogInformation(
            "[SQLITE-TRACE] Attempting to write trade {id} {src} {sym} {side} pnl={pnl} → {db}",
            id, e.Source, e.Symbol, e.Side, e.RealizedPnl, _dbPath);

        _gate.Wait();
        try
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT OR REPLACE INTO Trades
(Id,ClientId,Source,Symbol,Side,EntryPrice,ExitPrice,Qty,Leverage,RealizedPnL,RealizedR,InitialRiskPrice,ExitReason,StrategyName,EntryTime,ExitTime)
VALUES ($id,$cid,$src,$sym,$side,$ep,$xp,$qty,$lev,$pnl,$r,$risk,$reason,$strat,$ot,$ct)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$cid", e.ClientId ?? "");
            cmd.Parameters.AddWithValue("$src", e.Source ?? "");
            cmd.Parameters.AddWithValue("$sym", e.Symbol ?? "");
            cmd.Parameters.AddWithValue("$side", e.Side ?? "");
            cmd.Parameters.AddWithValue("$ep", (double)e.EntryPrice);
            cmd.Parameters.AddWithValue("$xp", (double)e.ExitPrice);
            cmd.Parameters.AddWithValue("$qty", (double)e.Qty);
            cmd.Parameters.AddWithValue("$lev", e.Leverage);
            cmd.Parameters.AddWithValue("$pnl", (double)e.RealizedPnl);
            cmd.Parameters.AddWithValue("$r", (double)e.RealizedR);
            cmd.Parameters.AddWithValue("$risk", (double)e.InitialRiskPrice);
            cmd.Parameters.AddWithValue("$reason", e.CloseReason ?? "");
            cmd.Parameters.AddWithValue("$strat", "");
            cmd.Parameters.AddWithValue("$ot", e.OpenedAtUtc == default ? DateTime.UtcNow.ToString("o") : e.OpenedAtUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$ct", e.ClosedAtUtc == default ? DateTime.UtcNow.ToString("o") : e.ClosedAtUtc.ToString("o"));
            var rows = cmd.ExecuteNonQuery();
            tx.Commit();

            // Checkpoint so size grows visibly even under WAL
            try
            {
                using var cp = c.CreateCommand();
                cp.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cp.ExecuteNonQuery();
            }
            catch { /* soft */ }

            long count = 0;
            try
            {
                using var q = c.CreateCommand();
                q.CommandText = "SELECT COUNT(*) FROM Trades;";
                count = Convert.ToInt64(q.ExecuteScalar());
            }
            catch { }

            _log.LogInformation(
                "[SQLITE-TRACE] Successfully inserted row id={id} rowsAffected={rows} tradesTotal={n} size={mb:F3}MB",
                id, rows, count, DbSizeMb);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[SQLITE-TRACE] ERROR insert trade {id} {sym}: {msg}", id, e.Symbol, ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<TradeJournalEntry> GetRecent(int take = 200)
    {
        var list = new List<TradeJournalEntry>();
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT * FROM Trades ORDER BY ExitTime DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", Math.Clamp(take, 1, 5000));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new TradeJournalEntry
                {
                    Id = r["Id"]?.ToString() ?? "",
                    ClientId = r["ClientId"]?.ToString() ?? "",
                    Source = r["Source"]?.ToString() ?? "",
                    Symbol = r["Symbol"]?.ToString() ?? "",
                    Side = r["Side"]?.ToString() ?? "",
                    EntryPrice = Convert.ToDecimal(r["EntryPrice"], CultureInfo.InvariantCulture),
                    ExitPrice = Convert.ToDecimal(r["ExitPrice"], CultureInfo.InvariantCulture),
                    Qty = Convert.ToDecimal(r["Qty"], CultureInfo.InvariantCulture),
                    Leverage = Convert.ToInt32(r["Leverage"]),
                    RealizedPnl = Convert.ToDecimal(r["RealizedPnL"], CultureInfo.InvariantCulture),
                    RealizedR = Convert.ToDecimal(r["RealizedR"], CultureInfo.InvariantCulture),
                    InitialRiskPrice = Convert.ToDecimal(r["InitialRiskPrice"], CultureInfo.InvariantCulture),
                    CloseReason = r["ExitReason"]?.ToString() ?? "",
                    OpenedAtUtc = DateTime.TryParse(r["EntryTime"]?.ToString(), out var ot) ? ot : default,
                    ClosedAtUtc = DateTime.TryParse(r["ExitTime"]?.ToString(), out var ct) ? ct : default
                });
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[SQLITE-TRACE] GetRecent failed");
        }
        return list;
    }

    public void UpsertSymbolMemory(string symbol, decimal sizeMult, bool softSkip, decimal avgR, decimal winRate, string note)
    {
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
INSERT INTO SymbolMemory(Symbol,SizeMult,SoftSkip,AvgR,WinRate,Note,LastUpdated)
VALUES ($s,$sm,$sk,$ar,$wr,$n,$u)
ON CONFLICT(Symbol) DO UPDATE SET
 SizeMult=$sm, SoftSkip=$sk, AvgR=$ar, WinRate=$wr, Note=$n, LastUpdated=$u";
            cmd.Parameters.AddWithValue("$s", symbol);
            cmd.Parameters.AddWithValue("$sm", (double)sizeMult);
            cmd.Parameters.AddWithValue("$sk", softSkip ? 1 : 0);
            cmd.Parameters.AddWithValue("$ar", (double)avgR);
            cmd.Parameters.AddWithValue("$wr", (double)winRate);
            cmd.Parameters.AddWithValue("$n", note ?? "");
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
            _log.LogInformation("[SQLITE-TRACE] SymbolMemory upsert {s} size={sm} skip={sk}", symbol, sizeMult, softSkip);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[SQLITE-TRACE] SymbolMemory upsert failed {s}", symbol);
        }
    }

    public void LogSignal(string symbol, string type, string action, string? blockReason)
    {
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO SignalLogs(Id,Symbol,SignalType,Action,BlockReason,Timestamp) VALUES ($i,$s,$t,$a,$b,$ts)";
            cmd.Parameters.AddWithValue("$i", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$s", symbol ?? "");
            cmd.Parameters.AddWithValue("$t", type ?? "");
            cmd.Parameters.AddWithValue("$a", action ?? "");
            cmd.Parameters.AddWithValue("$b", blockReason ?? "");
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
            _log.LogInformation("[SQLITE-TRACE] SignalLog {s} type={t} action={a}", symbol, type, action);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[SQLITE-TRACE] SignalLog failed {s}", symbol);
        }
    }
}
