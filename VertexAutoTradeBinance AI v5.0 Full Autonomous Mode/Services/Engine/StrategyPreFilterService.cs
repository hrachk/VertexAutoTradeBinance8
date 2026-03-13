using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services.Engine
{
    public sealed class StrategyPreFilterService : IStrategyPreFilter
    {
        private readonly ILogger<StrategyPreFilterService> _logger;
        private readonly TradingOptionsResolver _resolver;
        private readonly MarketDataService _market;
        private readonly EngineStateSnapshotService _stateSvc;

        // Configurable thresholds
        private readonly TimeSpan _maxStaleness1m = TimeSpan.FromSeconds(25);
        private readonly TimeSpan _maxStaleness5m = TimeSpan.FromSeconds(75);
        private readonly TimeSpan _maxStaleness15m = TimeSpan.FromSeconds(125);
        private readonly decimal _deadVolatilityThreshold = 0.0015m;
        private readonly decimal _deadRangeThreshold = 0.0010m;
        private readonly TimeSpan _exposureHardBlockSeconds = TimeSpan.FromSeconds(10);

        public StrategyPreFilterService(
            ILogger<StrategyPreFilterService> logger,
            MarketDataService market,
            EngineStateSnapshotService stateSvc,
            TradingOptionsResolver resolver)
        {
            _logger = logger;
            _market = market;
            _stateSvc = stateSvc;
            _resolver = resolver;
        }

        public async Task<PreFilterResult> EvaluateAsync(string symbol, KlineInterval tf, CancellationToken ct)
        {
            var _opt = _resolver.Resolve(symbol);

            // 1) COOL DOWN
            if (_opt.CooldownMinutes > 0)
            {
                var st = _stateSvc.State;
                if (TryIsSymbolInCooldown(st, symbol, TimeSpan.FromMinutes(_opt.CooldownMinutes), out var cdReason))
                    return PreFilterResult.Skip("COOLDOWN", cdReason, sleepMs: 40);
            }

            // 2) MarketSnapshot
            var snap = await _market.GetMarketSnapshot(symbol, tf, ct);
            if (snap == null)
                return PreFilterResult.Skip("NO_SNAPSHOT", "Нет MarketSnapshot", sleepMs: 40);

            // 3) WS latency / stale guard
            if (TryGetSnapshotUtc(snap, out var snapUtc))
            {
                var age = DateTime.UtcNow - snapUtc;

                var max = tf switch
                {
                    KlineInterval.OneMinute => _maxStaleness1m,
                    KlineInterval.FiveMinutes => _maxStaleness5m,
                    KlineInterval.FifteenMinutes => _maxStaleness15m,
                    _ => TimeSpan.FromSeconds(45)
                };

                if (age > max)
                {
                    return PreFilterResult.Skip(
                        "WS_STALE",
                        $"Данные устарели: age={age.TotalSeconds:F0}s > {max.TotalSeconds:F0}s",
                        sleepMs: 60);
                }
            }

            // 4) Dead market guard
            if (TryIsDeadMarket(snap))
                return PreFilterResult.Skip("DEAD_MKT", "Рынок стоит (dead zone)", sleepMs: 60);

            // 5) Lightweight exposure guard
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
                    if (st.LastProtectionUtc.HasValue &&
                        DateTime.UtcNow - st.LastProtectionUtc.Value < _exposureHardBlockSeconds)
                        return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in IsExposureHardBlocked for {symbol}", symbol);
            }

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
                    var last = st.LastProtectionUtc;

                    if (last.HasValue)
                    {
                        var elapsed = DateTime.UtcNow - last.Value;

                        if (elapsed < cd)
                        {
                            reason = $"Cooldown active: {elapsed.TotalMinutes:F1}m < {cd.TotalMinutes:F1}m";
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Логируем, но не блокируем поток
                Console.WriteLine($"TryIsSymbolInCooldown failed: {ex}");
            }

            return false;
        }

        private static bool TryGetSnapshotUtc(object snap, out DateTime utc)
        {
            utc = default;

            try
            {
                var t = snap.GetType();
                DateTime? dt = null;

                var p1 = t.GetProperty("UpdatedUtc") ?? t.GetProperty("TimestampUtc") ?? t.GetProperty("Timestamp");
                if (p1 != null && p1.GetValue(snap) is DateTime d1)
                    dt = d1;

                if (dt == null)
                {
                    var p2 = t.GetProperty("CloseTime") ?? t.GetProperty("CloseTimeUtc");
                    if (p2 != null && p2.GetValue(snap) is DateTime d2)
                        dt = d2;
                }

                if (dt == null)
                    return false;

                utc = DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc);
                return utc != default;
            }
            catch
            {
                return false;
            }
        }

        private bool TryIsDeadMarket(object snap)
        {
            try
            {
                var t = snap.GetType();

                var volP = t.GetProperty("VolatilityPercent")?.GetValue(snap);
                if (volP is decimal vol && vol >= 0 && vol < _deadVolatilityThreshold)
                    return true;

                var rangeP = t.GetProperty("RangePercent")?.GetValue(snap);
                if (rangeP is decimal rp && rp >= 0 && rp < _deadRangeThreshold)
                    return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TryIsDeadMarket reflection failed");
            }

            return false;
        }
    }
}