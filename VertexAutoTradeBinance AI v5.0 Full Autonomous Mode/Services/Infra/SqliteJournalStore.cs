using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Services.Learning;

namespace VertexAutoTradeBinance8.Services.Infra;

/// <summary>
/// Production journal store (SQLite). On first start migrates trade-journal.json → .bak.
/// JSON file writes can remain as dual-write for compatibility.
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
        TryMigrateJsonOnce();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={_dbPath}");
        c.Open();
        return c;
    }

    private void EnsureSchema()
    {
        using var c = Open();
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
    }

    private void TryMigrateJsonOnce()
    {
        try
        {
            var jsonPath = Path.Combine(_sharedRoot, "trade-journal.json");
            var bak = jsonPath + ".bak";
            if (!File.Exists(jsonPath) || File.Exists(bak)) return;

            lock (this)
            {
                if (!File.Exists(jsonPath) || File.Exists(bak)) return;
                var text = File.ReadAllText(jsonPath);
                var file = JsonSerializer.Deserialize<TradeJournalFile>(text) ?? new TradeJournalFile();
                int n = 0;
                foreach (var e in file.Entries)
                {
                    InsertTrade(e);
                    n++;
                }
                File.Move(jsonPath, bak, overwrite: true);
                _log.LogWarning("[SQLITE-JOURNAL] migrated {n} trades from JSON → {db}; JSON renamed .bak", n, _dbPath);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[SQLITE-JOURNAL] migrate skipped");
        }
    }

    public void InsertTrade(TradeJournalEntry e)
    {
        _gate.Wait();
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
INSERT OR REPLACE INTO Trades
(Id,ClientId,Source,Symbol,Side,EntryPrice,ExitPrice,Qty,Leverage,RealizedPnL,RealizedR,InitialRiskPrice,ExitReason,StrategyName,EntryTime,ExitTime)
VALUES ($id,$cid,$src,$sym,$side,$ep,$xp,$qty,$lev,$pnl,$r,$risk,$reason,$strat,$ot,$ct)";
            cmd.Parameters.AddWithValue("$id", e.Id ?? Guid.NewGuid().ToString("N"));
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
            cmd.Parameters.AddWithValue("$ot", e.OpenedAtUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$ct", e.ClosedAtUtc.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<TradeJournalEntry> GetRecent(int limit = 200)
    {
        var list = new List<TradeJournalEntry>();
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT * FROM Trades ORDER BY ExitTime DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
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
            _log.LogWarning(ex, "[SQLITE-JOURNAL] GetRecent failed");
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
        }
        catch { }
    }

    public void LogSignal(string symbol, string type, string action, string? blockReason)
    {
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO SignalLogs(Id,Symbol,SignalType,Action,BlockReason,Timestamp) VALUES ($i,$s,$t,$a,$b,$ts)";
            cmd.Parameters.AddWithValue("$i", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$s", symbol);
            cmd.Parameters.AddWithValue("$t", type);
            cmd.Parameters.AddWithValue("$a", action);
            cmd.Parameters.AddWithValue("$b", blockReason ?? "");
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        catch { }
    }
}
