using System.Text.Json;
using System.Text.Json.Serialization;
using VertexAutoTradeBinance8.Models;
using Microsoft.Extensions.Configuration;

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

        private readonly string _filePath;

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            Converters =
            {
                new JsonStringEnumConverter()
            }
        };

        public ExecutedSignalService(
            ILogger<ExecutedSignalService> logger,
            IConfiguration cfg)
        {
            _logger = logger;

            var root = cfg["SharedData:Root"]
                ?? throw new InvalidOperationException("SharedData:Root not configured");

            Directory.CreateDirectory(root);

            _filePath = Path.Combine(AppContext.BaseDirectory, "executed_signals.json");

            EnsureFileExists();
        }

        // ============================================================
        // HELPERS
        // ============================================================

        private void EnsureFileExists()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    File.WriteAllText(_filePath, "[]");
                    _logger.LogInformation("[EXEC] executed_signals.json created at {path}", _filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EXEC] Failed to ensure executed_signals.json exists");
                throw;
            }
        }

        private List<ExecutedSignalRecord> LoadInternal()
        {
            try
            {
                EnsureFileExists();

                string json;

                using var fs = new FileStream(
                    _filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);

                using var sr = new StreamReader(fs);
                json = sr.ReadToEnd();
                if (string.IsNullOrWhiteSpace(json))
                {
                    File.WriteAllText(_filePath, "[]");
                    return new();
                }

                return JsonSerializer.Deserialize<List<ExecutedSignalRecord>>(json, _jsonOptions)
                       ?? new();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EXEC] Failed to load executed_signals.json");

                try
                {
                    File.WriteAllText(_filePath, "[]");
                }
                catch (Exception inner)
                {
                    _logger.LogCritical(inner, "[EXEC] Failed to recover executed_signals.json");
                }

                return new();
            }
        }

        private void SaveInternal(List<ExecutedSignalRecord> list)
        {
            try
            {
                var json = JsonSerializer.Serialize(list, _jsonOptions);
                File.WriteAllText(_filePath, json);

                _logger.LogInformation(
                    "[EXEC] File saved, records={count}, lastTime={time}",
                    list.Count,
                    list.LastOrDefault()?.Time);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EXEC] Failed to save executed_signals.json");
                throw;
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
            }

            ExecutedSignalsChanged?.Invoke();

            _logger.LogInformation(
                "[EXEC][{symbol}] SignalCreated saved (qty={qty}, ntn={ntn:F2})",
                signal.Symbol, qty, notional);

            return record;
        }

        public void UpdateStatus(
         string symbol,
         DateTime time,
         TradeExecutionStatus status,
         decimal? qty = null,
         decimal? notional = null,
         decimal? filledEntry = null,   // ⬅️ НОВОЕ
         decimal? exitPrice = null,
         decimal? pnl = null,
         decimal? roi = null)
        {
            lock (_lock)
            {
                var list = LoadInternal();

                var rec = list.FirstOrDefault(x => x.Symbol == symbol && x.Time == time);

                if (rec == null)
                {
                    _logger.LogWarning(
                        "[EXEC][{symbol}] UpdateStatus: record not found (time={time}, status={status})",
                        symbol, time, status);
                    return;
                }

                rec.Status = status;
                if (qty.HasValue) rec.Qty = qty.Value;
                if (notional.HasValue) rec.Notional = notional.Value;

                //  фактический Price  вход
                if (filledEntry.HasValue)
                    rec.FilledEntryPrice = filledEntry.Value;


                if (exitPrice.HasValue) rec.ExitPrice = exitPrice.Value;
                if (pnl.HasValue) rec.PnL = pnl.Value;
                if (roi.HasValue) rec.RoiPercent = roi.Value;

                SaveInternal(list);
            }

            ExecutedSignalsChanged?.Invoke();

            _logger.LogInformation("[EXEC][{symbol}] Status updated → {status}", symbol, status);
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

        public void UpdateProtectionComputed(
            string symbol,
            DateTime time,
            decimal stopLoss,
            decimal takeProfit,
            decimal atr,
            string tags)
        {
            lock (_lock)
            {
                var list = LoadInternal();
                var rec = list.FirstOrDefault(x =>
                    x.Symbol == symbol &&
                    x.Time == time   // ✅ строгое совпадение
                ); 
 
                if (rec == null)
                {
                    _logger.LogWarning(
                        "[EXEC][{symbol}] ProtectionComputed: record not found (time={time})",
                        symbol, time);
                    return;
                }

                rec.StopLoss = stopLoss;

                if (takeProfit > 0)
                    rec.TakeProfits = new List<decimal> { takeProfit };

                rec.Atr = atr;
                rec.Status = TradeExecutionStatus.ProtectionComputed;

                if (!string.IsNullOrWhiteSpace(tags))
                    rec.Tags = string.IsNullOrWhiteSpace(rec.Tags)
                        ? tags
                        : $"{rec.Tags} | {tags}";

                SaveInternal(list);
            }

            ExecutedSignalsChanged?.Invoke();

            _logger.LogInformation(
                "[EXEC][{symbol}] ProtectionComputed saved → SL={sl}, TP={tp}",
                symbol, stopLoss, takeProfit);
        }
    }
}
