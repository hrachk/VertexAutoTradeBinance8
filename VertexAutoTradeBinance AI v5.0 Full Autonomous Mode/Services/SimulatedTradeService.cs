using Binance.Net.Enums;
using System.Text;
using System.Text.Json;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Симулятор пропущенных сделок (Paper-Fill Engine)
    /// </summary>
    public sealed class SimulatedTradeService
    {
        private readonly MarketDataService _marketData;
        private readonly AiSelfLearningService _learningService;
        private readonly ILogger<SimulatedTradeService> _logger;

        private readonly string _filePath;
        private readonly SemaphoreSlim _ioGate = new(1, 1);

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

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

            Directory.CreateDirectory(root);
            _filePath = Path.Combine(root, "missed_trades.json");

            try
            {
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

        // =========================================================
        // PUBLIC API
        // =========================================================

        public async Task<MissedTradeRecord?> SimulateMissedTradeAsync(
            TradeSignal signal,
            string reason,
            string? note = null)
        {
            try
            {
                _logger.LogInformation(
                    "[SIM][{symbol}] Start missed-trade simulation | reason={reason}",
                    signal.Symbol, reason);

                // 1) Load future klines
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
                    ?? (signal.TakeProfits.Count > 0
                        ? signal.TakeProfits[0]
                        : entry * 1.01m);

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

                decimal result =
                    hitSL ? -Math.Abs(entry - sl) :
                    hitTP ? Math.Abs(tp - entry) :
                    0m;

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

                    Event = "SIMULATED_RESULT",
                    Reason = reason,

                    AttemptNotional = 0m,
                    RequiredMinNotional = 0m,
                    FreeBalance = 0m,

                    Atr = signal.Atr ?? 0m,
                    Vol = 0m,
                    Slope = 0m,
                    Deviation = 0m,

                    Confidence = (int)((signal.Confidence ?? 0m) * 100),
                    Score = (int)((signal.AiQuality ?? 0m) * 100),

                    Regime = MarketRegime.Unknown,
                    SmartType = ""
                };

                // 2) Persist FIRST
                await AppendRecordAsync(record);

                // 3) AI learning AFTER persistence
                _learningService.RecordSimulatedTrade(
                    signal.Symbol,
                    signal.Side.ToString(),
                    entry,
                    sl,
                    tp,
                    result,
                    reason);

                _logger.LogInformation(
                    "[SIM][{symbol}] Simulation finished | result={res}",
                    signal.Symbol, result);

                return record;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SIM] Simulation failed");
                return null;
            }
        }

        public async Task AppendLifecycleEventAsync(
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

                    Event = stage,
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

                await AppendRecordAsync(record);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SIM] Failed to append lifecycle event");
            }
        }

        // =========================================================
        // INTERNAL IO
        // =========================================================

        private async Task AppendRecordAsync(MissedTradeRecord record)
        {
            await _ioGate.WaitAsync();
            try
            {
                List<MissedTradeRecord> list;

                if (File.Exists(_filePath))
                {
                    var json = await File.ReadAllTextAsync(_filePath, Encoding.UTF8);
                    list = JsonSerializer.Deserialize<List<MissedTradeRecord>>(json, JsonOpts)
                           ?? new List<MissedTradeRecord>();
                }
                else
                {
                    list = new List<MissedTradeRecord>();
                }

                list.Add(record);

                var tmp = _filePath + ".tmp";
                var outJson = JsonSerializer.Serialize(list, JsonOpts);

                await File.WriteAllTextAsync(tmp, outJson, Encoding.UTF8);
                File.Move(tmp, _filePath, overwrite: true);
            }
            finally
            {
                _ioGate.Release();
            }
        }
    }
}
