using Binance.Net.Enums;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services.Engine
{
    public sealed class StrategyPreFilterService : IStrategyPreFilter
    {
        private readonly ILogger<StrategyPreFilterService> _logger;
        private readonly TradingOptions _opt;
        private readonly MarketDataService _market;
        private readonly EngineStateSnapshotService _stateSvc;

        // WS latency guard (если нет новых данных — не гоняем стратегию)
        private readonly TimeSpan _maxStaleness1m = TimeSpan.FromSeconds(25);
        private readonly TimeSpan _maxStaleness5m = TimeSpan.FromSeconds(75);

        public StrategyPreFilterService(
            ILogger<StrategyPreFilterService> logger,
            IOptions<TradingOptions> opt,
            MarketDataService market,
            EngineStateSnapshotService stateSvc)
        {
            _logger = logger;
            _opt = opt.Value;
            _market = market;
            _stateSvc = stateSvc;
        }

        public async Task<PreFilterResult> EvaluateAsync(string symbol, KlineInterval tf, CancellationToken ct)
        {
            // 1) COOL DOWN (глобальный, если включён в TradingOptions)
            // TradingWorker уже делает InCooldown(symbol) после signal,
            // но здесь — PRE: чтобы не грузить систему.
            if (_opt.CooldownMinutes > 0)
            {
                var st = _stateSvc.State; // общий стейт
                if (TryIsSymbolInCooldown(st, symbol, TimeSpan.FromMinutes(_opt.CooldownMinutes), out var cdReason))
                    return PreFilterResult.Skip("COOLDOWN", cdReason, sleepMs: 40);
            }

            // 2) MarketSnapshot: если нет снапшота — пропускаем (не зависаем)
            var snap = await _market.GetMarketSnapshot(symbol, tf, ct);
            if (snap == null)
                return PreFilterResult.Skip("NO_SNAPSHOT", "Нет MarketSnapshot", sleepMs: 40);

            // 3) WS latency / stale data guard
            // В твоей модели снапшота может быть любое поле времени.
            // Ниже — максимально безопасная проверка:
            // - если есть Timestamp/UpdatedUtc/CloseTime — используем
            // - если нет — допускаем работу (чтобы не заблокировать торговлю)
            if (TryGetSnapshotUtc(snap, out var snapUtc))
            {
                var age = DateTime.UtcNow - snapUtc;

                var max = tf switch
                {
                    KlineInterval.OneMinute => _maxStaleness1m,
                    KlineInterval.FiveMinutes => _maxStaleness5m,
                    _ => TimeSpan.FromSeconds(90)
                };

                if (age > max)
                {
                    return PreFilterResult.Skip(
                        "WS_STALE",
                        $"Данные устарели: age={age.TotalSeconds:F0}s > {max.TotalSeconds:F0}s",
                        sleepMs: 60);
                }
            }

            // 4) Fast market dead-zone guard (микро-рынок/тишина):
            // Если волатильность микро и рынок стоит, смысла гонять стратегию нет.
            // (адаптируй поля снапшота под твою модель)
            if (TryIsDeadMarket(snap))
                return PreFilterResult.Skip("DEAD_MKT", "Рынок стоит (dead zone)", sleepMs: 60);

            // 5) Lightweight exposure guard (только по EngineState):
            // В StrategyEngine ты уже вызываешь CanIncreaseExposure(...),
            // здесь — грубая защита от циклического добавления риска.
            if (IsExposureHardBlocked(symbol))
                return PreFilterResult.Skip("EXPO_HARD", "Exposure hard block (engine state)", sleepMs: 80);

            return PreFilterResult.Ok("PreFilters OK");
        }

        // -------------------------
        // helpers
        // -------------------------

        private bool IsExposureHardBlocked(string symbol)
        {
            try
            {
                var key = EngineState.Key(symbol);
                if (_stateSvc.State.Symbols.TryGetValue(key, out var st))
                {
                    // пример: после частичных закрытий/защит — не доливаем сразу
                    if (DateTime.UtcNow - st.LastProtectionUtc < TimeSpan.FromSeconds(10))
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static bool TryIsSymbolInCooldown(EngineState state, string symbol, TimeSpan cd, out string reason)
        {
            reason = string.Empty;
            try
            {
                var key = EngineState.Key(symbol);
                if (state.Symbols.TryGetValue(key, out var st))
                {
                    // если у тебя есть поле LastStopUtc/LastTradeUtc — используй его.
                    // Здесь — универсально: cooldown от LastProtectionUtc как safety fallback.
                    var last = st.LastProtectionUtc;
                    if (last != default && (DateTime.UtcNow - last) < cd)
                    {
                        reason = $"Cooldown active: {(DateTime.UtcNow - last).TotalMinutes:F1}m < {cd.TotalMinutes:F1}m";
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool TryGetSnapshotUtc(object snap, out DateTime utc)
        {
            utc = default;

            // Пытаемся вытащить самые типичные поля рефлексией,
            // чтобы не ломать твою модель MarketSnapshot.
            var t = snap.GetType();

            DateTime? dt = null;

            var p1 = t.GetProperty("UpdatedUtc") ?? t.GetProperty("TimestampUtc") ?? t.GetProperty("Timestamp");
            if (p1 != null && p1.PropertyType == typeof(DateTime))
                dt = (DateTime)p1.GetValue(snap)!;

            if (dt == null)
            {
                var p2 = t.GetProperty("CloseTime") ?? t.GetProperty("CloseTimeUtc");
                if (p2 != null && p2.PropertyType == typeof(DateTime))
                    dt = (DateTime)p2.GetValue(snap)!;
            }

            if (dt == null)
                return false;

            utc = DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc);
            return utc != default;
        }

        private static bool TryIsDeadMarket(object snap)
        {
            // Безопасно: если полей нет — не блокируем.
            try
            {
                var t = snap.GetType();

                // Примеры: VolatilityPercent / SpreadPercent / RangePercent
                var volP = t.GetProperty("VolatilityPercent")?.GetValue(snap);
                if (volP is decimal vol && vol >= 0 && vol < 0.0015m)
                    return true;

                var rangeP = t.GetProperty("RangePercent")?.GetValue(snap);
                if (rangeP is decimal rp && rp >= 0 && rp < 0.0010m)
                    return true;
            }
            catch { }

            return false;
        }
    }
}
