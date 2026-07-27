using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance.Services
{
    /// <summary>
    /// Writes quality signals to live_signals.json so the Web /market page
    /// displays them in real-time — regardless of whether AutoTrade is on.
    /// Called after AI confirmation passes, before execution pipeline.
    /// File: {SharedData:Root}/live_signals.json  (max 50, TTL 2h)
    /// </summary>
    public class LiveSignalService
    {
        private readonly string _filePath;
        private readonly ILogger<LiveSignalService> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private const int MaxRecords = 50;
        private static readonly TimeSpan RecordTtl = TimeSpan.FromHours(2);

        public LiveSignalService(IConfiguration cfg, ILogger<LiveSignalService> logger)
        {
            var root = cfg["SharedData:Root"] ?? @"C:\Vertex\Engines\client_001";
            _filePath = Path.Combine(root, "live_signals.json");
            _logger = logger;
        }

        public async Task AppendAsync(dynamic signal, CancellationToken ct = default)
        {
            try
            {
                if (signal == null) return;
                decimal entry = (decimal)(signal.EntryPrice ?? 0m);
                decimal sl    = (decimal)(signal.StopLoss ?? 0m);
                if (entry <= 0 || sl <= 0) return;

                var tps = signal.TakeProfits as System.Collections.Generic.List<decimal>;
                if (tps == null || tps.Count == 0) return;

                decimal conf = (decimal)(signal.Confidence ?? 0m);
                if (conf <= 0) return;

                await _lock.WaitAsync(ct);
                try
                {
                    var records = await LoadAsync();
                    var cutoff = DateTime.UtcNow - RecordTtl;
                    records = records.Where(r => r.Time >= cutoff).ToList();

                    records.Insert(0, new LiveSignalRecord
                    {
                        Symbol      = (string)(signal.Symbol ?? ""),
                        Side        = signal.Side?.ToString() ?? "",
                        Time        = DateTime.UtcNow,
                        Entry       = entry,
                        StopLoss    = sl,
                        TakeProfits = tps.ToList(),
                        EntryRangeLow  = signal.EntryRangeLow as decimal?,
                        EntryRangeHigh = signal.EntryRangeHigh as decimal?,
                        Confidence  = (int)(conf * 100),
                        Score       = (int)(((decimal)(signal.AiQuality ?? 0m)) * 100),
                        Reason      = (string)(signal.Reason ?? ""),
                        Atr         = (decimal)(signal.Atr ?? 0m),
                    });

                    if (records.Count > MaxRecords)
                        records = records.Take(MaxRecords).ToList();

                    var dir = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    await File.WriteAllTextAsync(_filePath,
                        JsonSerializer.Serialize(records), ct);
                }
                finally { _lock.Release(); }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LIVESIG] append failed for {sym}", (string)(signal?.Symbol ?? ""));
            }
        }

        private async Task<List<LiveSignalRecord>> LoadAsync()
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
