using System.Text.Json;
using System.Text.Json.Serialization;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Журнал сделок бота (executed_signals.json в общей папке VertexPaths.DataDir).
    ///
    /// v6 fixes:
    ///  • Файл в общей папке — Web UI видит те же сделки, что и бот.
    ///  • У записи есть Id: открытие/закрытие обновляют КОНКРЕТНУЮ сделку,
    ///    а не «последнюю запись символа» (раньше close мог попасть в неисполненный сигнал).
    ///  • Время открытия больше не затирается временем закрытия (ClosedAt отдельно).
    ///  • Кэш в памяти + перечитывание при изменении файла другим процессом.
    ///  • Атомарная запись; размер журнала ограничен.
    /// </summary>
    public class ExecutedSignalService
    {
        private readonly ILogger<ExecutedSignalService> _logger;
        private readonly object _lock = new();
        public static event Action? ExecutedSignalsChanged;

        private const int MaxRecords = 5000;

        private static string FilePath => VertexPaths.ExecutedSignals;

        private List<ExecutedSignalRecord> _cache = new();
        private DateTime _cacheFileStamp = DateTime.MinValue;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public ExecutedSignalService(ILogger<ExecutedSignalService> logger)
        {
            _logger = logger;
            lock (_lock) EnsureLoadedUnlocked();
        }

        // ============================================================
        // STORAGE
        // ============================================================

        private void EnsureLoadedUnlocked()
        {
            try
            {
                var stamp = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
                if (stamp == _cacheFileStamp && (_cache.Count > 0 || stamp == DateTime.MinValue))
                    return;

                var json = VertexPaths.ReadAllTextShared(FilePath);
                _cache = string.IsNullOrWhiteSpace(json)
                    ? new List<ExecutedSignalRecord>()
                    : JsonSerializer.Deserialize<List<ExecutedSignalRecord>>(json, JsonOptions) ?? new();

                // legacy записи без Id
                foreach (var r in _cache.Where(r => string.IsNullOrWhiteSpace(r.Id)))
                    r.Id = Guid.NewGuid().ToString("N");

                _cacheFileStamp = stamp;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EXEC] Failed to load {path} — keeping in-memory journal", FilePath);
            }
        }

        private void SaveUnlocked()
        {
            try
            {
                if (_cache.Count > MaxRecords)
                    _cache = _cache.OrderByDescending(x => x.Time).Take(MaxRecords).ToList();

                VertexPaths.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(_cache, JsonOptions));
                _cacheFileStamp = File.GetLastWriteTimeUtc(FilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EXEC] Failed to save {path}", FilePath);
            }
        }

        private static void RaiseChanged()
        {
            try { ExecutedSignalsChanged?.Invoke(); } catch { /* UI handlers не должны ронять бота */ }
        }

        // ============================================================
        // WRITE API
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
                EnsureLoadedUnlocked();
                _cache.Add(record);
                SaveUnlocked();
            }
            RaiseChanged();

            _logger.LogInformation("[EXEC][{symbol}] SignalCreated id={id} qty={qty} ntn={ntn:F2}",
                signal.Symbol, record.Id, qty, notional);
            return record;
        }

        /// <summary>Обновление конкретной записи по Id.</summary>
        public bool UpdateById(
            string id,
            TradeExecutionStatus status,
            decimal? qty = null,
            decimal? notional = null,
            decimal? entryPrice = null,
            decimal? exitPrice = null,
            decimal? pnl = null,
            decimal? roi = null,
            decimal? fees = null,
            decimal? stopLoss = null,
            List<decimal>? takeProfits = null,
            string? closeReason = null)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;

            lock (_lock)
            {
                EnsureLoadedUnlocked();
                var rec = _cache.FirstOrDefault(x => x.Id == id);
                if (rec == null)
                {
                    _logger.LogWarning("[EXEC] UpdateById: record {id} not found (status={st})", id, status);
                    return false;
                }

                Apply(rec, status, qty, notional, entryPrice, exitPrice, pnl, roi, fees, stopLoss, takeProfits, closeReason);
                SaveUnlocked();
            }
            RaiseChanged();
            return true;
        }

        /// <summary>
        /// Закрытие последней ОТКРЫТОЙ сделки по symbol+side (если Id неизвестен — после рестарта).
        /// </summary>
        public bool CloseLatestOpen(
            string symbol,
            SignalSide side,
            TradeExecutionStatus status,
            decimal exitPrice,
            decimal pnl,
            decimal roi,
            decimal? fees,
            string closeReason)
        {
            lock (_lock)
            {
                EnsureLoadedUnlocked();
                var rec = _cache
                    .Where(x => string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
                                x.Side == side && x.IsOpen)
                    .OrderByDescending(x => x.Time)
                    .FirstOrDefault();
                if (rec == null) return false;

                Apply(rec, status, null, null, null, exitPrice, pnl, roi, fees, null, null, closeReason);
                SaveUnlocked();
            }
            RaiseChanged();
            return true;
        }

        /// <summary>Legacy API: обновление последней записи символа (не используйте для закрытий).</summary>
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
                EnsureLoadedUnlocked();
                var rec = _cache
                    .Where(x => x.Symbol == symbol && x.Time <= time && !x.IsClosed)
                    .OrderByDescending(x => x.Time)
                    .FirstOrDefault();
                if (rec == null)
                {
                    _logger.LogWarning("[EXEC][{symbol}] UpdateStatus: record not found (status={status})", symbol, status);
                    return;
                }
                Apply(rec, status, qty, notional, null, exitPrice, pnl, roi, null, null, null, null);
                SaveUnlocked();
            }
            RaiseChanged();
        }

        private void Apply(
            ExecutedSignalRecord rec,
            TradeExecutionStatus status,
            decimal? qty, decimal? notional, decimal? entryPrice, decimal? exitPrice,
            decimal? pnl, decimal? roi, decimal? fees, decimal? stopLoss, List<decimal>? tps, string? closeReason)
        {
            rec.Status = status;
            if (qty.HasValue) rec.Qty = qty.Value;
            if (notional.HasValue) rec.Notional = notional.Value;
            if (entryPrice is > 0) rec.Entry = entryPrice.Value;
            if (exitPrice.HasValue) rec.ExitPrice = exitPrice.Value;
            if (pnl.HasValue) rec.PnL = pnl.Value;
            if (roi.HasValue) rec.RoiPercent = roi.Value;
            if (fees.HasValue) rec.Fees = fees.Value;
            if (stopLoss is > 0) rec.StopLoss = stopLoss.Value;
            if (tps is { Count: > 0 }) rec.TakeProfits = tps.ToList();

            if (status == TradeExecutionStatus.PositionOpened && rec.OpenedAt == null)
                rec.OpenedAt = DateTime.UtcNow;

            if (rec.IsClosed)
            {
                rec.ClosedAt = DateTime.UtcNow;
                rec.CloseReason = closeReason ?? rec.CloseReason;
                _logger.LogInformation(
                    "[EXEC][{symbol}] CLOSE id={id} side={side} status={st} pnl={pnl:F4} → cooldown armed",
                    rec.Symbol, rec.Id, rec.Side, status, rec.PnL ?? 0);
            }
        }

        // ============================================================
        // READ API
        // ============================================================

        private ExecutedSignalRecord? FindLastCloseUnlocked(string symbol)
        {
            EnsureLoadedUnlocked();
            return _cache
                .Where(x => string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase) && x.IsClosed)
                .OrderByDescending(x => x.EffectiveCloseTime)
                .FirstOrDefault();
        }

        public bool IsInPostCloseCooldown(string symbol, int cooldownMinutes)
        {
            if (cooldownMinutes <= 0 || string.IsNullOrWhiteSpace(symbol)) return false;
            lock (_lock)
            {
                var last = FindLastCloseUnlocked(symbol);
                return last != null && DateTime.UtcNow - last.EffectiveCloseTime < TimeSpan.FromMinutes(cooldownMinutes);
            }
        }

        public DateTime? GetLastCloseUtc(string symbol)
        {
            lock (_lock) return FindLastCloseUnlocked(symbol)?.EffectiveCloseTime;
        }

        public SignalSide? GetLastCloseSide(string symbol)
        {
            lock (_lock) return FindLastCloseUnlocked(symbol)?.Side;
        }

        /// <summary>
        /// Re-entry policy:
        ///  - короткий отдых после закрытия на любую сторону;
        ///  - длинный отдых на ПОВТОР той же стороны;
        ///  - после убыточного закрытия (SL) отдых удваивается (анти-revenge).
        /// </summary>
        public bool ShouldBlockReentry(
            string symbol,
            SignalSide proposedSide,
            int anySideCooldownMinutes,
            int sameSideCooldownMinutes,
            out string reason)
        {
            reason = "";
            if (string.IsNullOrWhiteSpace(symbol)) return false;

            lock (_lock)
            {
                var last = FindLastCloseUnlocked(symbol);
                if (last == null) return false;

                var elapsed = DateTime.UtcNow - last.EffectiveCloseTime;
                bool wasLoss = (last.PnL ?? 0) < 0;
                int anyMin = wasLoss ? anySideCooldownMinutes * 2 : anySideCooldownMinutes;

                if (anyMin > 0 && elapsed < TimeSpan.FromMinutes(anyMin))
                {
                    reason = $"post-close rest {anyMin - elapsed.TotalMinutes:F0}m left (any side{(wasLoss ? ", after loss ×2" : "")})";
                    return true;
                }

                if (sameSideCooldownMinutes > 0 && last.Side == proposedSide &&
                    elapsed < TimeSpan.FromMinutes(sameSideCooldownMinutes))
                {
                    reason = $"same-side={proposedSide} blocked {sameSideCooldownMinutes - elapsed.TotalMinutes:F0}m left";
                    return true;
                }
                return false;
            }
        }

        /// <summary>Реализованный PnL за текущие сутки UTC (для дневного стопа).</summary>
        public decimal GetRealizedPnlTodayUtc()
        {
            var today = DateTime.UtcNow.Date;
            lock (_lock)
            {
                EnsureLoadedUnlocked();
                return _cache.Where(x => x.IsClosed && x.EffectiveCloseTime >= today).Sum(x => x.PnL ?? 0m);
            }
        }

        public List<ExecutedSignalRecord> GetAll()
        {
            lock (_lock)
            {
                EnsureLoadedUnlocked();
                return _cache.OrderByDescending(x => x.Time).ToList();
            }
        }
    }
}
