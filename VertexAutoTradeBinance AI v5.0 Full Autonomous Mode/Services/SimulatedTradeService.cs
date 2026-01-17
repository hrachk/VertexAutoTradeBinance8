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
       string? note = null,
       decimal? freeBalance = null,
       decimal? attemptNotional = null,
       decimal? requiredMinNotional = null)
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
                    SmartType = "",
                      Note = note ?? ""
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


        // =========================================================
        // INTERNAL IO
        // =========================================================

        private static readonly SemaphoreSlim _globalIoGate = new(1, 1);

        private async Task AppendRecordAsync(MissedTradeRecord record)
        {
            await _globalIoGate.WaitAsync();
            try
            {
                List<MissedTradeRecord> list;

                if (File.Exists(_filePath))
                {
                    await using var fs = new FileStream(
                        _filePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite);

                    list = await JsonSerializer.DeserializeAsync<List<MissedTradeRecord>>(fs, JsonOpts)
                           ?? new List<MissedTradeRecord>();
                }
                else
                {
                    list = new List<MissedTradeRecord>();
                }

                list.Add(record);

                var tmp = _filePath + ".tmp";
                var bak = _filePath + ".bak";

                await using (var fs = new FileStream(
                    tmp,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(fs, list, JsonOpts);
                    await fs.FlushAsync();
                }

                if (File.Exists(_filePath))
                {
                    File.Replace(tmp, _filePath, bak, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tmp, _filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SIM] AppendRecordAsync failed (IO-safe)");
            }
            finally
            {
                _globalIoGate.Release();
            }
        }


        public async Task AppendLifecycleEventAsync(
    TradeSignal signal,
    string stage,
    string reason = "",
    decimal? freeBalance = null,
    decimal? attemptNotional = null,
    decimal? requiredMinNotional = null,
    string? note = null,
    MarketRegime? regime = null,
    string? smartType = null,
    decimal? vol = null,
    decimal? slope = null,
    decimal? deviation = null)
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

                    TakeProfits = signal.TakeProfits.Count > 0
                        ? new List<decimal>(signal.TakeProfits)
                        : new List<decimal>(),

                    Event = stage,
                    Reason = reason,

                    AttemptNotional = attemptNotional ?? 0m,
                    RequiredMinNotional = requiredMinNotional ?? 0m,
                    FreeBalance = freeBalance ?? 0m,

                    Atr = signal.Atr ?? 0m,
                    Confidence = (int)((signal.Confidence ?? 0m) * 100),
                    Score = (int)((signal.AiQuality ?? 0m) * 100),

                    Regime = regime ?? MarketRegime.Unknown,
                    SmartType = smartType ?? "",

                    Vol = vol ?? 0m,
                    Slope = slope ?? 0m,
                    Deviation = deviation ?? 0m,

                    Note = note ?? "" // см. пункт 3.2
                };

                await AppendRecordAsync(record);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SIM] Failed to append lifecycle event");
            }
        }

    }
}
