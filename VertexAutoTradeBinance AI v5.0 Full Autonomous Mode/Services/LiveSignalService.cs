using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Writes strategy candidates to:
    ///   1) live_signals.json  — primary for /market Live Signals cards
    ///   2) missed_trades.json — so /missed-trades page keeps working as before
    ///
    /// Historically UI relied on missed_trades (filled on REJECT). When signals
    /// stopped entering RejectAsync, both pages went blank. Dual-write restores
    /// visibility on both pages whenever a signal is generated.
    /// </summary>
    public class LiveSignalService
    {
        private readonly string _filePath;
        private readonly string _missedPath;
        private readonly ILogger<LiveSignalService> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private const int MaxRecords = 50;
        private const int MaxMissedRecords = 500;
        private static readonly TimeSpan RecordTtl = TimeSpan.FromHours(2);

        public LiveSignalService(IConfiguration cfg, ILogger<LiveSignalService> logger)
        {
            var root = cfg["SharedData:Root"] ?? @"C:\Vertex\Engines\client_001";
            _filePath = Path.Combine(root, "live_signals.json");
            _missedPath = Path.Combine(root, "missed_trades.json");
            _logger = logger;
            _logger.LogInformation("[LIVESIG] live={path} missed={missed}", _filePath, _missedPath);
        }

        public async Task AppendAsync(dynamic signal, CancellationToken ct = default)
        {
            try
            {
                if (signal == null) return;
                decimal entry = (decimal)(signal.EntryPrice ?? 0m);
                decimal sl    = (decimal)(signal.StopLoss ?? 0m);
                if (entry <= 0 || sl <= 0) return;

                List<decimal>? tps = null;
                try { tps = signal.TakeProfits as System.Collections.Generic.List<decimal>; } catch { }
                if (tps == null || tps.Count == 0)
                {
                    try
                    {
                        decimal? single = signal.TakeProfit as decimal?;
                        if (single.HasValue && single.Value > 0)
                            tps = new List<decimal> { single.Value };
                    }
                    catch { }
                }
                if (tps == null || tps.Count == 0)
                {
                    decimal atr = 0m;
                    try { atr = (decimal)(signal.Atr ?? 0m); } catch { }
                    if (atr <= 0) atr = entry * 0.01m;
                    bool isLong = true;
                    try
                    {
                        var sideStr = signal.Side?.ToString() ?? "";
                        isLong = !sideStr.Contains("Sell", StringComparison.OrdinalIgnoreCase);
                    }
                    catch { }
                    tps = isLong
                        ? new List<decimal> { entry + atr, entry + atr * 2m, entry + atr * 3m }
                        : new List<decimal> { entry - atr, entry - atr * 2m, entry - atr * 3m };
                }

                decimal conf = 0m;
                try { conf = (decimal)(signal.Confidence ?? 0m); } catch { }
                if (conf > 1.5m) conf = conf / 100m;
                if (conf < 0m) conf = 0m;

                string symbol = (string)(signal.Symbol ?? "");
                string side = signal.Side?.ToString() ?? "";
                string reason = (string)(signal.Reason ?? "");
                decimal atrVal = 0m;
                try { atrVal = (decimal)(signal.Atr ?? 0m); } catch { }
                int confPct = (int)Math.Round(conf * 100m);
                int score = 0;
                try { score = (int)(((decimal)(signal.AiQuality ?? 0m)) * 100); } catch { }

                decimal? rangeLow = null, rangeHigh = null;
                try { rangeLow = signal.EntryRangeLow as decimal?; } catch { }
                try { rangeHigh = signal.EntryRangeHigh as decimal?; } catch { }

                await _lock.WaitAsync(ct);
                try
                {
                    // ── 1) live_signals.json ──────────────────────────────
                    var records = await LoadLiveAsync();
                    var cutoff = DateTime.UtcNow - RecordTtl;
                    records = records.Where(r => r.Time >= cutoff).ToList();

                    records.Insert(0, new LiveSignalRecord
                    {
                        Symbol = symbol,
                        Side = side,
                        Time = DateTime.UtcNow,
                        Entry = entry,
                        StopLoss = sl,
                        TakeProfits = tps.ToList(),
                        EntryRangeLow = rangeLow,
                        EntryRangeHigh = rangeHigh,
                        Confidence = confPct,
                        Score = score,
                        Reason = reason,
                        Atr = atrVal,
                    });

                    if (records.Count > MaxRecords)
                        records = records.Take(MaxRecords).ToList();

                    var dir = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    await File.WriteAllTextAsync(_filePath,
                        JsonSerializer.Serialize(records), ct);

                    // ── 2) missed_trades.json (UI /missed-trades + Market fallback)
                    await AppendMissedAsync(new MissedTradeRecord
                    {
                        Event = "SIGNAL",
                        Symbol = symbol,
                        Side = side,
                        Time = DateTime.UtcNow,
                        Entry = entry,
                        StopLoss = sl,
                        TakeProfits = tps.ToList(),
                        EntryRangeLow = rangeLow,
                        EntryRangeHigh = rangeHigh,
                        Confidence = confPct,
                        Score = score,
                        Reason = string.IsNullOrEmpty(reason) ? "LIVE_SIGNAL" : reason,
                        Atr = atrVal,
                        Note = "live_signal_mirror",
                    }, ct);

                    _logger.LogInformation(
                        "[LIVESIG] wrote {sym} {side} conf={c} → live+missed",
                        symbol, side, confPct);
                }
                finally { _lock.Release(); }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LIVESIG] append failed for {sym}", (string)(signal?.Symbol ?? ""));
            }
        }

        private async Task AppendMissedAsync(MissedTradeRecord record, CancellationToken ct)
        {
            try
            {
                List<MissedTradeRecord> list = new();
                if (File.Exists(_missedPath))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(_missedPath, ct);
                        list = JsonSerializer.Deserialize<List<MissedTradeRecord>>(json) ?? new();
                    }
                    catch { list = new(); }
                }

                list.Insert(0, record);
                if (list.Count > MaxMissedRecords)
                    list = list.Take(MaxMissedRecords).ToList();

                var dir = Path.GetDirectoryName(_missedPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var tmp = _missedPath + ".tmp";
                await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(list), ct);
                File.Move(tmp, _missedPath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LIVESIG] missed_trades mirror write failed");
            }
        }

        private async Task<List<LiveSignalRecord>> LoadLiveAsync()
        {
            try
            {
                if (!File.Exists(_filePath)) return new();
                var json = await File.ReadAllTextAsync(_filePath);
                return JsonSerializer.Deserialize<List<LiveSignalRecord>>(json) ?? new();
            }
            catch { return new(); }
        }
    }

    public class LiveSignalRecord
    {
        public string Symbol { get; set; } = "";
        public string Side { get; set; } = "";
        public DateTime Time { get; set; }
        public decimal Entry { get; set; }
        public decimal StopLoss { get; set; }
        public List<decimal> TakeProfits { get; set; } = new();
        public decimal? EntryRangeLow { get; set; }
        public decimal? EntryRangeHigh { get; set; }
        public int Confidence { get; set; }
        public int Score { get; set; }
        public string Reason { get; set; } = "";
        public decimal Atr { get; set; }
    }
}
