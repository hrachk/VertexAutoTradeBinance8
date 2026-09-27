using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services.SignalQuality;

public sealed class AuctionTelemetryRecord
{
    public DateTime TimestampUtc { get; set; }
    public string Symbol { get; set; } = "";
    public string Direction { get; set; } = "";
    public decimal CompositeScore { get; set; }
    public decimal Trend { get; set; }
    public decimal Derivatives { get; set; }
    public decimal Volume { get; set; }
    public decimal RiskReward { get; set; }
    public string AuctionResult { get; set; } = "";
}

/// <summary>Append-only JSONL telemetry for signal auctions + in-memory KPI.</summary>
public sealed class AuctionTelemetry
{
    private readonly ILogger<AuctionTelemetry> _log;
    private readonly string _path;
    private readonly object _fileLock = new();
    private long _total;
    private long _approved;
    private long _below;
    private long _outperformed;
    private double _sumApprovedScore;
    private readonly ConcurrentQueue<AuctionTelemetryRecord> _recent = new();

    public AuctionTelemetry(IConfiguration cfg, ILogger<AuctionTelemetry> log)
    {
        _log = log;
        var root = cfg["SharedData:Root"]
                   ?? Path.Combine(AppContext.BaseDirectory, "SharedData");
        try { Directory.CreateDirectory(root); } catch { /* */ }
        _path = Path.Combine(root, "signal_auction_logs.jsonl");
    }

    public void Log(AuctionTelemetryRecord r)
    {
        Interlocked.Increment(ref _total);
        if (r.AuctionResult.StartsWith("APPROVED", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _approved);
            Interlocked.Exchange(ref _sumApprovedScore,
                _sumApprovedScore + (double)r.CompositeScore); // not perfect atomic but KPI only
        }
        else if (r.AuctionResult.Contains("BELOW", StringComparison.OrdinalIgnoreCase))
            Interlocked.Increment(ref _below);
        else if (r.AuctionResult.Contains("OUTPERFORMED", StringComparison.OrdinalIgnoreCase))
            Interlocked.Increment(ref _outperformed);

        _recent.Enqueue(r);
        while (_recent.Count > 200 && _recent.TryDequeue(out _)) { }

        try
        {
            var line = JsonSerializer.Serialize(r) + "\n";
            lock (_fileLock)
                File.AppendAllText(_path, line);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[AUCTION-TEL] write failed");
        }
    }

    public (long total, long approved, long below, long outperformed, double avgApprovedScore, double passRatePct) GetKpi()
    {
        long t = Interlocked.Read(ref _total);
        long a = Interlocked.Read(ref _approved);
        long b = Interlocked.Read(ref _below);
        long o = Interlocked.Read(ref _outperformed);
        double avg = a > 0 ? _sumApprovedScore / a : 0;
        double pass = t > 0 ? 100.0 * a / t : 0;
        return (t, a, b, o, avg, pass);
    }

    public string LogPath => _path;
}
