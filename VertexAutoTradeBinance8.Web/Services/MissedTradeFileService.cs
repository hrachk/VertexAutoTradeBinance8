using System.Text.Json;
using System.Text.Json.Serialization;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services
{
    public class MissedTradeFileService
    {
        private readonly string _filePath;
        private readonly ILogger<MissedTradeFileService> _logger;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true)
            }
        };

        private const int MaxRetries = 4;
        private const int RetryDelayMs = 25;
        private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB hard guard

        public MissedTradeFileService(IWebHostEnvironment env, IConfiguration cfg,
            ILogger<MissedTradeFileService> logger)
        {

          //_filePath = Path.Combine( AppContext.BaseDirectory, "missed_trades.json");
            var root = cfg["SharedData:Root"]
      ?? throw new InvalidOperationException("SharedData:Root not configured");

            _filePath = Path.Combine( root, "missed_trades.json");

            _logger = logger;
        }

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

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

                    // ===== HARD FILE GUARDS =====
                    if (!fi.Exists)
                        return new();

                    if (fi.Length == 0)
                        return new();

                    if (fi.Length > MaxFileSizeBytes)
                    {
                        _logger.LogError(
                            "[WEB][MissedTrades] File too large ({size} bytes), skipping read",
                            fi.Length);
                        return new();
                    }

                    // ===== LOCK-FREE READ =====
                    await using var fs = new FileStream(
                        _filePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 64 * 1024,
                        FileOptions.SequentialScan);

                    // ===== JSON SAFE DESERIALIZE =====
                    var data = await JsonSerializer.DeserializeAsync<List<MissedTradeRecord>>(
                        fs,
                        _jsonOptions,
                        ct);

                    if (data == null)
                        return new();

                    return data;
                }
                catch (JsonException ex)
                {
                    // JSON might be mid-replace → retry allowed
                    _logger.LogWarning(
                        ex,
                        "[WEB][MissedTrades] JSON parse failed (attempt {attempt}/{max})",
                        attempt,
                        MaxRetries);
                }
                catch (IOException ex)
                {
                    // File locked / replace window → retry allowed
                    _logger.LogWarning(
                        ex,
                        "[WEB][MissedTrades] IO contention (attempt {attempt}/{max})",
                        attempt,
                        MaxRetries);
                }
                catch (OperationCanceledException)
                {
                    throw; // правильная отмена
                }
                catch (Exception ex)
                {
                    // Любая другая ошибка — лог и graceful fallback
                    _logger.LogError(
                        ex,
                        "[WEB][MissedTrades] Unexpected error while reading file");
                    return new();
                }

                // ===== BACKOFF =====
                if (attempt < MaxRetries)
                    await Task.Delay(RetryDelayMs * attempt, ct);
            }

            // ===== FINAL SAFE FALLBACK =====
            _logger.LogWarning(
                "[WEB][MissedTrades] Failed to load after {max} attempts",
                MaxRetries);

            return new();
        }


        public List<MissedTradeRecord> Load()
        {
            if (!File.Exists(_filePath))
                return new();

            string json;

            using var fs = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

            using var sr = new StreamReader(fs);
            json = sr.ReadToEnd();

            try
            {

                return JsonSerializer.Deserialize<List<MissedTradeRecord>>(json, JsonOptions)
                       ?? new();
            }
            catch
            {
                return new(); // если формат временно испорчен
            }
        }
    }
}
