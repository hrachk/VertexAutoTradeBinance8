using System.Text.Json;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Отвечает за сохранение и загрузку AI-снапшотов на диск.
    /// Формат файлов: ./ai-models/ai-model-YYYYMMDD.json
    /// </summary>
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

        /// <summary>
        /// Сохранение снапшота на диск.
        /// </summary>
        public async Task SaveSnapshotAsync(
            AiSelfLearningService.AiLearningSnapshot state,
            CancellationToken ct = default)
        {
            try
            {
                state.CreatedAtUtc = DateTime.UtcNow;

                var opts = new JsonSerializerOptions
                {
                    WriteIndented = true
                };

                var json = JsonSerializer.Serialize(state, opts);
                var path = BuildFileName(state.CreatedAtUtc);

                await File.WriteAllTextAsync(path, json, ct);

                var symbolsCount = state.Symbols?.Count ?? 0;

                _logger.LogInformation(
                    "\n🤖 AI-МОДЕЛЬ: снапшот сохранён\n" +
                    "• Файл:      {Path}\n" +
                    "• Время UTC: {Time}\n" +
                    "• Символов:  {Count}\n",
                    path,
                    state.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                    symbolsCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🤖 AI-СНАПШОТ: ошибка при сохранении снапшота");
            }
        }

        /// <summary>
        /// Загружает последний доступный снапшот (по дате в имени файла).
        /// </summary>
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
                if (state != null)
                {
                    var symbolsCount = state.Symbols?.Count ?? 0;

                    _logger.LogInformation(
                        "\n🤖 AI-МОДЕЛЬ: загружен сохранённый снапшот\n" +
                        "• Файл:      {Path}\n" +
                        "• Время UTC: {Time}\n" +
                        "• Символов:  {Count}\n",
                        latest,
                        state.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                        symbolsCount);
                }
                else
                {
                    _logger.LogWarning(
                        "🤖 AI-СНАПШОТ: не удалось десериализовать файл {Path}",
                        latest);
                }

                return state;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🤖 AI-СНАПШОТ: ошибка при загрузке последнего снапшота");
                return null;
            }
        }
    }
}
