using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Binance.Net.Enums;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Реестр позиций, которыми УПРАВЛЯЕТ бот (IsManagedByBot).
    /// Persist: managed_positions.json (переживает рестарт).
    /// Binance Futures не имеет MagicNumber — ownership только внутренний.
    /// </summary>
    public class ManagedPositionRegistry
    {
        public class ManagedInfo
        {
            public string Symbol { get; set; } = "";
            public PositionSide Side { get; set; }
            public decimal EntryPrice { get; set; }
            public decimal CalculatedSL { get; set; }
            public List<decimal> CalculatedTPs { get; set; } = new();
            public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
            public DateTime? LastRestoreTime { get; set; }
            public int RestoreFailCount { get; set; }
            public bool AllowManualOverride { get; set; } = false;
            public bool UserClearedProtection { get; set; } = false;
        }

        private readonly ConcurrentDictionary<string, ManagedInfo> _map =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly ILogger<ManagedPositionRegistry> _logger;
        private readonly object _ioLock = new();

        private static readonly string FilePath =
            Path.Combine(AppContext.BaseDirectory, "managed_positions.json");

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        public ManagedPositionRegistry(ILogger<ManagedPositionRegistry> logger)
        {
            _logger = logger;
            LoadFromDisk();
        }

        private static string Key(string symbol, PositionSide side) => $"{symbol}|{side}";

        // ----------------------------------------------------------------
        // Persistence
        // ----------------------------------------------------------------

        private void EnsureFileExists()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                if (!File.Exists(FilePath))
                    File.WriteAllText(FilePath, "[]");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MANAGED] Failed to ensure managed_positions.json");
            }
        }

        private void LoadFromDisk()
        {
            lock (_ioLock)
            {
                try
                {
                    EnsureFileExists();
                    var json = File.ReadAllText(FilePath);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        File.WriteAllText(FilePath, "[]");
                        return;
                    }

                    var list = JsonSerializer.Deserialize<List<ManagedInfo>>(json, _jsonOptions)
                               ?? new List<ManagedInfo>();

                    _map.Clear();
                    foreach (var item in list)
                    {
                        if (string.IsNullOrWhiteSpace(item.Symbol))
                            continue;
                        _map[Key(item.Symbol, item.Side)] = item;
                    }

                    _logger.LogInformation(
                        "[MANAGED] Loaded {n} position(s) from managed_positions.json",
                        _map.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[MANAGED] Failed to load managed_positions.json — starting empty");
                    try { File.WriteAllText(FilePath, "[]"); } catch { }
                }
            }
        }

        private void SaveToDisk()
        {
            lock (_ioLock)
            {
                try
                {
                    EnsureFileExists();
                    var list = _map.Values
                        .OrderBy(x => x.Symbol)
                        .ThenBy(x => x.Side.ToString())
                        .ToList();

                    var json = JsonSerializer.Serialize(list, _jsonOptions);
                    var tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, json);

                    // atomic-ish replace
                    if (File.Exists(FilePath))
                        File.Delete(FilePath);
                    File.Move(tmp, FilePath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[MANAGED] Failed to save managed_positions.json");
                    try
                    {
                        // fallback non-atomic
                        var list = _map.Values.ToList();
                        File.WriteAllText(FilePath, JsonSerializer.Serialize(list, _jsonOptions));
                    }
                    catch { }
                }
            }
        }

        /// <summary>Принудительная перезагрузка с диска (диагностика / hot-reload).</summary>
        public void Reload() => LoadFromDisk();

        /// <summary>Снимок всех managed позиций (для UI / debug).</summary>
        public IReadOnlyList<ManagedInfo> Snapshot() =>
            _map.Values.OrderBy(x => x.Symbol).ToList();

        // ----------------------------------------------------------------
        // API
        // ----------------------------------------------------------------

        public void Register(
            string symbol,
            PositionSide side,
            decimal entry,
            decimal calculatedSl,
            IEnumerable<decimal>? tps)
        {
            _map[Key(symbol, side)] = new ManagedInfo
            {
                Symbol = symbol,
                Side = side,
                EntryPrice = entry,
                CalculatedSL = calculatedSl,
                CalculatedTPs = tps?.Where(x => x > 0).ToList() ?? new List<decimal>(),
                OpenedAt = DateTime.UtcNow,
                LastRestoreTime = null,
                RestoreFailCount = 0,
                AllowManualOverride = false,
                UserClearedProtection = false
            };

            SaveToDisk();
            _logger.LogInformation(
                "[MANAGED] Registered {symbol} {side} entry={entry} SL={sl}",
                symbol, side, entry, calculatedSl);
        }

        public void RegisterFromSignal(TradeSignal signal, decimal? filledEntry = null)
        {
            var side = signal.Side == SignalSide.Buy ? PositionSide.Long : PositionSide.Short;
            var entry = filledEntry is > 0 ? filledEntry.Value : signal.EntryPrice;
            var tps = new List<decimal>();
            if (signal.TakeProfit is > 0) tps.Add(signal.TakeProfit.Value);
            if (signal.TakeProfits != null)
                tps.AddRange(signal.TakeProfits.Where(x => x > 0));
            Register(signal.Symbol, side, entry, signal.StopLoss, tps.Distinct());
        }

        public void Unregister(string symbol, PositionSide side)
        {
            if (_map.TryRemove(Key(symbol, side), out _))
            {
                SaveToDisk();
                _logger.LogInformation("[MANAGED] Unregistered {symbol} {side}", symbol, side);
            }
        }

        public void UnregisterAll(string symbol)
        {
            var removed = false;
            foreach (var k in _map.Keys
                         .Where(k => k.StartsWith(symbol + "|", StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                if (_map.TryRemove(k, out _))
                    removed = true;
            }

            if (removed)
            {
                SaveToDisk();
                _logger.LogInformation("[MANAGED] Unregistered all sides for {symbol}", symbol);
            }
        }

        public bool IsManaged(string symbol, PositionSide side) =>
            _map.ContainsKey(Key(symbol, side));

        public bool IsManagedAny(string symbol) =>
            _map.Keys.Any(k => k.StartsWith(symbol + "|", StringComparison.OrdinalIgnoreCase));

        public ManagedInfo? Get(string symbol, PositionSide side)
        {
            _map.TryGetValue(Key(symbol, side), out var info);
            return info;
        }

        public void UpdateCalculated(string symbol, PositionSide side, decimal sl, IEnumerable<decimal>? tps)
        {
            if (!_map.TryGetValue(Key(symbol, side), out var info))
                return;

            info.CalculatedSL = sl;
            if (tps != null)
                info.CalculatedTPs = tps.Where(x => x > 0).ToList();

            SaveToDisk();
        }

        public void MarkRestoreAttempt(string symbol, PositionSide side, bool success)
        {
            if (!_map.TryGetValue(Key(symbol, side), out var info))
                return;

            info.LastRestoreTime = DateTime.UtcNow;
            if (success) info.RestoreFailCount = 0;
            else info.RestoreFailCount++;

            SaveToDisk();
        }

        /// <summary>Если restore падал N раз подряд — пауза, чтобы не спамить биржу.</summary>
        public bool CanAttemptRestore(string symbol, PositionSide side, int maxFails = 5, int pauseMinutes = 10)
        {
            var info = Get(symbol, side);
            if (info == null) return false;
            if (info.AllowManualOverride || info.UserClearedProtection) return false;
            if (info.RestoreFailCount < maxFails) return true;
            if (info.LastRestoreTime == null) return true;
            return DateTime.UtcNow - info.LastRestoreTime.Value > TimeSpan.FromMinutes(pauseMinutes);
        }

        public void MarkUserClearedProtection(string symbol, PositionSide side)
        {
            if (!_map.TryGetValue(Key(symbol, side), out var info))
                return;

            info.UserClearedProtection = true;
            SaveToDisk();
            _logger.LogInformation(
                "[MANAGED] UserClearedProtection set for {symbol} {side}",
                symbol, side);
        }

        public void SetAllowManualOverride(string symbol, PositionSide side, bool allow)
        {
            if (!_map.TryGetValue(Key(symbol, side), out var info))
                return;

            info.AllowManualOverride = allow;
            SaveToDisk();
        }
    }
}
