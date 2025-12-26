using System.Text.Json;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services;

public sealed class AiLearningFileService
{
    private readonly string _filePath;
    private readonly ILogger<AiLearningFileService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public AiLearningFileService(
        IWebHostEnvironment env,
        ILogger<AiLearningFileService> logger)
    {
        _filePath = Path.Combine(
            AppContext.BaseDirectory,
            "ai-models",
            "ai_learning.json");

        _logger = logger;
    }

    // ===============================
    // CORE SAFE READER (ЕДИНЫЙ)
    // ===============================
    private async Task<T?> ReadSafeAsync<T>()
    {
        if (!File.Exists(_filePath))
            return default;

        try
        {
            await using var fs = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            return await JsonSerializer.DeserializeAsync<T>(fs, JsonOptions);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "[AI-LEARN-WEB] File locked, skip tick");
            return default;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AI-LEARN-WEB] Read error");
            return default;
        }
    }

    // ===============================
    // PUBLIC API
    // ===============================

    public Task<AiLearningSnapshot?> LoadSnapshot()
        => ReadSafeAsync<AiLearningSnapshot>();

    public Task<AiLearningSnapshot?> GetAllAsync()
        => ReadSafeAsync<AiLearningSnapshot>();

    public async Task<IReadOnlyList<AiLearningPointModel>> LoadAsync(
        DateTime? fromUtc = null,
        int minScore = 0)
    {
        var data = await ReadSafeAsync<List<AiLearningPointModel>>()
                   ?? new();

        if (fromUtc.HasValue)
            data = data.Where(e => e.Time >= fromUtc.Value).ToList();

        if (minScore > 0)
            data = data.Where(e => e.Score >= minScore).ToList();

        return data
            .OrderBy(e => e.Time)
            .ToList();
    }
}
