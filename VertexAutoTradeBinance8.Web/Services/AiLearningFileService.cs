using System.Text.Json;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services
{
    public class AiLearningFileService
    {
        private readonly string FilePath;
        private readonly ILogger<AiLearningFileService> _logger;
        private readonly IWebHostEnvironment _env;
        
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public AiLearningFileService(IWebHostEnvironment env, ILogger<AiLearningFileService> logger)
        {
            // путь к файлу ai-models/ai_learning.json
          //  FilePath =  @"C:\Users\karap\source\repos\VertexAutoTradeBinance8\VertexAutoTradeBinance AI v5.0 Full Autonomous Mode\bin\Debug\net8.0\ai-models\ai_learning.json";
            //   FilePath = @"F:\VERTEX TRADING SYSTEM\TradingAI\VertexAutoTradeBinance AI v5.0 Full Autonomous Mode\bin\Debug\net8.0\ai-models\ai_learning.json";

            _env = env;
            _logger = logger;
            FilePath = Path.Combine(AppContext.BaseDirectory, "ai-models","ai_learning.json");
        }

        public AiLearningSnapshot? LoadSnapshot()
        {
            if (!File.Exists(FilePath))
                return null;

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AiLearningSnapshot>(json, JsonOptions);
        }

        public async Task<AiLearningSnapshot?> GetAllAsync()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return null;

                using var stream = File.OpenRead(FilePath);

                var snapshot = await JsonSerializer.DeserializeAsync<AiLearningSnapshot>(
                    stream,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                return snapshot;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AI-LEARN-WEB] Failed to read ai_learning.json");
                return null;
            }
        }

        public async Task<IReadOnlyList<AiLearningPointModel>> LoadAsync(
        DateTime? fromUtc = null,
        int minScore = 0)
        {
            try
            {
                if (!File.Exists(FilePath))
                    return Array.Empty<AiLearningPointModel>();

                var json = await File.ReadAllTextAsync(FilePath);
                // здесь подгони под свою структуру ai_learning.json
                // ниже — пример, если там лежит массив событий
                var events = JsonSerializer.Deserialize<List<AiLearningPointModel>>(json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? new();

                if (fromUtc.HasValue)
                    events = events.Where(e => e.Time >= fromUtc.Value).ToList();

                if (minScore > 0)
                    events = events.Where(e => e.Score >= minScore).ToList();

                return events
                    .OrderBy(e => e.Time)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AiLearningFileService.LoadAsync ERROR");
                return Array.Empty<AiLearningPointModel>();
            }
        }

    }
}
