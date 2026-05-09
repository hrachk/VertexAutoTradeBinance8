using System.Collections.Concurrent;
using System.Threading;
using VertexAutoTradeBinance8.Models.DTO;
using VertexAutoTradeBinance8.Strategy;

public sealed class DecisionMarkerSink
{
    // =========================
    // INTERNAL NODE STORAGE
    // =========================
    private sealed class Bucket
    {
        public readonly ConcurrentQueue<DecisionMarkerDto> Queue = new();
        public int Count;
        public readonly object Lock = new(); // только для trim
    }

    private readonly ConcurrentDictionary<string, Bucket> _markers = new();

    private const int MaxMarkers = 500;
    private const int TrimTo = 450;
    private readonly ILogger<StrategyEngine> _logger;

    public DecisionMarkerSink( ILogger<StrategyEngine> logger)
    {
        _logger = logger;
    }
    // =========================
    // KEY NORMALIZATION
    // =========================
    private static string Key(string symbol, string tf)
    {
        return string.Concat(
            symbol.ToUpperInvariant(),
            ":",
           tf
        );
    }

    private static string NormalizeTf(string tf)
    {
        return tf switch
        {
            "OneSecond" or "1s" => "1s",
            "OneMinute" or "1m" => "1m",
            "ThreeMinutes" or "3m" => "3m",
            "FiveMinutes" or "5m" => "5m",
            "FifteenMinutes" or "15m" => "15m",
            "ThirtyMinutes" or "30m" => "30m",
            "OneHour" or "1h" => "1h",
            "TwoHour" or "2h" => "2h",
            "FourHour" or "4h" => "4h",
            "SixHour" or "6h" => "6h",
            "EightHour" or "8h" => "8h",
            "TwelveHour" or "12h" => "12h",
            "OneDay" or "1d" => "1d",
            "ThreeDay" or "3d" => "3d",
            "OneWeek" or "1w" => "1w",
            "OneMonth" or "1M" => "1M",
            _ => tf.ToLowerInvariant()
        };
    }

    // =========================
    // ADD (LOCK-FREE FAST PATH)
    // =========================
    public void Add(DecisionMarkerDto marker)
    {
        _logger.LogInformation("[MARKER] ADD {symbol} {tf}", marker.Symbol, marker.Timeframe);
        if (marker == null ||
            string.IsNullOrWhiteSpace(marker.Symbol) ||
            string.IsNullOrWhiteSpace(marker.Timeframe))
            return;

        var tf = NormalizeTf(marker.Timeframe);

        var key = Key(marker.Symbol, tf);
        var bucket = _markers.GetOrAdd(key, _ => new Bucket());

       

        bucket.Queue.Enqueue(marker);

        var newCount = Interlocked.Increment(ref bucket.Count);

        if (newCount > MaxMarkers)
        {
            Trim(bucket);
        }
    }

    // =========================
    // TRIM (RARE LOCK)
    // =========================
    private void Trim(Bucket bucket)
    {
        lock (bucket.Lock)
        {
            while (bucket.Count > TrimTo &&
                   bucket.Queue.TryDequeue(out _))
            {
                Interlocked.Decrement(ref bucket.Count);
            }
        }
    }

    // =========================
    // GET (LOCK-FREE SNAPSHOT)
    // =========================
    public IReadOnlyList<DecisionMarkerDto> Get(
        string symbol,
        string timeframe,
        DateTime? fromUtc = null)
    {
        var key = Key(symbol, timeframe);

        if (!_markers.TryGetValue(key, out var bucket))
            return Array.Empty<DecisionMarkerDto>();

        var list = bucket.Queue.ToArray();

        if (fromUtc.HasValue)
        {
            list = list
                .Where(x => x.CandleTimeUtc >= fromUtc.Value)
                .ToArray();
        }

        return list;
    }

    // =========================
    // DUMP ALL (LOCK-FREE)
    // =========================
    public IReadOnlyDictionary<string, IReadOnlyList<DecisionMarkerDto>> DumpAll()
    {
        var result = new Dictionary<string, IReadOnlyList<DecisionMarkerDto>>();

        foreach (var kv in _markers)
        {
            var copy = kv.Value.Queue.ToArray();
            result[kv.Key] = copy;
        }

        return result;
    }

    // =========================
    // RESTORE (SAFE REPLACE)
    // =========================
    public void Restore(string key, IEnumerable<DecisionMarkerDto> markers)
    {
        if (string.IsNullOrWhiteSpace(key) || markers == null)
            return;

        var bucket = new Bucket();

        var safe = markers
            .Where(m => m != null)
            .TakeLast(MaxMarkers);

        foreach (var m in safe)
        {
            bucket.Queue.Enqueue(m);
            bucket.Count++;
        }

        _markers.AddOrUpdate(key, bucket, (_, __) => bucket);
    }
}