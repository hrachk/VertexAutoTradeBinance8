using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Симулятор пропущенных сделок (Paper-Fill Engine)
    /// </summary>
    public class SimulatedTradeService
    {
        private readonly MarketDataService _marketData;
        
        private readonly ILogger<SimulatedTradeService> _logger;
        private readonly  AiSelfLearningService  _learningService;

        public SimulatedTradeService(
            MarketDataService marketData,
             AiSelfLearningService  learningService,
            ILogger<SimulatedTradeService> logger)
        {
            _marketData = marketData;
            _learningService = learningService;  // Lazy инъекция
            _logger = logger;
        }

        /// <summary>
        /// Запускает симуляцию сделки, если позиция была пропущена
        /// </summary>
        public async Task SimulateMissedTradeAsync(TradeSignal signal, string reason)
        {
            try
            {
                _logger.LogInformation(
                    "[SIM][{symbol}] Старт симуляции пропущенной сделки. Причина: {reason}",
                    signal.Symbol, reason);

                // Загружаем следующие 20 свечей после сигнала
                var klines = await _marketData.GetFuturesKlinesAsync(
                    signal.Symbol,
                    KlineInterval.FiveMinutes,
                    30);

                if (klines == null || klines.Count == 0)
                    return;

                decimal entry = signal.EntryPrice;
                decimal sl = signal.StopLoss;
                decimal tp = signal.TakeProfit ?? (signal.EntryPrice * 1.01m);

                bool hitSL = false;
                bool hitTP = false;

                foreach (var candle in klines)
                {
                    if (signal.Side == SignalSide.Buy)
                    {
                        if (candle.LowPrice <= sl) { hitSL = true; break; }
                        if (candle.HighPrice >= tp) { hitTP = true; break; }
                    }
                    else
                    {
                        if (candle.HighPrice >= sl) { hitSL = true; break; }
                        if (candle.LowPrice <= tp) { hitTP = true; break; }
                    }
                }

                decimal result;
                if (hitSL) result = -Math.Abs(entry - sl);
                else if (hitTP) result = Math.Abs(tp - entry);
                else result = 0;

                // Передаём в обучающую систему
                _learningService.RecordSimulatedTrade(
                    signal.Symbol,
                    signal.Side.ToString(),
                    entry,
                    sl,
                    tp,
                    result,
                    reason);

                _logger.LogInformation(
                    "[SIM][{symbol}] Завершена симуляция → Outcome: {result}",
                    signal.Symbol, result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SIM] Ошибка симуляции сделки");
            }
        }
    }
}
