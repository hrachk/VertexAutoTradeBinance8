using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Helpers;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// v6 – обучающий модуль QUANT-REALTIME:
    /// Проверяет закрытую позицию и отправляет данные в AiSelfLearningService.RecordTrade().
    /// Используется как часть полного цикла анализа сигнала.
    /// </summary>
    public class TradeResultMonitorService
    {
        private readonly ILogger<TradeResultMonitorService> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly AiSelfLearningService _aiLearning;
        private readonly AiMarketRegimeService _regimeService;

        public TradeResultMonitorService(
            ILogger<TradeResultMonitorService> logger,
            BinanceClientFactory factory,
            AiSelfLearningService aiLearning,
            AiMarketRegimeService regimeService)
        {
            _logger = logger;
            _factory = factory;
            _aiLearning = aiLearning;
            _regimeService = regimeService;
        }

        /// <summary>
        /// Проверяет, закрылась ли позиция после выхода — и обучает AI по фактической сделке.
        /// </summary>
        public async Task CheckClosedPositionAsync(string symbol, TradeSignal signal, CancellationToken ct)
        {
            if (signal == null)
                return;

            using var client = _factory.CreateRestClient();

            // ===========================
            // 1) Получаем позиции
            // ===========================
            var posResult = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, null, ct);
            if (!posResult.Success || posResult.Data == null)
                return;

            var longPos = posResult.Data.FirstOrDefault(p => p.PositionSide == PositionSide.Long);
            var shortPos = posResult.Data.FirstOrDefault(p => p.PositionSide == PositionSide.Short);

            decimal qtyLong = longPos != null ? Math.Abs(longPos.Quantity) : 0m;
            decimal qtyShort = shortPos != null ? Math.Abs(shortPos.Quantity) : 0m;

            // Если есть активная позиция — сделка НЕ закрыта
            if (qtyLong > 0 || qtyShort > 0)
                return;

            // ===========================
            // 2) Цена выхода
            // ===========================
            decimal exitPrice = longPos?.MarkPrice ?? shortPos?.MarkPrice ?? 0m;

            if (exitPrice <= 0)
                return;

            decimal entry = signal.EntryPrice;
            decimal sl = signal.StopLoss;

            // ===========================
            // 3) Рассчитываем R/R и win/lose
            // ===========================
            decimal risk = Math.Abs(entry - sl);
            decimal reward = Math.Abs(exitPrice - entry);
            decimal rr = reward / Math.Max(risk, 0.00001m);

            bool isWin = signal.Side == SignalSide.Buy
                ? exitPrice > entry
                : exitPrice < entry;

            // ===========================
            // 4) Определяем режим рынка на выходе
            // ===========================
            MarketRegime regime = MarketRegime.Range;

            try
            {
                var kl = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    signal.Timeframe!.ToKlineInterval(),
                    startTime: null,
                    endTime: null,
                    limit: 150,
                    ct);

                if (kl.Success)
                {
                    var det = _regimeService.DetectRegime(
                        symbol,
                        signal.Timeframe!.ToKlineInterval(),
                        kl.Data.Cast<BinanceFuturesUsdtKline>().ToList());

                    if (det != null)
                        regime = det.Regime;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TRADE-MONITOR] Error detecting market regime");
            }

            // ===========================
            // 5) Обучаем AI (новый метод v6)
            // ===========================
            try
            {
                _aiLearning.RecordTrade(
                    symbol,
                    signal.Side,
                    entry,
                    exitPrice,
                    regime);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TRADE-MONITOR] AI RecordTrade error");
            }

            // ===========================
            // 6) Логирование
            // ===========================
            _logger.LogInformation(
                "AI-LEARN CLOSED TRADE {symbol}: entry={entry}, exit={exit}, rr={rr:F2}, win={win}, regime={regime}",
                symbol, entry, exitPrice, rr, isWin, regime);
        }
    }
}
