using Binance.Net.Enums;
using System.Text.Json;
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
        private readonly string _filePath;
        private readonly object _lock = new();
      


        public SimulatedTradeService(
    MarketDataService marketData,
    AiSelfLearningService learningService,
    ILogger<SimulatedTradeService> logger,
    IConfiguration cfg)
        {
            _marketData = marketData;
            _learningService = learningService;
            _logger = logger;

            var root = cfg["SharedData:Root"]
                ?? throw new InvalidOperationException("SharedData:Root not configured");

            _filePath = Path.Combine(root, "missed_trades.json");

            try
            {
                Directory.CreateDirectory(root);

                if (!File.Exists(_filePath))
                {
                    File.WriteAllText(_filePath, "[]");
                    _logger.LogInformation("[SIM] missed_trades.json created at {path}", _filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SIM] Failed to init missed_trades.json");
            }
        }

        public void AppendLifecycleEvent(
    TradeSignal signal,
    string stage,
    string reason = "",
    decimal attemptNotional = 0m,
    decimal requiredMinNotional = 0m)
        {
            try
            {
                var record = new MissedTradeRecord
                {
                    Symbol = signal.Symbol,
                    Time = DateTime.UtcNow,

                    Entry = signal.EntryPrice,
                    StopLoss = signal.StopLoss,
                    Side = signal.Side.ToString(),
                    TakeProfits = new List<decimal>(signal.TakeProfits),

                    Event = stage,          // 🔑 КЛЮЧ
                    Reason = reason,

                    AttemptNotional = attemptNotional,
                    RequiredMinNotional = requiredMinNotional,

                    FreeBalance = 0m,
                    Atr = signal.Atr ?? 0m,
                    Confidence = (int)((signal.Confidence ?? 0m) * 100),
                    Score = (int)((signal.AiQuality ?? 0m) * 100),

                    Regime = MarketRegime.Unknown,
                    SmartType = "",
                    Vol = 0m,
                    Slope = 0m,
                    Deviation = 0m
                };

                AppendRecord(record);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SIM] failed to append lifecycle event");
            }
        }

        public async Task<MissedTradeRecord?> SimulateMissedTradeAsync(
        TradeSignal signal,
        string reason,
        string? note = null)
        {
            try
            {
                _logger.LogInformation(
                    "[SIM][{symbol}] Старт симуляции пропущенной сделки. Причина: {reason}",
                    signal.Symbol, reason);

                // ======================================================
                // 1) Загружаем будущие свечи
                // ======================================================
                var klines = await _marketData.GetFuturesKlinesAsync(
                    signal.Symbol,
                    KlineInterval.FiveMinutes,
                    30);

                if (klines == null || klines.Count == 0)
                    return null;

                decimal entry = signal.EntryPrice;
                decimal sl = signal.StopLoss;

                decimal tp =
                    signal.TakeProfit
                    ?? (signal.TakeProfits.Count > 0 ? signal.TakeProfits[0] : entry * 1.01m);

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
                else result = 0m;

                // ======================================================
                // 2) Формируем MissedTradeRecord (БЕЗ ВЫДУМАННЫХ ПОЛЕЙ)
                // ======================================================
                var record = new MissedTradeRecord
                {
                    Symbol = signal.Symbol,
                    Time = DateTime.UtcNow,

                    Entry = entry,
                    StopLoss = sl,
                    Side = signal.Side.ToString(),

                    TakeProfits = signal.TakeProfits.Count > 0
                        ? new List<decimal>(signal.TakeProfits)
                        : new List<decimal> { tp },

                    Reason = reason,

                    // --- нет в TradeSignal → safe defaults ---
                    FreeBalance = 0m,
                    AttemptNotional = 0m,
                    RequiredMinNotional = 0m,

                    Atr = signal.Atr ?? 0m,
                    Vol = 0m,
                    Slope = 0m,
                    Confidence = (int)((signal.AiQuality ?? 0m) * 100),

                    Regime = MarketRegime.Unknown,
                    SmartType = "",

                    Deviation = 0m,
                    Score = (int)((signal.AiQuality ?? 0m) * 100)
                };



                // ======================================================
                // 3) AI — SIMULATED TRADE CHANNEL
                // ======================================================
                _learningService.RecordSimulatedTrade(
                    signal.Symbol,
                    signal.Side.ToString(),
                    entry,
                    sl,
                    tp,
                    result,
                    reason);

                // ======================================================
                // 4) WRITE missed_trades.json (LOG + UI)
                // ======================================================
                try
                {
                    lock (_lock)
                    {
                        List<MissedTradeRecord> list;

                        if (File.Exists(_filePath))
                        {
                            var json = File.ReadAllText(_filePath);
                            list = JsonSerializer.Deserialize<List<MissedTradeRecord>>(json)
                                   ?? new List<MissedTradeRecord>();
                        }
                        else
                        {
                            list = new List<MissedTradeRecord>();
                        }

                        list.Add(record);

                        File.WriteAllText(
                            _filePath,
                            JsonSerializer.Serialize(
                                list,
                                new JsonSerializerOptions { WriteIndented = true }
                            ));
                    }

                    _logger.LogInformation("[SIM] missed_trades.json appended");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[SIM] failed to write missed_trades.json");
                }

                _logger.LogInformation(
                    "[SIM][{symbol}] Завершена симуляция → Outcome: {result}",
                    signal.Symbol, result);
                record.Event = "SIMULATED_RESULT";
                AppendRecord(record);
                return record;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SIM] Ошибка симуляции сделки");
                return null;
            }

        }

        private void AppendRecord(MissedTradeRecord record)
        {
            lock (_lock)
            {
                List<MissedTradeRecord> list;

                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    list = JsonSerializer.Deserialize<List<MissedTradeRecord>>(json)
                           ?? new List<MissedTradeRecord>();
                }
                else
                {
                    list = new List<MissedTradeRecord>();
                }

                list.Add(record);

                File.WriteAllText(
                    _filePath,
                    JsonSerializer.Serialize(list, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));
            }
        }

    }
}
