using System.Text.Json;

namespace VertexAutoTradeBinance8.Services
{
    public class AiModelSnapshotService
    {
        private readonly ILogger<AiModelSnapshotService> _logger;
        private readonly string _folder;

        public AiModelSnapshotService(ILogger<AiModelSnapshotService> logger)
        {
            _logger = logger;
            _folder = Path.Combine(AppContext.BaseDirectory, "ai-models");

            try
            {
                if (!Directory.Exists(_folder))
                    Directory.CreateDirectory(_folder);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🤖 AI-СНАПШОТ: не удалось создать папку {Folder}", _folder);
            }
        }

        private string BuildFileName(DateTime utcNow)
            => Path.Combine(_folder, $"ai-model-{utcNow:yyyyMMdd}.json");

        public async Task SaveSnapshotAsync(
            AiSelfLearningService.AiLearningSnapshot state,
            CancellationToken ct = default)
        {
            try
            {
                // ❗ FIX: Не сохранять пустой снапшот
                if (state.Symbols == null || state.Symbols.Count == 0)
                {
                    _logger.LogWarning("🤖 AI-МОДЕЛЬ: снапшот не сохранён → нет символов");
                    return;
                }

                state.CreatedAtUtc = DateTime.UtcNow;

                var opts = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(state, opts);
                var path = BuildFileName(state.CreatedAtUtc);

                await File.WriteAllTextAsync(path, json, ct);

                _logger.LogInformation(
                    "\n🤖 AI-МОДЕЛЬ: снапшот сохранён\n" +
                    "• Файл:      {Path}\n" +
                    "• Время UTC: {Time}\n" +
                    "• Символов:  {Count}\n",
                    path,
                    state.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                    state.Symbols.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🤖 AI-СНАПШОТ: ошибка при сохранении снапшота");
            }
        }

        public async Task<AiSelfLearningService.AiLearningSnapshot?> LoadLatestAsync(
            CancellationToken ct = default)
        {
            try
            {
                if (!Directory.Exists(_folder))
                    return null;

                var files = Directory.GetFiles(_folder, "ai-model-*.json");
                if (files.Length == 0)
                    return null;

                var latest = files.OrderBy(f => f).Last();
                var json = await File.ReadAllTextAsync(latest, ct);

                var state = JsonSerializer.Deserialize<AiSelfLearningService.AiLearningSnapshot>(json);

                // ❗ FIX: пустые снапшоты игнорируем
                if (state == null || state.Symbols == null || state.Symbols.Count == 0)
                {
                    _logger.LogWarning("🤖 AI-МОДЕЛЬ: найден пустой файл снапшота → SKIP {Path}", latest);
                    return null;
                }

                _logger.LogInformation(
                    "\n🤖 AI-МОДЕЛЬ: загружен сохранённый снапшот\n" +
                    "• Файл:      {Path}\n" +
                    "• Время UTC: {Time}\n" +
                    "• Символов:  {Count}\n",
                    latest,
                    state.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                    state.Symbols.Count);

                return state;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🤖 AI-МОДЕЛЬ: ошибка при загрузке последнего снапшота");
                return null;
            }
        }
    }
}
