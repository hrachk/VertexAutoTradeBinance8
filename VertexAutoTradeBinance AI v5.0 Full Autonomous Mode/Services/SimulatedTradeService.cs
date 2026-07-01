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

            // Use SharedData:Root like every other cross-process file in this
            // project (decision_trace_*.jsonl, klines_bootstrap.json, etc) —
            // AppContext.BaseDirectory points at THIS process's own bin folder,
            // which is wrong when the Web app (running from a different bin
            // folder) needs to read missed_trades.json back. Falls back to the
            // local bin folder only if SharedData:Root isn't configured.
            var sharedRoot = cfg["SharedData:Root"];
            var root = !string.IsNullOrWhiteSpace(sharedRoot)
                ? sharedRoot
                : AppContext.BaseDirectory;

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
        /*
        public async Task<MissedTradeRecord?> SimulateMissedTradeAsync(
       TradeSignal signal,
       string reason,
       string? note = null,
       decimal? freeBalance = null,
       decimal? attemptNotional = null,
       decimal? requiredMinNotional = null)
        {
            if (signal == null)
            {
                _logger.LogError("[SIM] SimulateMissedTradeAsync called with null signal | reason={reason}, note={note}", reason, note);
                return null;
            }
          
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

                decimal entry = signal.EntryPrice;   // уже безопасно
                decimal sl = signal.StopLoss;
                decimal tp = signal.TakeProfit ?? (signal.TakeProfits?.FirstOrDefault() ?? entry * 1.01m);


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
                    EntryRangeLow = signal.EntryRangeLow,
                    EntryRangeHigh = signal.EntryRangeHigh,

                    TakeProfits = signal.TakeProfits != null && signal.TakeProfits.Count > 0
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
                bool isExecutionPolicy =
                    reason.StartsWith("FALLBACK_MKT_BLOCKED", StringComparison.OrdinalIgnoreCase) ||
                    reason.StartsWith("EXECUTION_POLICY", StringComparison.OrdinalIgnoreCase);

                if (!isExecutionPolicy)
                {
                    _learningService.RecordSimulatedTrade(
                        signal.Symbol,
                        signal.Side.ToString(),
                        entry,
                        sl,
                        tp,
                        result,
                        reason);
                }
                else
                {
                    _logger.LogInformation(
                        "[SIM][{symbol}] Execution-policy miss → AI learning skipped | reason={reason}",
                        signal.Symbol, reason);
                }

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
        */
        // =========================================================
        // 1️⃣ Метод SimulateMissedTradeAsync — финальный фикс
        // =========================================================
        public async Task<MissedTradeRecord?> SimulateMissedTradeAsync(
      TradeSignal signal,
      string reason,
      string? note = null,
      decimal? freeBalance = null,
      decimal? attemptNotional = null,
      decimal? requiredMinNotional = null)
        {
            if (signal == null)
            {
                _logger.LogError("[SIM] Signal is null | reason={reason}", reason);
                return null;
            }

            try
            {
                var klines = await _marketData.GetFuturesKlinesAsync(
                    signal.Symbol,
                    KlineInterval.FiveMinutes,
                    30);

                if (klines == null || klines.Count == 0)
                {
                    _logger.LogWarning("[SIM] No klines for simulation {symbol}", signal.Symbol);
                    return null;
                }

                // =====================================================
                // 1️⃣ BASE VALUES
                // =====================================================

                decimal entry = signal.EntryPrice;

                if (entry <= 0)
                {
                    _logger.LogWarning("[SIM] Invalid entry price {entry}", entry);
                    return null;
                }

                decimal atr = Math.Abs(signal.Atr ?? 0m);

                if (atr <= 0)
                    atr = entry * 0.002m; // fallback 0.2%

                // PROFESSIONAL ATR RISK MODEL
                decimal slAtrMultiplier = 2.21m;   // ← увеличивает SL distance
                decimal tpAtrMultiplier = 3.0m;   // ← правильный RR model

                decimal minDistance =
                    Math.Max(
                        atr * slAtrMultiplier,
                        entry * 0.002m); // safety floor 0.2%

                decimal sl = signal.StopLoss;
                List<decimal> tps =
                    signal.TakeProfits != null && signal.TakeProfits.Count > 0
                    ? new List<decimal>(signal.TakeProfits)
                    : new List<decimal>();

                // =====================================================
                // 2️⃣ STOP LOSS NORMALIZATION
                // =====================================================

                bool slInvalid = false;

                if (sl <= 0)
                    slInvalid = true;

                if (Math.Abs(sl - entry) < minDistance)
                    slInvalid = true;

                if (signal.Side == SignalSide.Buy && sl >= entry)
                    slInvalid = true;

                if (signal.Side == SignalSide.Sell && sl <= entry)
                    slInvalid = true;

                if (slInvalid)
                {
                    if (signal.Side == SignalSide.Buy)
                        sl = entry - minDistance;
                    else
                        sl = entry + minDistance;
                }

                // final safety clamp
                if (signal.Side == SignalSide.Buy)
                    sl = Math.Min(sl, entry - minDistance);

                if (signal.Side == SignalSide.Sell)
                    sl = Math.Max(sl, entry + minDistance);

                // =====================================================
                // 3️⃣ TAKE PROFIT NORMALIZATION
                // =====================================================

                if (tps.Count == 0)
                {
                    if (signal.Side == SignalSide.Buy)
                        tps.Add(entry + atr * tpAtrMultiplier);
                    else
                        tps.Add(entry - atr * tpAtrMultiplier);
                }

                decimal tp = tps[0];

                bool tpInvalid = false;

                if (signal.Side == SignalSide.Buy && tp <= entry)
                    tpInvalid = true;

                if (signal.Side == SignalSide.Sell && tp >= entry)
                    tpInvalid = true;

                if (Math.Abs(tp - entry) < minDistance)
                    tpInvalid = true;

                if (tpInvalid)
                {
                    if (signal.Side == SignalSide.Buy)
                        tp = entry + atr * tpAtrMultiplier;
                    else
                        tp = entry - atr * tpAtrMultiplier;

                    tps[0] = tp;
                }

                // =====================================================
                // 4️⃣ SIMULATION ENGINE
                // realistic execution model
                // =====================================================

                bool hitSL = false;
                bool hitTP = false;

                foreach (var candle in klines)
                {
                    if (signal.Side == SignalSide.Buy)
                    {
                        if (candle.LowPrice <= sl)
                        {
                            hitSL = true;
                            break;
                        }

                        if (candle.HighPrice >= tp)
                        {
                            hitTP = true;
                            break;
                        }
                    }
                    else
                    {
                        if (candle.HighPrice >= sl)
                        {
                            hitSL = true;
                            break;
                        }

                        if (candle.LowPrice <= tp)
                        {
                            hitTP = true;
                            break;
                        }
                    }
                }

                decimal result = 0m;

                if (hitSL)
                    result = -Math.Abs(entry - sl);
                else if (hitTP)
                    result = Math.Abs(tp - entry);

                // =====================================================
                // 5️⃣ BUILD RECORD
                // =====================================================

                var record = new MissedTradeRecord
                {
                    Symbol = signal.Symbol,
                    Time = DateTime.UtcNow,

                    Entry = entry,
                    StopLoss = sl,
                    EntryRangeLow = signal.EntryRangeLow,
                    EntryRangeHigh = signal.EntryRangeHigh,

                    Side = signal.Side.ToString(),
                    TakeProfits = tps,

                    Event = "SIMULATED_RESULT",

                    Reason = reason,
                    Note = note ?? "",

                    AttemptNotional = attemptNotional ?? 0m,
                    RequiredMinNotional = requiredMinNotional ?? 0m,
                    FreeBalance = freeBalance ?? 0m,

                    Atr = atr,

                    Vol = 0m,
                    Slope = 0m,
                    Deviation = 0m,

                    Confidence = (int)((signal.Confidence ?? 0m) * 100),
                    Score = (int)((signal.AiQuality ?? 0m) * 100),

                    Regime = MarketRegime.Unknown,
                    SmartType = ""
                };

                await AppendRecordAsync(record);

                // =====================================================
                // 6️⃣ AI LEARNING
                // =====================================================

                bool isExecutionPolicy =
                    reason.StartsWith("FALLBACK_MKT_BLOCKED", StringComparison.OrdinalIgnoreCase) ||
                    reason.StartsWith("EXECUTION_POLICY", StringComparison.OrdinalIgnoreCase);

                if (!isExecutionPolicy)
                {
                    _learningService.RecordSimulatedTrade(
                        signal.Symbol,
                        signal.Side.ToString(),
                        entry,
                        sl,
                        tp,
                        result,
                        reason);
                }

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
                if (signal == null)
                    return;

                var record = new MissedTradeRecord
                {
                    Symbol = signal.Symbol ?? "",
                    Time = DateTime.UtcNow,

                    Entry = signal.EntryPrice,
                    StopLoss = signal.StopLoss,
                    EntryRangeLow = signal.EntryRangeLow,
                    EntryRangeHigh = signal.EntryRangeHigh,

                    Side = signal.Side.ToString(),

                    // ✅ FIX
                    TakeProfits = signal.TakeProfits != null && signal.TakeProfits.Count > 0
                        ? new List<decimal>(signal.TakeProfits)
                        : new List<decimal>(),

                    Event = stage ?? "",
                    Reason = reason ?? "",

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

                    Note = note ?? ""
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
