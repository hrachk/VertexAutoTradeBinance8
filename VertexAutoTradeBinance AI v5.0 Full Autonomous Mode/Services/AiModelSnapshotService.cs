// ================================================================
// AI Snapshot Service
// ================================================================
using System.Text.Json;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Services;

public class AiModelSnapshotService
{
    private readonly ILogger<AiModelSnapshotService> _logger;
    private readonly string _folder;

    public AiModelSnapshotService(ILogger<AiModelSnapshotService> logger)
    {
        _logger = logger;
        // Shared with Web UI: C:\VertexShared\ai-models
        _folder = SharedDataPaths.AiModelsFolder;

        try
        {
            SharedDataPaths.EnsureDirectories();
            _logger.LogInformation("🤖 AI-СНАПШОТ: folder → {Folder}", _folder);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🤖 AI-СНАПШОТ: не удалось создать папку {Folder}", _folder);
        }
    }

    private string BuildFileName(DateTime utcNow)
        => Path.Combine(_folder, $"ai-model-{utcNow:yyyyMMdd}.json");

    public async Task SaveSnapshotAsync(
        AiLearningSnapshot state,
        CancellationToken ct = default)
    {
        try
        {
            if ((state.Symbols == null || state.Symbols.Count == 0)
                && (state.MarketStates == null || state.MarketStates.Count == 0)
                && (state.Trades == null || state.Trades.Count == 0))
            {
                _logger.LogWarning("🤖 AI-МОДЕЛЬ: снапшот пуст → skip");
                return;
            }

            state.CreatedAtUtc = DateTime.UtcNow;

            var opts = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(state, opts);
            var path = BuildFileName(state.CreatedAtUtc);

            // Atomic write: temp → replace, so we never leave a half-written / null-byte file
            var tempPath = path + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, ct);
            File.Move(tempPath, path, overwrite: true);

            _logger.LogInformation(
                "\n🤖 AI-МОДЕЛЬ: снапшот сохранён\n" +
                "• Файл:      {Path}\n" +
                "• Symbols:   {Symbols}\n" +
                "• States:    {States}\n" +
                "• Trades:    {Trades}\n",
                path,
                state.Symbols?.Count ?? 0,
                state.MarketStates?.Count ?? 0,
                state.Trades?.Count ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🤖 AI-СНАПШОТ: ошибка при сохранении снапшота");
        }
    }

    public async Task<AiLearningSnapshot?> LoadLatestAsync(
        CancellationToken ct = default)
    {
        try
        {
            if (!Directory.Exists(_folder))
                return null;

            var files = Directory.GetFiles(_folder, "ai-model-*.json")
                .OrderByDescending(f => f) // newest first by name (yyyyMMdd)
                .ToArray();

            if (files.Length == 0)
                return null;

            foreach (var candidate in files)
            {
                try
                {
                    // Skip empty / null-byte / garbage files
                    var fi = new FileInfo(candidate);
                    if (fi.Length == 0)
                    {
                        _logger.LogWarning("🤖 AI-МОДЕЛЬ: пустой файл → SKIP {Path}", candidate);
                        TryQuarantine(candidate);
                        continue;
                    }

                    var json = await File.ReadAllTextAsync(candidate, ct);

                    if (string.IsNullOrWhiteSpace(json) || json[0] == '\0')
                    {
                        _logger.LogWarning("🤖 AI-МОДЕЛЬ: файл содержит null/пусто → SKIP {Path}", candidate);
                        TryQuarantine(candidate);
                        continue;
                    }

                    var state = JsonSerializer.Deserialize<AiLearningSnapshot>(json);

                    if (state == null || state.Symbols == null || state.Symbols.Count == 0)
                    {
                        _logger.LogWarning("🤖 AI-МОДЕЛЬ: найден пустой/невалидный снапшот → SKIP {Path}", candidate);
                        continue;
                    }

                    _logger.LogInformation(
                        "\n🤖 AI-МОДЕЛЬ: загружен сохранённый снапшот\n" +
                        "• Файл:      {Path}\n" +
                        "• Время UTC: {Time}\n" +
                        "• Символов:  {Count}\n",
                        candidate,
                        state.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                        state.Symbols.Count);

                    return state;
                }
                catch (JsonException jex)
                {
                    _logger.LogWarning(jex, "🤖 AI-МОДЕЛЬ: битый JSON → SKIP {Path}", candidate);
                    TryQuarantine(candidate);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "🤖 AI-МОДЕЛЬ: не удалось прочитать {Path} → SKIP", candidate);
                }
            }

            _logger.LogWarning("🤖 AI-МОДЕЛЬ: ни один снапшот не удалось загрузить");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🤖 AI-МОДЕЛЬ: ошибка при загрузке последнего снапшота");
            return null;
        }
    }

    /// <summary>
    /// Rename corrupt file so it is not picked again (keeps it for diagnostics).
    /// </summary>
    private void TryQuarantine(string path)
    {
        try
        {
            var bad = path + ".bad";
            if (File.Exists(bad))
                File.Delete(bad);
            File.Move(path, bad);
            _logger.LogInformation("🤖 AI-МОДЕЛЬ: битый файл перемещён → {Bad}", bad);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "🤖 AI-МОДЕЛЬ: не удалось переместить битый файл {Path}", path);
        }
    }
}
