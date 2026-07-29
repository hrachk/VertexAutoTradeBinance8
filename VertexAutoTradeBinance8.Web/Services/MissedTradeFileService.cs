using System.Text.Json;
using System.Text.Json.Serialization;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services
{
    public class MissedTradeFileService
    {
        private readonly string _filePath;
        private string _globalRoot = AppContext.BaseDirectory;
        private readonly ILogger<MissedTradeFileService> _logger;

        private const int MaxRetries     = 4;
        private const int RetryDelayMs   = 25;

        // Hard guard: if the file is somehow larger than this, truncate
        // it immediately rather than skipping the read entirely — so the
        // Web UI always has SOMETHING to show instead of going blank.
        // Root cause of growth is fixed on the Engine side (MaxRecords=500
        // + compact JSON), but this guard handles any already-bloated file.
        private const long MaxFileSizeBytes = 10L * 1024 * 1024; // 10 MB

        // How many records the Web is willing to display. Mirrors the
        // Engine's MaxRecords = 500 so the trim never needs to fire here
        // in normal operation; it's a belt-and-suspenders fallback.
        private const int MaxDisplayRecords = 500;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
            ReadCommentHandling         = JsonCommentHandling.Skip,
            AllowTrailingCommas         = true,
            NumberHandling              = JsonNumberHandling.AllowReadingFromString,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, true)
            }
        };

        public MissedTradeFileService(
            IWebHostEnvironment env,
            IConfiguration cfg,
            ILogger<MissedTradeFileService> logger)
        {
            var root = cfg["SharedData:Root"];
            _filePath = !string.IsNullOrEmpty(root)
                ? Path.Combine(root, "missed_trades.json")
                : Path.Combine(AppContext.BaseDirectory, "missed_trades.json");

        // ── Per-client access ────────────────────────────────
        _globalRoot = root ?? AppContext.BaseDirectory;

            _logger = logger;
        }

        /// <summary>Load from a specific file path — for per-client data isolation.</summary>
        public async Task<List<MissedTradeRecord>> LoadFromPathAsync(
            string filePath, CancellationToken ct = default)
        {
            // Swap path temporarily by delegating to a path-parameterised inner call
            var saved  = _filePath;
            // Use field injection trick: directly read from the specified path
            if (!File.Exists(filePath)) return new();
            try
            {
                var fi = new FileInfo(filePath);
                if (fi.Length == 0) return new();
                var json = await File.ReadAllTextAsync(filePath, ct);
                return System.Text.Json.JsonSerializer.Deserialize<List<MissedTradeRecord>>(
                    json,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new();
            }
            catch { return new(); }
        }

        public async Task<List<MissedTradeRecord>> LoadAsync(CancellationToken ct = default)
        {
            if (!File.Exists(_filePath))
                return new();

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var fi = new FileInfo(_filePath);

                    if (!fi.Exists || fi.Length == 0)
                        return new();

                    if (fi.Length > MaxFileSizeBytes)
                    {
                        // File grew beyond the hard guard — this means the Engine
                        // ran without the MaxRecords fix for a long time.
                        // Truncate to the most recent MaxDisplayRecords entries
                        // so the UI recovers immediately without waiting for
                        // someone to manually delete the file.
                        _logger.LogWarning(
                            "[WEB][MissedTrades] File too large ({size:N0} bytes) — truncating to last {max} records",
                            fi.Length, MaxDisplayRecords);

                        await TruncateFileAsync(ct);

                        // Re-check size after truncation
                        fi.Refresh();
                        if (fi.Length > MaxFileSizeBytes)
                        {
                            _logger.LogError(
                                "[WEB][MissedTrades] Truncation failed — file still {size:N0} bytes, skipping",
                                fi.Length);
                            return new();
                        }
                    }

                    await using var fs = new FileStream(
                        _filePath,
                        FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 64 * 1024,
                        FileOptions.SequentialScan);

                    var data = await JsonSerializer.DeserializeAsync<List<MissedTradeRecord>>(
                        fs, _jsonOptions, ct);

                    if (data == null)
                        return new();

                    // Belt-and-suspenders: cap what we return to the UI
                    return data.Count > MaxDisplayRecords
                        ? data.GetRange(data.Count - MaxDisplayRecords, MaxDisplayRecords)
                        : data;
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "[WEB][MissedTrades] JSON parse failed (attempt {attempt}/{max})",
                        attempt, MaxRetries);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "[WEB][MissedTrades] IO contention (attempt {attempt}/{max})",
                        attempt, MaxRetries);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[WEB][MissedTrades] Unexpected error");
                    return new();
                }

                if (attempt < MaxRetries)
                    await Task.Delay(RetryDelayMs * attempt, ct);
            }

            _logger.LogWarning(
                "[WEB][MissedTrades] Failed to load after {max} attempts", MaxRetries);
            return new();
        }

        /// <summary>
        /// Reads the entire file, keeps only the last MaxDisplayRecords
        /// entries, and rewrites it — used when the file grew beyond the
        /// hard size limit (Engine running without the MaxRecords cap fix).
        /// </summary>
        private async Task TruncateFileAsync(CancellationToken ct)
        {
            try
            {
                List<MissedTradeRecord> all;

                await using (var fs = new FileStream(
                    _filePath,
                    FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 128 * 1024,
                    FileOptions.SequentialScan))
                {
                    all = await JsonSerializer.DeserializeAsync<List<MissedTradeRecord>>(
                              fs, _jsonOptions, ct)
                          ?? new();
                }

                var trimmed = all.Count > MaxDisplayRecords
                    ? all.GetRange(all.Count - MaxDisplayRecords, MaxDisplayRecords)
                    : all;

                var tmp = _filePath + ".webtmp";
                await using (var fs = new FileStream(
                    tmp,
                    FileMode.Create, FileAccess.Write,
                    FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(fs, trimmed, _jsonOptions, ct);
                    await fs.FlushAsync(ct);
                }

                File.Replace(tmp, _filePath, _filePath + ".webbak",
                    ignoreMetadataErrors: true);

                _logger.LogInformation(
                    "[WEB][MissedTrades] Truncated {before} → {after} records",
                    all.Count, trimmed.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WEB][MissedTrades] TruncateFileAsync failed");
            }
        }

        // Sync fallback (used by legacy callers) — same logic, synchronous
        public List<MissedTradeRecord> Load()
        {
            if (!File.Exists(_filePath))
                return new();

            try
            {
                using var fs = new FileStream(
                    _filePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                var json = sr.ReadToEnd();

                var data = JsonSerializer.Deserialize<List<MissedTradeRecord>>(json, _jsonOptions)
                           ?? new();

                return data.Count > MaxDisplayRecords
                    ? data.GetRange(data.Count - MaxDisplayRecords, MaxDisplayRecords)
                    : data;
            }
            catch
            {
                return new();
            }
        }
        /// <summary>Returns a reader scoped to a specific client root folder.</summary>
        public string GetPathForRoot(string root)
            => Path.Combine(root, "missed_trades.json");
    }
}
