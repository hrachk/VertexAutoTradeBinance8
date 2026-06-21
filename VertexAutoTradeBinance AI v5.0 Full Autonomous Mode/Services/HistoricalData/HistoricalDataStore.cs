using System.Collections.Concurrent;
using System.Text.Json;

namespace VertexAutoTradeBinance8.Services.HistoricalData
{
    /// <summary>
    /// Portable, file-based historical kline store. No database engine,
    /// no external dependency — just JSON files under a folder
    /// (datadb/SYMBOL/TF.json by default), one file per symbol+timeframe
    /// pair. Chosen deliberately over a real database for this project's
    /// actual scale (tens to low hundreds of symbols, a handful of
    /// timeframes, no need for years of tick-level data) — this keeps the
    /// whole system self-contained and easy to back up/move/inspect by
    /// hand, at the cost of not scaling to a true tick-database's volume.
    /// If that ever becomes the bottleneck, this class's public API
    /// (LoadAsync/AppendAsync) is the seam to swap the storage engine
    /// behind without touching any caller.
    ///
    /// Storage shape: a single JSON array of HistoricalKline per file,
    /// sorted ascending by OpenTime, deduplicated by OpenTime on every
    /// write (so re-fetching overlapping REST ranges is always safe).
    /// </summary>
    public sealed class HistoricalDataStore
    {
        private readonly string _root;
        private readonly ILogger<HistoricalDataStore> _logger;

        // One lock per symbol+tf file, not one global lock — concurrent
        // writes to DIFFERENT files (different symbols/timeframes) never
        // block each other; only writes to the SAME file are serialized.
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

        public HistoricalDataStore(IConfiguration cfg, ILogger<HistoricalDataStore> logger)
        {
            _logger = logger;

            // Lives under SharedData:Root like every other cross-process
            // file in this project (klines_bootstrap.json, decision_trace,
            // appsettings.runtime.json) — same convention, same reasoning:
            // both Engine and Web need to agree on one physical location
            // regardless of which folder either process actually runs from.
            var sharedRoot = cfg["SharedData:Root"];
            var baseDir = !string.IsNullOrWhiteSpace(sharedRoot) ? sharedRoot : AppContext.BaseDirectory;
            _root = Path.Combine(baseDir, "datadb");
            Directory.CreateDirectory(_root);
        }

        private static string SafeSegment(string s) =>
            string.Concat(s.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'));

        private string FilePath(string symbol, string timeframe) =>
            Path.Combine(_root, SafeSegment(symbol.ToUpperInvariant()), $"{SafeSegment(timeframe)}.json");

        private SemaphoreSlim LockFor(string symbol, string timeframe) =>
            _locks.GetOrAdd($"{symbol}:{timeframe}", _ => new SemaphoreSlim(1, 1));

        /// <summary>
        /// Loads all stored bars for a symbol+timeframe, oldest first.
        /// Returns an empty list (not an error) if nothing is stored yet.
        /// </summary>
        public async Task<List<HistoricalKline>> LoadAsync(string symbol, string timeframe, CancellationToken ct = default)
        {
            var path = FilePath(symbol, timeframe);
            if (!File.Exists(path)) return new List<HistoricalKline>();

            var gate = LockFor(symbol, timeframe);
            await gate.WaitAsync(ct);
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var data = await JsonSerializer.DeserializeAsync<List<HistoricalKline>>(fs, cancellationToken: ct);
                return data ?? new List<HistoricalKline>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DATADB] Failed to load {symbol} {tf} — treating as empty", symbol, timeframe);
                return new List<HistoricalKline>();
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Loads just the last `count` bars (oldest-first order preserved)
        /// — convenience for callers that only need recent history, e.g.
        /// for indicator warmup, without loading a potentially large file
        /// fully into a different shape first.
        /// </summary>
        public async Task<List<HistoricalKline>> LoadLastAsync(string symbol, string timeframe, int count, CancellationToken ct = default)
        {
            var all = await LoadAsync(symbol, timeframe, ct);
            return all.Count <= count ? all : all.GetRange(all.Count - count, count);
        }

        /// <summary>
        /// The OpenTime of the OLDEST stored bar, or null if nothing is
        /// stored yet. Used by deep backfill to know where to continue
        /// extending the archive backward in time.
        /// </summary>
        public async Task<long?> GetOldestOpenTimeAsync(string symbol, string timeframe, CancellationToken ct = default)
        {
            var all = await LoadAsync(symbol, timeframe, ct);
            return all.Count > 0 ? all[0].OpenTime : null;
        }

        /// <summary>
        /// Total bar count currently stored for this symbol+timeframe —
        /// cheap-ish introspection for status reporting (loads the full
        /// file, but these files are small enough at this project's scale
        /// that this is fine; revisit if that ever changes).
        /// </summary>
        public async Task<int> CountAsync(string symbol, string timeframe, CancellationToken ct = default)
        {
            var all = await LoadAsync(symbol, timeframe, ct);
            return all.Count;
        }

        /// <summary>
        /// Merges new bars into the stored file: dedup by OpenTime (a bar
        /// with the same OpenTime as an existing one replaces it — handles
        /// the common case of re-fetching the still-forming current bar),
        /// re-sorts by OpenTime, writes back atomically (write to a .tmp
        /// file, then rename — never leaves a half-written file behind if
        /// the process dies mid-write).
        /// </summary>
        public async Task AppendAsync(string symbol, string timeframe, IEnumerable<HistoricalKline> newBars, CancellationToken ct = default)
        {
            var bars = newBars as ICollection<HistoricalKline> ?? newBars.ToList();
            if (bars.Count == 0) return;

            var path = FilePath(symbol, timeframe);
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);

            var gate = LockFor(symbol, timeframe);
            await gate.WaitAsync(ct);
            try
            {
                var existing = new List<HistoricalKline>();
                if (File.Exists(path))
                {
                    try
                    {
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        existing = await JsonSerializer.DeserializeAsync<List<HistoricalKline>>(fs, cancellationToken: ct) ?? new();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[DATADB] Existing file for {symbol} {tf} unreadable — starting fresh", symbol, timeframe);
                    }
                }

                var byTime = new SortedDictionary<long, HistoricalKline>();
                foreach (var k in existing) byTime[k.OpenTime] = k;
                foreach (var k in bars) byTime[k.OpenTime] = k; // new data wins on overlap

                var merged = byTime.Values.ToList();

                var tmpPath = path + ".tmp";
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(fs, merged, cancellationToken: ct);
                }
                File.Move(tmpPath, path, overwrite: true);

                _logger.LogDebug("[DATADB] {symbol} {tf}: {count} bars stored ({added} new/updated)",
                    symbol, timeframe, merged.Count, bars.Count);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// True if a file exists for this symbol+timeframe at all (doesn't
        /// load/parse it — just checks presence, for cheap "do we have
        /// anything yet" checks before deciding whether to backfill).
        /// </summary>
        public bool Has(string symbol, string timeframe) => File.Exists(FilePath(symbol, timeframe));

        // Small marker file (no content needed, presence is the signal)
        // recording "deep backfill reached this symbol's actual listing
        // date on the exchange, stop trying to go further back". Kept as
        // a separate file rather than a field inside the data JSON itself
        // — simpler to reason about, and avoids rewriting the (potentially
        // large) data file just to flip one flag.
        private string ExhaustedMarkerPath(string symbol, string timeframe) =>
            FilePath(symbol, timeframe) + ".exhausted";

        public bool IsBackfillExhausted(string symbol, string timeframe) =>
            File.Exists(ExhaustedMarkerPath(symbol, timeframe));

        public async Task MarkBackfillExhaustedAsync(string symbol, string timeframe, CancellationToken ct = default)
        {
            var path = ExhaustedMarkerPath(symbol, timeframe);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, DateTime.UtcNow.ToString("O"), ct);
        }

        /// <summary>
        /// Lists every symbol currently stored on disk, regardless of
        /// whether it's part of the live trading universe — this is the
        /// whole point of this store existing separately from the
        /// in-memory trading buffer.
        /// </summary>
        public IReadOnlyList<string> ListStoredSymbols()
        {
            if (!Directory.Exists(_root)) return Array.Empty<string>();
            return Directory.GetDirectories(_root)
                .Select(Path.GetFileName)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .OrderBy(s => s)
                .ToList();
        }
    }
}
