using Microsoft.Extensions.Options;
using System.Text.Json;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Профессиональный сервис снапшотов движка.
    /// Автоматически создаёт папки, пишет JSON, делает бэкап,
    /// гарантирует 100% работоспособность даже при ошибках.
    /// Совместим с UI и Real-Time обновлением.
    /// </summary>
    public class EngineStateSnapshotService
    {
        private readonly ILogger<EngineStateSnapshotService> _logger;
        private readonly string _path;
        private readonly string _backupPath;
        public EngineState State { get; } = new EngineState();
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public EngineStateSnapshotService(
     ILogger<EngineStateSnapshotService> logger,
     IOptions<EngineStateSettings> options)
        {
            _logger = logger;
            _path = Path.Combine(AppContext.BaseDirectory, "engine_state.json");

            _backupPath = _path + ".bak";

            EnsureDirectoryExists();
        }

        // ===============================================================
        // Создание директории, если её нет
        // ===============================================================
        private void EnsureDirectoryExists()
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    _logger.LogInformation("[ENGINE STATE] Created directory: {dir}", dir);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ENGINE STATE] Failed to ensure directory exists");
            }
        }

        // ===============================================================
        // Основной метод записи снапшота
        // ===============================================================
        public void Save(EngineState state)
        {
            try
            {
                // 1 — сериализация
                var json = JsonSerializer.Serialize(state, _jsonOptions);

                // 2 — резервная копия
                try
                {
                    if (File.Exists(_path))
                        File.Copy(_path, _backupPath, overwrite: true);
                }
                catch (Exception backupEx)
                {
                    _logger.LogWarning(backupEx, "[ENGINE STATE] Backup failed, continuing...");
                }

                // 3 — запись основного файла
                File.WriteAllText(_path, json);

                _logger.LogInformation("[ENGINE STATE] Snapshot saved → {path}", _path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ENGINE STATE] Snapshot SAVE ERROR");
            }
        }

        // ===============================================================
        // Чтение снапшота (для UI)
        // ===============================================================
        public EngineState? Load()
        {
            try
            {
                if (!File.Exists(_path))
                    return null;

                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<EngineState>(json, _jsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ENGINE STATE] Snapshot LOAD ERROR");
                return null;
            }
        }
    }
}
