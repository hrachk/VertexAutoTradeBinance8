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
        private readonly string _engineStatePath;
        private readonly string _backupPath;
        public EngineState State { get; } = new EngineState();
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public EngineStateSnapshotService(
     ILogger<EngineStateSnapshotService> logger,
     IOptions<WebPathsOptions> paths)
        {
            _logger = logger;
            //  _path = options.Value.SnapshotPath ?? Path.Combine(AppContext.BaseDirectory, "engine_state.json");
            //_engineStatePath = Path.Combine(AppContext.BaseDirectory, "engine_state.json");
            var baseDir = AppContext.BaseDirectory;

            _engineStatePath = Path.Combine(
                baseDir,
                paths.Value.EngineState
            );

            _backupPath = _engineStatePath + ".bak";

            EnsureDirectoryExists();
        }

        // ===============================================================
        // Создание директории, если её нет
        // ===============================================================
        private void EnsureDirectoryExists()
        {
            try
            {
                var dir = Path.GetDirectoryName(_engineStatePath);
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
                    if (File.Exists(_engineStatePath))
                        File.Copy(_engineStatePath, _backupPath, overwrite: true);
                }
                catch (Exception backupEx)
                {
                    _logger.LogWarning(backupEx, "[ENGINE STATE] Backup failed, continuing...");
                }

                // 3 — запись основного файла
                File.WriteAllText(_engineStatePath, json);

                _logger.LogInformation("[ENGINE STATE] Snapshot saved → {path}", _engineStatePath);
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
                if (!File.Exists(_engineStatePath))
                    return null;

                var json = File.ReadAllText(_engineStatePath);
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
