using VertexAutoTradeBinance8.Services.Storage;
using System.Text.Json;
using System.Text.Json.Serialization;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// ExecutedSignalService:
    ///  - пишет/читает executed_signals.json
    ///  - используется Engine / OrderExecutor / PositionSupervisor
    ///  - UI читает файл и рисует карточки
    /// </summary>
    public class ExecutedSignalService
    {
        private readonly ILogger<ExecutedSignalService> _logger;
        private readonly object _lock = new();
        public static event Action? ExecutedSignalsChanged;

        private static readonly string FilePath =
           new VertexPaths().ExecutedSignals;
        //private static readonly string FilePath = @"C:\Users\karap\source\repos\VertexAutoTradeBinance8\VertexAutoTradeBinance AI v5.0 Full Autonomous Mode\bin\Debug\net8.0\executed_signals.json";
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            Converters =
            {
                new JsonStringEnumConverter()
            }
        };

        public ExecutedSignalService(ILogger<ExecutedSignalService> logger)
        {
            _logger = logger;
            EnsureFileExists();
        }

        // ============================================================
        // HELPERS
        // ============================================================

        /// <summary>
        /// Создание файла и директории, если их нет.
        /// </summary>
        private void EnsureFileExists()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                if (!File.Exists(FilePath))
                {
                    File.WriteAllText(FilePath, "[]");
                    _logger.LogInformation("[EXEC] executed_signals.json created");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EXEC] Failed to ensure executed_signals.json exists");
            }
        }

        private List<ExecutedSignalRecord> LoadInternal()
        {
            try
            {
                EnsureFileExists();

                var json = File.ReadAllText(FilePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    File.WriteAllText(FilePath, "[]");
                    return new();
                }

                return JsonSerializer.Deserialize<List<ExecutedSignalRecord>>(json, _jsonOptions)
                       ?? new();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EXEC] Failed to load executed_signals.json");

                // Пытаемся пересоздать безопасный файл
                try
                {
                    File.WriteAllText(FilePath, "[]");
                }
                catch { }

                return new();
            }
        }

        private void SaveInternal(List<ExecutedSignalRecord> list)
        {
            try
            {
                var json = JsonSerializer.Serialize(list, _jsonOptions);
                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EXEC] Failed to save executed_signals.json");
            }
        }

        // ============================================================
        // API
        // ============================================================

        public ExecutedSignalRecord AddSignalCreated(
            TradeSignal signal,
            int opportunityScore,
            decimal atr,
            decimal volatility,
            decimal slope,
            decimal qty,
            decimal notional,
            string tags = "")
        {
            var record = new ExecutedSignalRecord
            {
                Symbol = signal.Symbol,
                Time = DateTime.UtcNow,
                Side = signal.Side,
                Entry = signal.EntryPrice,
                StopLoss = signal.StopLoss,
                TakeProfits = signal.TakeProfits?.ToList() ?? new(),
                Qty = qty,
                Notional = notional,
                Status = TradeExecutionStatus.SignalCreated,
                Reason = signal.Reason ?? string.Empty,
                OpportunityScore = opportunityScore,
                Atr = atr,
                Volatility = volatility,
                Slope = slope,
                Tags = tags
            };

            lock (_lock)
            {
                var list = LoadInternal();
                list.Add(record);
                SaveInternal(list);
                ExecutedSignalsChanged?.Invoke();


            }

            _logger.LogInformation("[EXEC][{symbol}] SignalCreated saved (qty={qty}, ntn={ntn:F2})",
                signal.Symbol, qty, notional);

            return record;
        }

        public void UpdateStatus(
            string symbol,
            DateTime time,
            TradeExecutionStatus status,
            decimal? qty = null,
            decimal? notional = null,
            decimal? exitPrice = null,
            decimal? pnl = null,
            decimal? roi = null)
        {
            lock (_lock)
            {
                var list = LoadInternal();

                var rec = list
                    .Where(x => x.Symbol == symbol && x.Time <= time)
                    .OrderByDescending(x => x.Time)
                    .FirstOrDefault();

                if (rec == null)
                {
                    _logger.LogWarning("[EXEC][{symbol}] UpdateStatus: record not found (status={status})",
                        symbol, status);
                    return;
                }

                rec.Status = status;

                if (qty.HasValue) rec.Qty = qty.Value;
                if (notional.HasValue) rec.Notional = notional.Value;
                if (exitPrice.HasValue) rec.ExitPrice = exitPrice.Value;
                if (pnl.HasValue) rec.PnL = pnl.Value;
                if (roi.HasValue) rec.RoiPercent = roi.Value;

                SaveInternal(list);
                ExecutedSignalsChanged?.Invoke();

                _logger.LogInformation("[EXEC][{symbol}] Status updated → {status}", symbol, status);
            }
        }

        public List<ExecutedSignalRecord> GetAll()
        {
            lock (_lock)
            {
                return LoadInternal()
                    .OrderByDescending(x => x.Time)
                    .ToList();
            }
        }
    }
}
