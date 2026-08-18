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

        // Disk I/O throttle: in-memory State always updates; file max ~1/sec
        // (was writing on EVERY symbol every tick → blocked worker + more kline timeouts)
        private readonly object _ioLock = new();
        private DateTime _lastPersistUtc = DateTime.MinValue;
        private DateTime _lastBackupUtc = DateTime.MinValue;
        private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(1.5);
        private static readonly TimeSpan BackupInterval = TimeSpan.FromSeconds(30);

        public EngineStateSnapshotService(
     ILogger<EngineStateSnapshotService> logger,
     IOptions<EngineStateSettings> options)
        {
            _logger = logger;

            // Config SnapshotPath if set, otherwise shared C:\VertexShared\engine_state.json
            var configured = options?.Value?.SnapshotPath;
            _path = !string.IsNullOrWhiteSpace(configured)
                ? configured!
                : SharedDataPaths.EngineStateJson;

            _backupPath = _path + ".bak";

            _logger.LogInformation("[ENGINE STATE] Using path → {path}", _path);
            EnsureFileExists();
        }

        // ===============================================================
        // Создать папку и пустой engine_state.json, если нет
        // ===============================================================
        private void EnsureFileExists()
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    _logger.LogInformation("[ENGINE STATE] Created directory: {dir}", dir);
                }

                if (!File.Exists(_path))
                {
                    // minimal valid empty state so UI / readers never see missing file
                    var empty = JsonSerializer.Serialize(new EngineState(), _jsonOptions);
                    File.WriteAllText(_path, empty);
                    _logger.LogInformation("[ENGINE STATE] engine_state.json created → {path}", _path);
                }
                else
                {
                    _logger.LogInformation("[ENGINE STATE] engine_state.json ready → {path}", _path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ENGINE STATE] Failed to ensure engine_state.json exists at {path}", _path);
            }
        }

        // ===============================================================
        // Apply UI snapshot fields onto LIVE State (do NOT wipe Symbols)
        // then persist the live State. Supervisor/exposure use State.Symbols.
        // ===============================================================
        public void Save(EngineState snapshot)
        {
            try
            {
                // Merge "current symbol analysis" into the shared live state
                State.Status = string.IsNullOrWhiteSpace(snapshot.Status) ? "Running" : snapshot.Status;
                State.Mode = snapshot.Mode;
                State.BalanceUsdt = snapshot.BalanceUsdt;
                State.Symbol = snapshot.Symbol;
                State.Timeframe = snapshot.Timeframe;
                State.MarketRegime = snapshot.MarketRegime;
                State.SmartRegime = snapshot.SmartRegime;
                State.Slope = snapshot.Slope;
                State.Volatility = snapshot.Volatility;
                State.Confidence = snapshot.Confidence;
                State.LiquidityDanger = snapshot.LiquidityDanger;
                State.LiquidityReason = snapshot.LiquidityReason;
                State.SoftEntry = snapshot.SoftEntry;
                State.BlockedByLiquidity = snapshot.BlockedByLiquidity;
                State.LastUpdate = snapshot.LastUpdate != default ? snapshot.LastUpdate : DateTime.UtcNow;

                if (snapshot.EquityUsd != 0) State.EquityUsd = snapshot.EquityUsd;
                if (snapshot.UsedMarginUsd != 0) State.UsedMarginUsd = snapshot.UsedMarginUsd;

                // Keep SymbolState entries from supervisor; never replace the dictionary.
                // Memory always up to date; disk throttled so worker is not blocked.
                PersistLiveState(force: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ENGINE STATE] Snapshot SAVE ERROR");
            }
        }

        /// <summary>
        /// Write live State to disk (atomic). Throttled unless force=true.
        /// </summary>
        public void PersistLiveState(bool force = false)
        {
            lock (_ioLock)
            {
                var now = DateTime.UtcNow;
                if (!force && now - _lastPersistUtc < PersistInterval)
                    return;

                try
                {
                    // Serialize UI fields only (skip ConcurrentDictionary Symbols — supervisor keeps them in RAM)
                    var dto = new EngineState
                    {
                        Status = State.Status,
                        Mode = State.Mode,
                        BalanceUsdt = State.BalanceUsdt,
                        Symbol = State.Symbol,
                        Timeframe = State.Timeframe,
                        MarketRegime = State.MarketRegime,
                        SmartRegime = State.SmartRegime,
                        Slope = State.Slope,
                        Volatility = State.Volatility,
                        Confidence = State.Confidence,
                        LiquidityDanger = State.LiquidityDanger,
                        LiquidityReason = State.LiquidityReason,
                        SoftEntry = State.SoftEntry,
                        BlockedByLiquidity = State.BlockedByLiquidity,
                        LastUpdate = State.LastUpdate,
                        EquityUsd = State.EquityUsd,
                        UsedMarginUsd = State.UsedMarginUsd,
                        Symbols = new System.Collections.Concurrent.ConcurrentDictionary<string, SymbolState>()
                    };

                    var json = JsonSerializer.Serialize(dto, _jsonOptions);

                    if (force || now - _lastBackupUtc >= BackupInterval)
                    {
                        try
                        {
                            if (File.Exists(_path))
                                File.Copy(_path, _backupPath, overwrite: true);
                            _lastBackupUtc = now;
                        }
                        catch (Exception backupEx)
                        {
                            _logger.LogWarning(backupEx, "[ENGINE STATE] Backup failed, continuing...");
                        }
                    }

                    var tmp = _path + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, _path, overwrite: true);
                    _lastPersistUtc = now;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ENGINE STATE] PersistLiveState ERROR → {path}", _path);
                }
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
