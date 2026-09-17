using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VertexAutoTradeBinance8.Web.Services.Twa;

public sealed class TwaSummaryDto
{
    public decimal Equity { get; set; }
    public decimal Balance { get; set; }
    public decimal DayPnl { get; set; }
    public decimal DayPnlPct { get; set; }
    public int OpenPositions { get; set; }
    public bool KillSwitch { get; set; }
    public bool BtcVolLock { get; set; }
    public string BtcVolReason { get; set; } = "";
    public bool DailyDdEmergency { get; set; }
    public string BinanceStatus { get; set; } = "Unknown";
    public string Mode { get; set; } = "Demo";
    public DateTime Utc { get; set; } = DateTime.UtcNow;
}

public sealed class TwaPositionDto
{
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "";
    public decimal Entry { get; set; }
    public decimal Mark { get; set; }
    public decimal Qty { get; set; }
    public int Leverage { get; set; }
    public decimal UnPnl { get; set; }
    public decimal? Sl { get; set; }
    public decimal? Tp { get; set; }
    public string Source { get; set; } = "Demo";
}

public sealed class TwaStatsDto
{
    public int TradesToday { get; set; }
    public int TradesWeek { get; set; }
    public decimal WinRate { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal TotalPnl { get; set; }
    public double SqliteMb { get; set; }
    public string SqliteIntegrity { get; set; } = "n/a";
}

public sealed class TwaDashboardService
{
    private readonly IConfiguration _cfg;
    private readonly DemoAccountService _demo;
    private readonly ILogger<TwaDashboardService> _log;
    private readonly BinanceClientFactory? _binance;

    public TwaDashboardService(
        IConfiguration cfg,
        DemoAccountService demo,
        ILogger<TwaDashboardService> log,
        BinanceClientFactory? binance = null)
    {
        _cfg = cfg;
        _demo = demo;
        _log = log;
        _binance = binance;
    }

    private string SharedRoot => _cfg["SharedData:Root"] ?? @"C:\Vertex\Engines\client_001";
    private string JournalDb => Path.Combine(SharedRoot, "vertex_journal.db");
    private string KillFlag => Path.Combine(SharedRoot, "emergency_kill.flag");

    public TwaSummaryDto GetSummary()
    {
        var dto = new TwaSummaryDto();
        try
        {
            dto.KillSwitch = File.Exists(KillFlag);
            // Demo equity
            // Read demo-account.json for primary client
            var demoPath = Path.Combine(SharedRoot, "client_001", "demo-account.json");
            if (!File.Exists(demoPath))
            {
                var dirs = Directory.Exists(SharedRoot)
                    ? Directory.GetDirectories(SharedRoot, "client_*")
                    : Array.Empty<string>();
                foreach (var d in dirs)
                {
                    var p = Path.Combine(d, "demo-account.json");
                    if (File.Exists(p)) { demoPath = p; break; }
                }
            }
            if (File.Exists(demoPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(demoPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("Balance", out var bal))
                    dto.Balance = bal.GetDecimal();
                decimal upnl = 0;
                int n = 0;
                if (root.TryGetProperty("Positions", out var pos) && pos.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in pos.EnumerateArray())
                    {
                        n++;
                        if (p.TryGetProperty("UnrealizedPnl", out var u)) upnl += u.GetDecimal();
                        else if (p.TryGetProperty("UnrealizedPnL", out var u2)) upnl += u2.GetDecimal();
                    }
                }
                dto.OpenPositions = n;
                dto.Equity = dto.Balance + upnl;
                // day pnl from history if present
                if (root.TryGetProperty("History", out var hist) && hist.ValueKind == JsonValueKind.Array)
                {
                    var today = DateTime.UtcNow.Date;
                    decimal day = 0;
                    foreach (var h in hist.EnumerateArray())
                    {
                        if (h.TryGetProperty("ClosedAtUtc", out var ct) &&
                            DateTime.TryParse(ct.GetString(), out var closed) &&
                            closed.ToUniversalTime().Date == today &&
                            h.TryGetProperty("RealizedPnl", out var rp))
                            day += rp.GetDecimal();
                    }
                    dto.DayPnl = day;
                    if (dto.Balance > 0)
                        dto.DayPnlPct = day / dto.Balance * 100m;
                }
            }

            // BTC vol lock file / reason from a simple marker if Engine wrote one
            var volFlag = Path.Combine(SharedRoot, "btc_vol_lock.flag");
            dto.BtcVolLock = File.Exists(volFlag);
            if (dto.BtcVolLock)
                try { dto.BtcVolReason = File.ReadAllText(volFlag).Trim(); } catch { dto.BtcVolReason = "BTC_VOLATILITY_LOCK"; }

            dto.BinanceStatus = dto.KillSwitch ? "Blocked (KILL)" : "Connected";
            dto.Mode = _demo.DemoMode ? "Demo" : "Live";
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[TWA] summary");
        }
        dto.Utc = DateTime.UtcNow;
        return dto;
    }

    public List<TwaPositionDto> GetPositions()
    {
        var list = new List<TwaPositionDto>();
        try
        {
            string? demoPath = null;
            if (Directory.Exists(SharedRoot))
            {
                foreach (var d in Directory.GetDirectories(SharedRoot, "client_*"))
                {
                    var p = Path.Combine(d, "demo-account.json");
                    if (File.Exists(p)) { demoPath = p; break; }
                }
            }
            if (demoPath == null) return list;
            using var doc = JsonDocument.Parse(File.ReadAllText(demoPath));
            if (!doc.RootElement.TryGetProperty("Positions", out var pos)) return list;
            foreach (var p in pos.EnumerateArray())
            {
                var dto = new TwaPositionDto { Source = "Demo" };
                if (p.TryGetProperty("Symbol", out var s)) dto.Symbol = s.GetString() ?? "";
                if (p.TryGetProperty("Side", out var side)) dto.Side = side.GetString() ?? "";
                if (p.TryGetProperty("EntryPrice", out var e)) dto.Entry = e.GetDecimal();
                if (p.TryGetProperty("MarkPrice", out var m)) dto.Mark = m.GetDecimal();
                else if (p.TryGetProperty("LastPrice", out var lp)) dto.Mark = lp.GetDecimal();
                if (p.TryGetProperty("Qty", out var q)) dto.Qty = q.GetDecimal();
                if (p.TryGetProperty("Leverage", out var lev)) dto.Leverage = lev.GetInt32();
                if (p.TryGetProperty("UnrealizedPnl", out var u)) dto.UnPnl = u.GetDecimal();
                if (p.TryGetProperty("StopLoss", out var sl) && sl.ValueKind != JsonValueKind.Null)
                    dto.Sl = sl.GetDecimal();
                if (p.TryGetProperty("TakeProfits", out var tps) && tps.ValueKind == JsonValueKind.Array && tps.GetArrayLength() > 0)
                    dto.Tp = tps[0].GetDecimal();
                list.Add(dto);
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "[TWA] positions"); }
        return list;
    }

    public TwaStatsDto GetStats()
    {
        var dto = new TwaStatsDto();
        try
        {
            if (File.Exists(JournalDb))
            {
                dto.SqliteMb = new FileInfo(JournalDb).Length / (1024.0 * 1024.0);
                using var c = new SqliteConnection($"Data Source={JournalDb}");
                c.Open();
                using (var chk = c.CreateCommand())
                {
                    chk.CommandText = "PRAGMA quick_check;";
                    dto.SqliteIntegrity = chk.ExecuteScalar()?.ToString() ?? "?";
                }
                var today = DateTime.UtcNow.Date;
                var week = today.AddDays(-7);
                var trades = new List<(DateTime closed, decimal pnl)>();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT ExitTime, RealizedPnL FROM Trades ORDER BY ExitTime DESC LIMIT 500";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        DateTime.TryParse(r.GetString(0), out var ct);
                        var pnl = Convert.ToDecimal(r.GetValue(1), CultureInfo.InvariantCulture);
                        trades.Add((ct.ToUniversalTime(), pnl));
                    }
                }
                dto.TradesToday = trades.Count(t => t.closed.Date == today);
                dto.TradesWeek = trades.Count(t => t.closed >= week);
                dto.TotalPnl = trades.Sum(t => t.pnl);
                int wins = trades.Count(t => t.pnl > 0);
                dto.WinRate = trades.Count > 0 ? (decimal)wins / trades.Count : 0;
                decimal gw = trades.Where(t => t.pnl > 0).Sum(t => t.pnl);
                decimal gl = trades.Where(t => t.pnl < 0).Sum(t => Math.Abs(t.pnl));
                dto.ProfitFactor = gl > 0 ? gw / gl : (gw > 0 ? 99m : 0m);
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "[TWA] stats"); }
        return dto;
    }

    public void RequestPause(int minutes)
    {
        var until = DateTime.UtcNow.AddMinutes(Math.Clamp(minutes, 1, 24 * 60));
        File.WriteAllText(KillFlag, $"{DateTime.UtcNow:o}\ntwa /pause until {until:o}\n");
    }

    public void RequestKill()
    {
        File.WriteAllText(KillFlag, $"{DateTime.UtcNow:o}\ntwa FLATTEN ALL / KILL\n");
        try
        {
            File.WriteAllText(Path.Combine(SharedRoot, "flatten_demo.flag"), DateTime.UtcNow.ToString("o") + "\ntwa kill\n");
        }
        catch { }
        // Best-effort clear demo positions immediately
        try
        {
            if (Directory.Exists(SharedRoot))
            {
                foreach (var d in Directory.GetDirectories(SharedRoot, "client_*"))
                {
                    var path = Path.Combine(d, "demo-account.json");
                    if (!File.Exists(path)) continue;
                    var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
                    if (node?["Positions"] is System.Text.Json.Nodes.JsonArray arr) arr.Clear();
                    if (node?["PendingOrders"] is System.Text.Json.Nodes.JsonArray pend) pend.Clear();
                    if (node != null)
                        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[TWA] demo flatten"); }
    }

    public void RequestResume()
    {
        try { if (File.Exists(KillFlag)) File.Delete(KillFlag); } catch { }
    }

    public void CloseDemoSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        try
        {
            if (!Directory.Exists(SharedRoot)) return;
            foreach (var d in Directory.GetDirectories(SharedRoot, "client_*"))
            {
                var path = Path.Combine(d, "demo-account.json");
                if (!File.Exists(path)) continue;
                var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
                if (node?["Positions"] is not System.Text.Json.Nodes.JsonArray arr) continue;
                for (int i = arr.Count - 1; i >= 0; i--)
                {
                    var sym = arr[i]?["Symbol"]?.ToString();
                    if (string.Equals(sym, symbol, StringComparison.OrdinalIgnoreCase))
                        arr.RemoveAt(i);
                }
                File.WriteAllText(path, node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[TWA] close {s}", symbol); }
    }
}
