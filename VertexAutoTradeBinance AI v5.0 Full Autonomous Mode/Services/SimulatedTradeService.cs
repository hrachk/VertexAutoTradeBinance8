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
        /// Paper-fill пропущенной сделки.
        /// FIX: раньше «следующие свечи после сигнала» на деле были ПОСЛЕДНИМИ 30 свечами
        /// ДО сигнала → AI обучался на мусоре. Теперь симуляция отложена: через 60 минут
        /// берём только свечи, открывшиеся ПОСЛЕ сигнала.
        /// </summary>
        public Task SimulateMissedTradeAsync(TradeSignal signal, string reason)
        {
            var snapshot = new TradeSignal
            {
                Symbol = signal.Symbol,
                Side = signal.Side,
                EntryPrice = signal.EntryPrice,
                StopLoss = signal.StopLoss,
                TakeProfit = signal.TakeProfit ?? signal.TakeProfits?.FirstOrDefault(),
                Time = DateTime.UtcNow
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(60));
                    var all = await _marketData.GetKlines(snapshot.Symbol, KlineInterval.FiveMinutes, 30);
                    var after = all.Where(k => k.OpenTime >= snapshot.Time && k.CloseTime <= DateTime.UtcNow).ToList();
                    if (after.Count == 0 || snapshot.EntryPrice <= 0 || snapshot.StopLoss <= 0)
                        return;

                    decimal entry = snapshot.EntryPrice;
                    decimal sl = snapshot.StopLoss;
                    decimal tp = snapshot.TakeProfit is > 0 ? snapshot.TakeProfit.Value
                        : (snapshot.Side == SignalSide.Buy ? entry + Math.Abs(entry - sl) * 1.5m : entry - Math.Abs(entry - sl) * 1.5m);

                    bool filled = false, hitSL = false, hitTP = false;
                    foreach (var c in after)
                    {
                        if (!filled)
                        {
                            filled = c.LowPrice <= entry && c.HighPrice >= entry;
                            if (!filled) continue;
                        }
                        // консервативно: если в одной свече и SL и TP — считаем SL
                        if (snapshot.Side == SignalSide.Buy)
                        {
                            if (c.LowPrice <= sl) { hitSL = true; break; }
                            if (c.HighPrice >= tp) { hitTP = true; break; }
                        }
                        else
                        {
                            if (c.HighPrice >= sl) { hitSL = true; break; }
                            if (c.LowPrice <= tp) { hitTP = true; break; }
                        }
                    }
                    if (!filled) return;

                    decimal result = hitSL ? -Math.Abs(entry - sl) : hitTP ? Math.Abs(tp - entry) : 0m;
                    _learningService.RecordSimulatedTrade(snapshot.Symbol, snapshot.Side.ToString(), entry, sl, tp, result, reason);
                    _logger.LogInformation("[SIM][{symbol}] missed-trade outcome ({reason}) → {result}", snapshot.Symbol, reason, result);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[SIM] simulation failed");
                }
            });

            return Task.CompletedTask;
        }
    }
}
