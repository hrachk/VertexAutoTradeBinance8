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
            Path.Combine(AppContext.BaseDirectory, "executed_signals.json");

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

                // На закрытии фиксируем время EXIT (для post-close cooldown)
                if (status == TradeExecutionStatus.PositionClosedTp ||
                    status == TradeExecutionStatus.PositionClosedSl ||
                    status == TradeExecutionStatus.PositionClosedManual)
                {
                    rec.Time = time; // close timestamp
                    _lastCloseUtc[symbol] = time;
                    _lastCloseSide[symbol] = rec.Side;
                    _logger.LogInformation(
                        "[EXEC][{symbol}] CLOSE recorded @ {t:u} side={side} → cooldown (any + same-side)",
                        symbol, time, rec.Side);
                }

                SaveInternal(list);
                ExecutedSignalsChanged?.Invoke();

                _logger.LogInformation("[EXEC][{symbol}] Status updated → {status}", symbol, status);
            }
        }

        // In-memory last close (быстрый путь; файл — для рестарта)
        private readonly Dictionary<string, DateTime> _lastCloseUtc = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SignalSide> _lastCloseSide = new(StringComparer.OrdinalIgnoreCase);

        private ExecutedSignalRecord? FindLastCloseUnlocked(string symbol)
        {
            var list = LoadInternal();
            return list
                .Where(x =>
                    string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
                    (x.Status == TradeExecutionStatus.PositionClosedTp ||
                     x.Status == TradeExecutionStatus.PositionClosedSl ||
                     x.Status == TradeExecutionStatus.PositionClosedManual))
                .OrderByDescending(x => x.Time)
                .FirstOrDefault();
        }

        /// <summary>
        /// true если по символу было полное закрытие меньше чем cooldownMinutes назад (любая сторона).
        /// </summary>
        public bool IsInPostCloseCooldown(string symbol, int cooldownMinutes)
        {
            if (cooldownMinutes <= 0 || string.IsNullOrWhiteSpace(symbol))
                return false;

            var now = DateTime.UtcNow;
            var window = TimeSpan.FromMinutes(cooldownMinutes);

            lock (_lock)
            {
                if (_lastCloseUtc.TryGetValue(symbol, out var mem) && now - mem < window)
                    return true;

                var lastClose = FindLastCloseUnlocked(symbol);
                if (lastClose == null)
                    return false;

                _lastCloseUtc[symbol] = lastClose.Time;
                _lastCloseSide[symbol] = lastClose.Side;
                return now - lastClose.Time < window;
            }
        }

        public DateTime? GetLastCloseUtc(string symbol)
        {
            lock (_lock)
            {
                if (_lastCloseUtc.TryGetValue(symbol, out var mem))
                    return mem;
                return FindLastCloseUnlocked(symbol)?.Time;
            }
        }

        public SignalSide? GetLastCloseSide(string symbol)
        {
            lock (_lock)
            {
                if (_lastCloseSide.TryGetValue(symbol, out var side))
                    return side;
                var last = FindLastCloseUnlocked(symbol);
                if (last == null) return null;
                _lastCloseSide[symbol] = last.Side;
                _lastCloseUtc[symbol] = last.Time;
                return last.Side;
            }
        }

        /// <summary>
        /// Умный re-entry:
        /// - strategy уже решила side по текущему рынку (не «помнит» прошлую сделку);
        /// - короткий rest на любой вход после close;
        /// - длинный rest только на ПОВТОР той же стороны (анти-инерция / revenge same-side).
        /// Противоположный side после короткого rest — можно, если стратегия дала сигнал.
        /// </summary>
        public bool ShouldBlockReentry(
            string symbol,
            SignalSide proposedSide,
            int anySideCooldownMinutes,
            int sameSideCooldownMinutes,
            out string reason)
        {
            reason = "";
            if (string.IsNullOrWhiteSpace(symbol))
                return false;

            var now = DateTime.UtcNow;

            lock (_lock)
            {
                DateTime? closeTime = null;
                SignalSide? closeSide = null;

                if (_lastCloseUtc.TryGetValue(symbol, out var memT))
                {
                    closeTime = memT;
                    if (_lastCloseSide.TryGetValue(symbol, out var memS))
                        closeSide = memS;
                }

                if (closeTime == null || closeSide == null)
                {
                    var last = FindLastCloseUnlocked(symbol);
                    if (last != null)
                    {
                        closeTime = last.Time;
                        closeSide = last.Side;
                        _lastCloseUtc[symbol] = last.Time;
                        _lastCloseSide[symbol] = last.Side;
                    }
                }

                if (closeTime == null)
                    return false;

                var elapsed = now - closeTime.Value;

                // 1) короткий rest на любой re-entry
                if (anySideCooldownMinutes > 0 &&
                    elapsed < TimeSpan.FromMinutes(anySideCooldownMinutes))
                {
                    var left = anySideCooldownMinutes - elapsed.TotalMinutes;
                    reason = $"post-close rest {left:F0}m left (any side)";
                    return true;
                }

                // 2) длинный rest только если стратегия снова предлагает ТУ ЖЕ сторону
                if (sameSideCooldownMinutes > 0 &&
                    closeSide.HasValue &&
                    closeSide.Value == proposedSide &&
                    elapsed < TimeSpan.FromMinutes(sameSideCooldownMinutes))
                {
                    var left = sameSideCooldownMinutes - elapsed.TotalMinutes;
                    reason = $"same-side={proposedSide} blocked {left:F0}m left after last close (need new context/trend)";
                    return true;
                }

                return false;
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
