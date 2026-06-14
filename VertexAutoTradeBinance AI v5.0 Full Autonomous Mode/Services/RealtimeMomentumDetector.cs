using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Services.MarketData;

namespace VertexAutoTradeBinance8.Services
{
    // =====================================================
    // RealtimeMomentumDetector
    //
    // EVENT-DRIVEN детектор начала тренда в реальном времени.
    //
    // Проблема polling подхода:
    //   Анализ закрытых 5M свечей → запаздывание до 5 минут
    //   Тренд начался → бот узнает только через 5 мин
    //
    // Решение:
    //   1) Подписка на WS @kline_1m (уже есть через WsKlineSubscriber)
    //   2) Анализируем НЕЗАКРЫТЫЕ 1M свечи в реальном времени
    //   3) При обнаружении импульса → триггер для StrategyEngine
    //   4) StrategyEngine запускается немедленно, не ждёт следующего цикла
    //
    // Условия триггера (все три должны выполниться):
    //   A) Momentum: движение 1M свечи > ATR1M × threshold
    //   B) Direction: 3 из последних 4 свечей в одном направлении
    //   C) Volume surge: объём текущей свечи > avg(20) × 1.5
    //
    // Cooldown: после триггера символ блокируется на 60 сек
    //   (не спамим StrategyEngine)
    // =====================================================
    public sealed class RealtimeMomentumDetector
    {
        private readonly ILogger<RealtimeMomentumDetector> _logger;
        private readonly MarketDataFacade _marketData;

        // Cooldown после триггера
        private readonly ConcurrentDictionary<string, DateTime> _lastTrigger =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly TimeSpan TriggerCooldown = TimeSpan.FromSeconds(30); // было 60s

        // Пороги
        private const decimal MomentumThreshold = 0.35m;   // было 0.4 → ловим чуть раньше
        private const decimal VolumeSurgeRatio   = 1.3m;   // было 1.5 → менее строго
        private const int     DirectionBars      = 4;

        public event Action<string, MomentumSignal>? MomentumDetected;

        public sealed class MomentumSignal
        {
            public string   Symbol      { get; init; } = string.Empty;
            public bool     IsLong      { get; init; }
            public decimal  Strength    { get; init; }  // движение / ATR
            public decimal  VolumeMult  { get; init; }  // объём / avg
            public decimal  Price       { get; init; }
            public decimal  Atr1M       { get; init; }
            public DateTime DetectedAt  { get; init; } = DateTime.UtcNow;
            public string   Reason      { get; init; } = string.Empty;
        }

        public RealtimeMomentumDetector(
            ILogger<RealtimeMomentumDetector> logger,
            MarketDataFacade marketData)
        {
            _logger     = logger;
            _marketData = marketData;
        }

        // =====================================================
        // OnKline1MUpdate — вызывается из WsKlineSubscriber
        // при каждом обновлении 1M свечи (каждые ~1-2 сек)
        // =====================================================
        // Вызывается при каждом WS обновлении 1M свечи
        public void OnKlineUpdate(
            string  symbol,
            decimal openPrice,
            decimal highPrice,
            decimal lowPrice,
            decimal closePrice,
            decimal volume,
            bool    isFinal,
            KlineInterval interval)
        {
            try
            {
                // Обрабатываем только 1M таймфрейм
                if (interval != KlineInterval.OneMinute)
                    return;

                // Проверяем cooldown
                if (_lastTrigger.TryGetValue(symbol, out var last) &&
                    DateTime.UtcNow - last < TriggerCooldown)
                    return;

                // Движение текущей свечи
                decimal candleMove = Math.Abs(closePrice - openPrice);
                bool    isGreen    = closePrice > openPrice;

                // Получаем буфер 1M свечей для ATR и volume
                var klines1m = _marketData.GetCachedKlines(symbol, KlineInterval.OneMinute);

                if (klines1m == null || klines1m.Count < 20)
                    return;

                // ATR(14) на 1M
                decimal atr1m = CalcAtr(klines1m, 14);
                if (atr1m <= 0) return;

                // Порог импульса
                decimal moveRatio = candleMove / atr1m;
                if (moveRatio < MomentumThreshold) return;

                // Volume surge
                decimal avgVol = klines1m
                    .TakeLast(20)
                    .Average(c => c.Volume);
                decimal volumeMult = avgVol > 0
                    ? volume / avgVol
                    : 1m;

                if (volumeMult < VolumeSurgeRatio) return;

                // Direction check: 3 из последних 4 свечей в том же направлении
                int last4 = klines1m.Count - 1;
                int sameDir = 0;
                for (int i = Math.Max(0, last4 - DirectionBars + 1); i <= last4; i++)
                {
                    bool barGreen = klines1m[i].ClosePrice > klines1m[i].OpenPrice;
                    if (barGreen == isGreen) sameDir++;
                }

                if (sameDir < 3) return;

                // =====================================================
                // ТРИГГЕР — все условия выполнены
                // =====================================================
                _lastTrigger[symbol] = DateTime.UtcNow;

                var signal = new MomentumSignal
                {
                    Symbol     = symbol,
                    IsLong     = isGreen,
                    Strength   = moveRatio,
                    VolumeMult = volumeMult,
                    Price      = closePrice,
                    Atr1M      = atr1m,
                    DetectedAt = DateTime.UtcNow,
                    Reason     = $"MOMENTUM_1M move={moveRatio:F2}×ATR vol={volumeMult:F1}×avg dir={sameDir}/{DirectionBars}"
                };

                _logger.LogInformation(
                    "[MOMENTUM] 🚀 {symbol} {dir} detected! strength={str:F2}×ATR vol={vol:F1}×avg price={price}",
                    symbol,
                    isGreen ? "LONG" : "SHORT",
                    moveRatio,
                    volumeMult,
                    closePrice);

                MomentumDetected?.Invoke(symbol, signal);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MOMENTUM] Error processing {symbol}", symbol);
            }
        }

        // ATR(14) расчёт
        private static decimal CalcAtr(
            IReadOnlyList<Binance.Net.Objects.Models.Futures.BinanceFuturesUsdtKline> klines,
            int period)
        {
            if (klines.Count < period + 1) return 0m;

            decimal sum = 0m;
            int last = klines.Count - 1;
            for (int i = last - period + 1; i <= last; i++)
            {
                decimal hl   = klines[i].HighPrice - klines[i].LowPrice;
                decimal hpcp = Math.Abs(klines[i].HighPrice - klines[i - 1].ClosePrice);
                decimal lpcp = Math.Abs(klines[i].LowPrice  - klines[i - 1].ClosePrice);
                sum += Math.Max(hl, Math.Max(hpcp, lpcp));
            }
            return sum / period;
        }

        // Сбросить cooldown (например при закрытии позиции)
        public void ResetCooldown(string symbol)
            => _lastTrigger.TryRemove(symbol, out _);

        // Проверить есть ли активный импульс по символу
        public bool HasRecentTrigger(string symbol)
            => _lastTrigger.TryGetValue(symbol, out var t) &&
               DateTime.UtcNow - t < TriggerCooldown;
    }
}
