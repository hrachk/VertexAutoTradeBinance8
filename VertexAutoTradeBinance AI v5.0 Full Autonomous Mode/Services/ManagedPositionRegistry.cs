using System.Collections.Concurrent;
using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Реестр позиций, которыми УПРАВЛЯЕТ бот (IsManagedByBot).
    /// Binance Futures не имеет MagicNumber — ownership только внутренний.
    ///
    /// Правила:
    ///  - Бот открыл → Register (IsManaged=true, Calculated SL/TP)
    ///  - Полное закрытие → Unregister
    ///  - Ручная позиция без записи → IsManaged=false → supervisor НЕ трогает
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

        private readonly ConcurrentDictionary<string, ManagedInfo> _map = new(StringComparer.OrdinalIgnoreCase);

        private static string Key(string symbol, PositionSide side) => $"{symbol}|{side}";

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
        }

        public void RegisterFromSignal(TradeSignal signal, decimal? filledEntry = null)
        {
            var side = signal.Side == SignalSide.Buy ? PositionSide.Long : PositionSide.Short;
            var entry = filledEntry is > 0 ? filledEntry.Value : signal.EntryPrice;
            var tps = new List<decimal>();
            if (signal.TakeProfit is > 0) tps.Add(signal.TakeProfit.Value);
            if (signal.TakeProfits != null) tps.AddRange(signal.TakeProfits.Where(x => x > 0));
            Register(signal.Symbol, side, entry, signal.StopLoss, tps.Distinct());
        }

        public void Unregister(string symbol, PositionSide side) =>
            _map.TryRemove(Key(symbol, side), out _);

        public void UnregisterAll(string symbol)
        {
            foreach (var k in _map.Keys.Where(k => k.StartsWith(symbol + "|", StringComparison.OrdinalIgnoreCase)).ToList())
                _map.TryRemove(k, out _);
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
            if (!_map.TryGetValue(Key(symbol, side), out var info)) return;
            info.CalculatedSL = sl;
            if (tps != null)
                info.CalculatedTPs = tps.Where(x => x > 0).ToList();
        }

        public void MarkRestoreAttempt(string symbol, PositionSide side, bool success)
        {
            if (!_map.TryGetValue(Key(symbol, side), out var info)) return;
            info.LastRestoreTime = DateTime.UtcNow;
            if (success) info.RestoreFailCount = 0;
            else info.RestoreFailCount++;
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
            if (_map.TryGetValue(Key(symbol, side), out var info))
                info.UserClearedProtection = true;
        }
    }
}
