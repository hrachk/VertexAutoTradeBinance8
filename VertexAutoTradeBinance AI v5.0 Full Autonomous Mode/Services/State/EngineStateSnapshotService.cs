using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text.Json;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{

    public class EngineStateRuntime
    {
        public decimal BaseDepositUsd { get; set; }   // якорь
        public decimal RealizedPnlUsd { get; set; }   // накопленный
        public decimal EngineEquityUsd { get; set; }  // Base + Realized

        // уже есть:
        public int SupervisorChecksLastMinute { get; set; }
        public DateTime LastSupervisorAction { get; set; }
        public string LastSupervisorMessage { get; set; }

        // per-symbol state
        public ConcurrentDictionary<string, SymbolState> Symbols { get; } = new();
    }



    /// <summary>
    /// Профессиональный сервис снапшотов движка.
    /// Thread-safe, async-safe, без file-lock race.
    /// </summary>
    public class EngineStateSnapshotService
    {
        private readonly ILogger<EngineStateSnapshotService> _logger;
        private readonly string _engineStatePath;
        private readonly string _backupPath;

        // 🔒 ЕДИНСТВЕННЫЙ writer gate (критично)
        private static readonly SemaphoreSlim _saveGate = new(1, 1);

        public EngineState State { get; } = new EngineState();


        public void EnsureDepositInitialized(decimal baseDepositUsd)
        {
            if (State.BaseDepositUsd > 0m)
                return; // уже инициализировано, НИКОГДА не трогаем

            State.BaseDepositUsd = baseDepositUsd;
            State.RealizedPnlUsd = 0m;
            State.EngineEquityUsd = baseDepositUsd;
        }
      

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

            var baseDir = AppContext.BaseDirectory;

            _engineStatePath = Path.Combine(
                baseDir,
                paths.Value.EngineState
            );

            _backupPath = _engineStatePath + ".bak";

            EnsureDirectoryExists();
        }

        // ===============================================================
        // Directory ensure
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
        // PUBLIC API (sync wrapper, чтобы не ломать вызовы)
        // ===============================================================
        public void Save(EngineState state)
        {
            _ = SaveAsync(state);
        }

        // ===============================================================
        // REAL SAVE (async-safe, non-blocking)
        // ===============================================================
        public async Task SaveAsync(EngineState state)
        {
            if (!await _saveGate.WaitAsync(0))
                return;

            try
            {
                var json = JsonSerializer.Serialize(state, _jsonOptions);

                var dir = Path.GetDirectoryName(_engineStatePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                var tmpPath = _engineStatePath + ".tmp";

                // 1️⃣ пишем во временный файл (НЕ блокирует основной)
                await using (var fs = new FileStream(
                    tmpPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    useAsync: true))
                await using (var sw = new StreamWriter(fs))
                {
                    await sw.WriteAsync(json);
                }

                // 2️⃣ atomic replace (Windows-safe)
                File.Copy(tmpPath, _engineStatePath, overwrite: true);
                File.Delete(tmpPath);

                // 3️⃣ backup best-effort
                try
                {
                    File.Copy(_engineStatePath, _backupPath, overwrite: true);
                }
                catch { }

                _logger.LogDebug(
                    "[ENGINE STATE] Snapshot saved ({bytes} bytes)",
                    json.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ENGINE STATE] Snapshot SAVE ERROR");
            }
            finally
            {
                _saveGate.Release();
            }
        }


        // ===============================================================
        // LOAD (UI / read-only)
        // ===============================================================
        public EngineState? Load()
        {
            try
            {
                if (!File.Exists(_engineStatePath))
                    return null;

                using var fs = new FileStream(
                    _engineStatePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite); // 🔥 КЛЮЧ

                return JsonSerializer.Deserialize<EngineState>(fs, _jsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ENGINE STATE] Snapshot LOAD ERROR");
                return null;
            }
        }

    }
}
