using System.Text;
using System.Text.Json;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Strategy;

namespace VertexAutoTradeBinance8.Services
{ 
    internal sealed class RegimeGateProfile
    {
        public MarketRegime Regime { get; init; }

        // базовые веса (ручной дизайн)
        public Dictionary<string, decimal> BaseWeights { get; } = new();

        // авто-коррекция от AI (накопленная)
        public Dictionary<string, decimal> AdaptiveBias { get; } = new();
      

        public decimal GetWeight(string gate)
        {
            BaseWeights.TryGetValue(gate, out var baseW);
            AdaptiveBias.TryGetValue(gate, out var bias);

            if (baseW == 0 && bias == 0)
                return 1.0m;

            var w = (baseW == 0 ? 1.0m : baseW) * (bias == 0 ? 1.0m : bias);
            return Math.Clamp(w, 0.70m, 1.30m);
        }
    }
    public class AiSelfLearningService
    {
        private DateTime? _lastImportedTradeCloseUtc;
        public DateTime? LastImportedTradeCloseUtc => _lastImportedTradeCloseUtc;

        public void UpdateLastImportedTradeCloseUtc(DateTime time)
        {
            _lastImportedTradeCloseUtc = time;
        }

        public decimal GetGateMultiplier(string symbol, MarketRegime regime, string gate)
        {
            // v1: symbol сейчас не участвует (оставляем в сигнатуре на будущее)
            return GetGateWeight(regime, gate);
        }
         
        // symbol-agnostic, режим — главный ключ
        private readonly Dictionary<MarketRegime, RegimeGateProfile> _gateProfiles
            = new();

        private void EnsureGateProfiles()
        {
            lock (_lock)
            {
                void add(MarketRegime r, Action<Dictionary<string, decimal>> cfg)
                {
                    if (_gateProfiles.ContainsKey(r)) return;
                    var p = new RegimeGateProfile { Regime = r };
                    cfg(p.BaseWeights);
                    _gateProfiles[r] = p;
                }

                add(MarketRegime.StrongUpTrend, w =>
                {
                    w["RR"] = 0.90m;
                    w["PATTERN"] = 0.95m;
                    w["LIQ"] = 1.05m;
                    w["EXPO"] = 1.00m;
                });

                add(MarketRegime.Range, w =>
                {
                    w["RR"] = 1.15m;
                    w["PATTERN"] = 1.10m;
                    w["LIQ"] = 0.95m;
                    w["EXPO"] = 1.00m;
                });

                add(MarketRegime.StrongDownTrend, w =>
                {
                    w["RR"] = 0.90m;
                    w["PATTERN"] = 0.95m;
                    w["LIQ"] = 1.05m;
                    w["EXPO"] = 1.00m;
                });

                add(MarketRegime.Unknown, w => { });

                foreach (MarketRegime r in Enum.GetValues(typeof(MarketRegime)))
                {
                    if (!_gateProfiles.ContainsKey(r))
                        _gateProfiles[r] = new RegimeGateProfile { Regime = r };
                }
            }
               
        }


        private readonly ILogger<AiSelfLearningService> _logger;
        private readonly object _lock = new();

        private static readonly string FilePath =
            Path.Combine(AppContext.BaseDirectory, "ai-models/ai_learning.json");

        private static readonly string BackupPath =
            Path.Combine(AppContext.BaseDirectory, "ai-models/ai_learning_backup.json");

        // Снимок статистики каждые N минут (для trade-based / signal-based)
        private DateTime _lastSnapshot = DateTime.MinValue;
        private readonly TimeSpan SnapshotInterval = TimeSpan.FromMinutes(5);

        // Глобальный HYBRID snapshot раз в N секунд (background learning)
        private DateTime _lastHybridSnapshot = DateTime.MinValue;

        // Было 60s, делаю более «квантовым» — 30s (в твои 15–30s)
        private readonly TimeSpan HybridInterval = TimeSpan.FromSeconds(30);

        // =====================================================================
        // CORE STORAGE v7
        // =====================================================================

        /// <summary>
        /// Главная статистика по символу/режиму (только по сделкам).
        /// Используется GetAiRiskAdjustment() → AiRisk в StrategyEngine.
        /// </summary>
        private readonly Dictionary<string, Dictionary<MarketRegime, RegimeStats>> _stats
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// QUANT STORAGE – непрерывная лента рыночных состояний
        /// (используется PredictTrend и для будущего Dashboard).
        /// </summary>
        private readonly List<MarketState> _marketStates = new();

        /// <summary>
        /// История сделок (используется для анализа, Dashboard, off-line обучения).
        /// </summary>
        private readonly List<TradeHistoryEntry> _tradeHistory = new();

        // =====================================================
        // AI META (for startup / registry / dashboard)
        // =====================================================

        /// <summary>
        /// UTC time when AI service instance was created.
        /// Used for cold-start detection.
        /// </summary>
        public DateTime StartedUtc { get; } = DateTime.UtcNow;

        /// <summary>
        /// Total number of recorded real trades (learning-confirmed).
        /// </summary>
        public int TotalTrades
        {
            get
            {
                lock (_lock)
                    return _tradeHistory.Count;
            }
        }

        // TREND MODEL
        public record AiTrendPrediction(int Direction, decimal Confidence, decimal RrBias);

        private Dictionary<string, List<LearningEvent>> _learnBuffer = new Dictionary<string, List<LearningEvent>>();

        // Decision intelligence layer (NEW)
        private readonly Dictionary<string, DecisionTraceAggregate> _decisionGates
            = new(StringComparer.OrdinalIgnoreCase);

        private const double PnlHalfLifeDays = 30.0;
        // === PnL HALF-LIFE CONFIG ===
        private static readonly TimeSpan PnlHalfLife = TimeSpan.FromDays(30); // 30d = 0.5
        private static readonly decimal MinPnlWeight = 0.05m;
 
        public AiSelfLearningService(ILogger<AiSelfLearningService> logger )
        {
            
            _logger = logger;

            // 0) Гарантируем каталоги
            try
            {
                var fileDir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(fileDir) && !Directory.Exists(fileDir))
                    Directory.CreateDirectory(fileDir);

                var backupDir = Path.GetDirectoryName(BackupPath);
                if (!string.IsNullOrWhiteSpace(backupDir) && !Directory.Exists(backupDir))
                    Directory.CreateDirectory(backupDir);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AI] Error creating ai-models directory or backup directory.");
                return;
            }

            // 1) СНАЧАЛА ГРУЗИМ СТАРОЕ СОСТОЯНИЕ
            Load();
            // 🔥 ГАРАНТИЯ: хотя бы один валидный snapshot после старта
            ForceSnapshot();
 
        }
         

        // =====================================================================
        // MODELS
        // =====================================================================
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public class MarketState
        {
            public string Symbol { get; set; } = "";
            public string Timeframe { get; set; } = "";
            public MarketRegime Regime { get; set; }
            public decimal TrendSlopePercent { get; set; }
            public decimal VolatilityPercent { get; set; }
            public decimal Atr { get; set; }
            public decimal Confidence { get; set; }
            public DateTime Time { get; set; }
            public string? Reason { get; set; }   // MICRO_SIGNAL / SOFT_ENTRY / PERIODIC_30s / ...
        }

        public class TradeHistoryEntry
        {
            public string Symbol { get; set; } = "";
            public SignalSide Side { get; set; }
            public decimal Entry { get; set; }
            public decimal Exit { get; set; }
            public decimal Pnl { get; set; }
            public decimal PnlPct { get; set; }
            public MarketRegime Regime { get; set; }
            public DateTime Time { get; set; }
        }

        public class RegimeStats
        {
            public int Trades { get; set; }
            public int Wins { get; set; }
            public int Count { get; set; }

            public int Losses { get; set; }
            public decimal AvgPnl { get; set; }
            public decimal RiskWeight { get; set; } = 1.0m;

            // 🔥 ВАЖНО: явное поле режима для snapshot / Dashboard
            public MarketRegime Regime { get; set; } = MarketRegime.Unknown;
        }

        public decimal GetGateWeight(MarketRegime regime, string gate)
        {
            EnsureGateProfiles();
            return _gateProfiles.TryGetValue(regime, out var p)
                ? p.GetWeight(gate)
                : 1.0m;
        }


        // =====================================================================
        // DECISION TRACE LEARNING (NEW LAYER)
        // =====================================================================
        public void RecordDecisionTrace(
     string symbol,
     MarketRegime regime,
     IReadOnlyList<FastFailResult> gates)
        {
            RegimeGateProfile profile;

            // ===============================
            // 1) GateProfiles + AdaptiveBias
            // ===============================
            lock (_lock)
            {
                EnsureGateProfiles();

                if (!_gateProfiles.TryGetValue(regime, out profile))
                {
                    // auto-register forward-compatible
                    profile = new RegimeGateProfile { Regime = regime };
                    _gateProfiles[regime] = profile;

                    _logger.LogWarning(
                        "[AI] GateProfile auto-registered for new regime: {Regime}",
                        regime);
                }

                foreach (var g in gates)
                {
                    // строгость по прошлой статистике
                    var strict = GetGateStrictness(symbol, regime, g.Gate);

                    profile.AdaptiveBias.TryGetValue(g.Gate, out var cur);

                    var lastUpdate =
        cur == 0 ? DateTime.UtcNow : DateTime.UtcNow.AddMinutes(-10);

                    var decayedStrict = ApplyConfidenceDecay(strict, lastUpdate);

                    var target = decayedStrict; // 0.85 / 1.10 / 1.00
                    var updated = cur == 0
                        ? target
                        : (cur * 0.9m + target * 0.1m);

                    profile.AdaptiveBias[g.Gate] =
                        Math.Clamp(updated, 0.85m, 1.15m);
                }
            }

            // ===============================
            // 2) DecisionTrace aggregation
            // ===============================
            lock (_lock)
            {
                if (!_decisionGates.TryGetValue(symbol, out var agg))
                {
                    agg = new DecisionTraceAggregate { Symbol = symbol };
                    _decisionGates[symbol] = agg;
                }

                if (!agg.ByRegime.TryGetValue(regime, out var reg))
                {
                    reg = new DecisionGateRegimeStats { Regime = regime };
                    agg.ByRegime[regime] = reg;
                }

                foreach (var g in gates)
                {
                    if (!reg.Gates.TryGetValue(g.Gate, out var st))
                    {
                        st = new DecisionGateStats { Gate = g.Gate };
                        reg.Gates[g.Gate] = st;
                    }

                    st.Hits++;
                    if (!g.Allow)
                        st.Blocks++;
                }
            }
        }


        public void RecordMarketStateTriggered(
      string reason,
      string symbol,
      string timeframe,
      MarketRegime regime,
      decimal slope,
      decimal volatility,
      decimal atr,
      decimal confidence,
      bool skipSnapshot = true
  )
        {
            lock (_lock)
            {
                _marketStates.Add(new MarketState
                {
                    Symbol = symbol,
                    Timeframe = timeframe,
                    Regime = regime,
                    TrendSlopePercent = slope,
                    VolatilityPercent = volatility,
                    Atr = atr,
                    Confidence = confidence,
                    Time = DateTime.UtcNow,
                    Reason = reason
                });

                // =====================================================
                // 🔥 BOOTSTRAP STATS (SAFE, NON-DESTRUCTIVE)
                // =====================================================
                if (!_stats.ContainsKey(symbol))
                {
                    _stats[symbol] = new Dictionary<MarketRegime, RegimeStats>
                    {
                        [regime] = new RegimeStats
                        {
                            Regime = regime,
                            Count = 0,
                            Wins = 0,
                            Losses = 0,
                            AvgPnl = 0,
                            RiskWeight = 1.0m
                        }
                    };
                }
                else if (!_stats[symbol].ContainsKey(regime))
                {
                    _stats[symbol][regime] = new RegimeStats
                    {
                        Regime = regime,
                        Count = 0,
                        Wins = 0,
                        Losses = 0,
                        AvgPnl = 0,
                        RiskWeight = 1.0m
                    };
                }

                if (_marketStates.Count > 5000)
                    _marketStates.RemoveRange(0, 2500);

                _logger.LogDebug(
                    "[HYBRID][{Symbol}] MarketState logged ({Reason}) slope={Slope} vol={Vol} atr={Atr} conf={Conf}",
                    symbol, reason, slope, volatility, atr, confidence);
            }

            if (!skipSnapshot)
                TrySnapshot();
        }


        // 3) BACKGROUND MARKET LEARNING – глобальный 30s snapshot по режиму
        public void TryHybridPeriodicSnapshot(
    string symbol,
    string timeframe,
    MarketRegime regime,
    decimal slope,
    decimal volatility,
    decimal atr,
    decimal confidence)
        {
            if (DateTime.UtcNow - _lastHybridSnapshot < HybridInterval)
                return;

            _lastHybridSnapshot = DateTime.UtcNow;

            // 🔥 hybrid snapshot — это фон, но он ВАЖЕН
            Save(force: true);
            _lastSnapshot = DateTime.UtcNow;

            RecordMarketStateTriggered(
    reason: "PERIODIC_30s",
    symbol: symbol,
    timeframe: timeframe,
    regime: regime,
    slope: slope,
    volatility: volatility,
    atr: atr,
    confidence: confidence,
    skipSnapshot: false   // ✅ ВАЖНО
);
        }


        // =====================================================================
        // BASE MARKET STATE (SmartRegimeService → StrategyEngine)
        // =====================================================================
        public void RecordMarketState(
         string symbol,
         string timeframe,
         MarketRegime regime,
         decimal trendSlopePercent,
         decimal volatilityPercent,
         decimal atr,
         decimal confidence)
        {
            lock (_lock)
            {
                _marketStates.Add(new MarketState
                {
                    Symbol = symbol,
                    Timeframe = timeframe,
                    Regime = regime,
                    TrendSlopePercent = trendSlopePercent,
                    VolatilityPercent = volatilityPercent,
                    Atr = atr,
                    Confidence = confidence,
                    Time = DateTime.UtcNow,
                    Reason = "BASE_REGIME"
                });
                if (_marketStates.Count > 5000)
                    _marketStates.RemoveRange(0, 2500);

                _logger.LogInformation(
                "[HYBRID] MarketState tick {symbol} {tf} {regime}",
                symbol, timeframe, regime);
            }

            // 🔥 ВАЖНО: именно здесь
            TryHybridPeriodicSnapshot(
                symbol,
                timeframe,
                regime,
                trendSlopePercent,
                volatilityPercent,
                atr,
                confidence
            );
        }


        private readonly HashSet<string> _learnedTrades = new();


        // =====================================================================
        // 2) TRADE ENTRY (вызывается из PositionSupervisor / TradeResultMonitor)
        // =====================================================================
        public void RecordTrade(
      string symbol,
      SignalSide side,
      decimal entry,
      decimal exit,
      MarketRegime regime)
        {
            if (entry <= 0)
                return;

            // ===========================
            // 0) PnL calculation
            // ===========================
            decimal pnl =
                side == SignalSide.Buy
                    ? exit - entry
                    : entry - exit;

            decimal pnlPct =
                side == SignalSide.Buy
                    ? (exit - entry) / entry
                    : (entry - exit) / entry;

            // ===========================
            // 1) Idempotency guard
            // ===========================
            var tradeKey = $"{symbol}|{side}|{entry}|{exit}";

            lock (_lock)
            {
                if (!_learnedTrades.Add(tradeKey))
                    return;

                // ===========================
                // 2) Store trade history
                // ===========================
                _tradeHistory.Add(new TradeHistoryEntry
                {
                    Symbol = symbol,
                    Side = side,
                    Entry = entry,
                    Exit = exit,
                    Regime = regime,
                    Pnl = pnl,
                    PnlPct = pnlPct,
                    Time = DateTime.UtcNow
                });

                if (_tradeHistory.Count > 5000)
                    _tradeHistory.RemoveRange(0, 2500);
            }

            // ===========================
            // 3) Learning (PERCENT-BASED)
            // ===========================
            UpdateStats(symbol, regime, pnlPct);

            // ===========================
            // 4) Snapshot (forced, safe)
            // ===========================
            Save(force: true);
            _lastSnapshot = DateTime.UtcNow;
        }




        private void UpdateStats(string symbol, MarketRegime regime, decimal pnl)
        {
            if (regime == MarketRegime.Unknown && Math.Abs(pnl) < 0.15m)
                return;
            if (!_stats.TryGetValue(symbol, out var regimes))
            {
                regimes = new Dictionary<MarketRegime, RegimeStats>();
                _stats[symbol] = regimes;
            }

            if (!regimes.TryGetValue(regime, out var s))
            {
                s = new RegimeStats
                {
                    Regime = regime,
                    RiskWeight = 1.0m
                };
                regimes[regime] = s;
            }
            else if (s.Regime == MarketRegime.Unknown)
            {
                s.Regime = regime;
            }

            // ===== БАЗОВАЯ СТАТИСТИКА (НЕ ТРОГАЕМ) =====
            s.Count++;
            if (pnl >= 0) s.Wins++;
            else s.Losses++;

            s.AvgPnl = (s.AvgPnl * (s.Count - 1) + pnl) / s.Count;

            if (s.Count >= 20)
            {
                decimal wr = s.Wins / (decimal)s.Count;
                s.RiskWeight = Math.Clamp(wr, 0.65m, 1.35m);
            }

            // ====================================================
            // 🔥 PnL-WEIGHTED LEARNING (ADD-ON, НЕ ЛОМАЕТ СТАРОЕ)
            // ====================================================

            decimal absPnl = Math.Abs(pnl);
            if (absPnl <= 0)
                return;


            // === HALF-LIFE WEIGHTING ===
            var halfLifeWeight = ApplyHalfLife(DateTime.UtcNow);
            absPnl *= halfLifeWeight;


            const decimal K = 25m;
            const decimal minWeight = 0.3m;
            const decimal maxWeight = 1.3m;
            const decimal lossMultiplier = 1.6m;

            decimal weight = absPnl * K;
            weight = Math.Clamp(weight, minWeight, maxWeight);

            if (pnl < 0)
                weight *= lossMultiplier;

            // корректируем уже рассчитанный RiskWeight
            s.RiskWeight *= pnl >= 0
                ? weight
                : 1m / weight;

            // финальный защитный clamp
            s.RiskWeight = Math.Clamp(s.RiskWeight, 0.65m, 1.35m);
        }


        public decimal GetGateStrictness(
            string symbol,
            MarketRegime regime,
            string gate)
        {
            if (!_decisionGates.TryGetValue(symbol, out var agg))
                return 1.0m;

            if (!agg.ByRegime.TryGetValue(regime, out var reg))
                return 1.0m;

            if (!reg.Gates.TryGetValue(gate, out var st))
                return 1.0m;

            if (st.BlockRate > 0.70m)
                return 0.85m;   // gate слишком строгий → ослабляем

            if (st.BlockRate < 0.15m)
                return 1.10m;   // gate почти не работает → усиливаем

            return 1.0m;
        }


        // =====================================================================
        // AI RISK (для StrategyEngine / AiLeverage / RiskManager)
        // =====================================================================
 
        public decimal GetAiRiskAdjustment(string symbol, MarketRegime regime) 
        {
            if (string.IsNullOrEmpty(symbol)) 
                return 1.00m; 
            if (!_stats.TryGetValue(symbol, out var regimes)) 
                return 1.00m; 
            if (!regimes.TryGetValue(regime, out var s)) 
                 return 1.00m;
            
            //return s?.RiskWeight ?? 1.00m;
            return Math.Clamp(s?.RiskWeight ?? 1.00m, 0.70m, 1.30m);
        }

        // =====================================================================
        // AI TREND PREDICTOR (QUANT-REALTIME MAX)
        // =====================================================================
        public AiTrendPrediction PredictTrend(
      string symbol,
      MarketRegime regime,
      decimal slope,
      decimal volatility)
        {
            List<MarketState> recent;

            lock (_lock)
            {
                recent = _marketStates
                    .Where(x => x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.Time)
                    .Take(80)  // Чуть больше истории для сглаживания
                    .ToList();
            }

            if (recent.Count < 10)
                return new AiTrendPrediction(0, 0.20m, 1.00m);

            decimal avgSlope = recent.Average(x => x.TrendSlopePercent);
            decimal avgConf = recent.Average(x => x.Confidence);
            decimal avgVol = recent.Average(x => x.VolatilityPercent);

            int dir = avgSlope > 0.001m ? 1 : avgSlope < -0.001m ? -1 : 0;
       
            decimal rawConfidence =
            Math.Clamp(
            Math.Abs(avgSlope) * 25m + avgConf,
            0.05m,
            0.85m);

            decimal confidence = ApplyConfidenceDecay(
            rawConfidence,
            recent[0].Time   // последнее состояние рынка
            );


            if (avgVol < 0.005m)
                confidence += 0.10m;

            confidence = Math.Min(confidence, 0.85m);

            decimal rrBias = dir == 0 ? 1.00m : 0.90m;

            return new AiTrendPrediction(dir, confidence, rrBias);
        }
         
        public AiLearningSnapshot ExportState()
        {
            lock (_lock)
            {
                return BuildSnapshot();
            }
        }
        // =====================================================================
        // IMPORT STATE
        // =====================================================================
        public void ImportState(AiLearningSnapshot snap)
        {
            if (snap == null)
                return;

            lock (_lock)
            {
                _stats.Clear();

                if (snap.Symbols == null)
                    return;

                foreach (var sym in snap.Symbols)
                {
                    if (string.IsNullOrWhiteSpace(sym.Symbol))
                        continue;

                    var regimes = new Dictionary<MarketRegime, RegimeStats>();
                    _stats[sym.Symbol] = regimes;

                    foreach (var r in sym.Regimes)
                    {
                        regimes[r.Regime] = new RegimeStats
                        {
                            Regime = r.Regime,
                            Count = r.Trades,
                            Wins = r.Wins,
                            Losses = r.Trades - r.Wins,
                            AvgPnl = r.Trades > 0 ? r.SumRr / r.Trades : 0m,
                            RiskWeight = 1.0m
                        };
                    }
                }
            }
        }


        // =====================================================================
        // SAVE / LOAD (v7 – с Meta-блоком, но совместимо со старым форматом)
        // =====================================================================
        private void Save(bool force)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var attr = File.GetAttributes(FilePath);
                    if (attr.HasFlag(FileAttributes.ReadOnly))
                        File.SetAttributes(FilePath, attr & ~FileAttributes.ReadOnly);
                }


                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                AiLearningSnapshot snapshot;

                lock (_lock)
                {
                    // НЕ пропускаем при force=true
                    if (!force)
                    {
                        if (_stats.Count == 0 && _tradeHistory.Count == 0)
                        {
                            _logger.LogWarning("[AI] Snapshot skipped: empty state");
                            return;
                        }
                    }

                    snapshot = BuildSnapshot();
                }

                var json = JsonSerializer.Serialize(snapshot, JsonOptions);

                bool saved = false;

                try
                {
                    SaveSnapshotAtomic(json);
                    saved = true;
                    _logger.LogInformation(
                        "[AI] Snapshot saved OK → symbols={Symbols}, states={States}, trades={Trades}",
                        snapshot.Meta.Symbols,
                        snapshot.Meta.MarketStates,
                        snapshot.Meta.Trades);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AI] SAVE ERROR (atomic)");
                }

                if (saved)
                {
                    _logger.LogInformation(
                        "[AI] Snapshot saved OK → symbols={Symbols}, states={States}, trades={Trades}",
                        snapshot.Meta.Symbols,
                        snapshot.Meta.MarketStates,
                        snapshot.Meta.Trades);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AI] SAVE ERROR");
            }
        }

        private static readonly Mutex _saveMutex =
    new(false, "Global\\VERTEX_AI_LEARNING_SAVE");

        private void SaveSnapshotAtomic(string json)
        {
            var tmpPath = FilePath + ".tmp";
            var backupPath = BackupPath;

            _saveMutex.WaitOne();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);

                File.WriteAllText(tmpPath, json, Encoding.UTF8);

                if (File.Exists(FilePath))
                {
                    // 🔒 ATOMIC replace
                    File.Replace(tmpPath, FilePath, backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tmpPath, FilePath);
                    File.Copy(FilePath, backupPath, overwrite: true);
                }
            }
            finally
            {
                _saveMutex.ReleaseMutex();
            }
        }


        private void Load()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        _logger.LogInformation("[AI] Directory created: {Directory}", dir);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[AI] Error creating directory: {Directory}", dir);
                    }
                }
 
                if (!File.Exists(FilePath))
                {
                    _logger.LogInformation("[AI] ai_learning.json not found → starting with empty in-memory state.");
                    // НИЧЕГО НЕ СОХРАНЯЕМ ЗДЕСЬ.
                    // Первый нормальный Save() сам создаст файл с реальными данными.
                    return;
                }

                AiLearningSnapshot? snap = null;
                string json;

                using (var fs = new FileStream(
                    FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite))   // 🔥 КЛЮЧ
                using (var sr = new StreamReader(fs))
                {
                    json = sr.ReadToEnd();
                }
                _logger.LogInformation("[AI] File read successfully: {FilePath}", FilePath);

                try
                {
                    snap = JsonSerializer.Deserialize<AiLearningSnapshot>(json, JsonOptions);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AI] Error deserializing ai_learning.json.");
                    snap = new AiLearningSnapshot();  // Если не получилось, создаём пустой объект
                }

                if (snap == null)
                {
                    _logger.LogWarning("[AI] Failed to load ai_learning.json. Using empty state.");
                    return;
                }

                ImportState(snap);

                lock (_lock)
                {
                    _marketStates.Clear();
                    _tradeHistory.Clear();

                    if (snap.MarketStates != null)
                        _marketStates.AddRange(snap.MarketStates);

                    if (snap.Trades != null)
                        _tradeHistory.AddRange(snap.Trades);
                }

                // =====================================================
                // RESTORE DECISION GATES (AFTER IMPORT STATE)
                // =====================================================
                if (snap.DecisionGates != null)
                {
                    _decisionGates.Clear();

                    foreach (var d in snap.DecisionGates)
                    {
                        if (!_decisionGates.TryGetValue(d.Symbol, out var agg))
                        {
                            agg = new DecisionTraceAggregate { Symbol = d.Symbol };
                            _decisionGates[d.Symbol] = agg;
                        }

                        if (!agg.ByRegime.TryGetValue(d.Regime, out var reg))
                        {
                            reg = new DecisionGateRegimeStats { Regime = d.Regime };
                            agg.ByRegime[d.Regime] = reg;
                        }

                        reg.Gates[d.Gate] = new DecisionGateStats
                        {
                            Gate = d.Gate,
                            Hits = d.Hits,
                            Blocks = d.Blocks
                        };
                    }

                    _logger.LogInformation(
                        "[AI] DecisionGates restored: symbols={Count}",
                        _decisionGates.Count);
                }


                _logger.LogInformation(
                    "[AI] ai_learning loaded successfully: Symbols={Symbols}, Trades={Trades}, States={States}",
                    snap.Meta?.Symbols ?? _stats.Count,
                    snap.Meta?.Trades ?? _tradeHistory.Count,
                    snap.Meta?.MarketStates ?? _marketStates.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AI] Fatal error loading ai_learning.json.");
            }
        }

        public void ForceSnapshot()
        {
            Save(force: true);
            _lastSnapshot = DateTime.UtcNow;
            _logger.LogInformation("[AI][SNAPSHOT] Force save triggered");
        }

        private void TrySnapshot()
        {
            var now = DateTime.UtcNow;
            if (now - _lastSnapshot < SnapshotInterval)
                return;

            Save(force: false);
            _lastSnapshot = now;

            _logger.LogInformation("[AI][SNAPSHOT] Saved (interval={Min} min)", SnapshotInterval.TotalMinutes);
        }

        private AiLearningSnapshot BuildSnapshot()
        {
            var snap = new AiLearningSnapshot
            {
                CreatedAtUtc = DateTime.UtcNow,
                SnapshotVersion = 8,
                Meta = new AiLearningMeta
                {
                    Engine = "AiSelfLearningService.v8"
                }
            };
            // Логируем состояние _stats перед добавлением в snapshot
            _logger.LogInformation("[AI] Building snapshot: {StatsCount} symbols found.", _stats.Count);

            // --- агрегированная статистика по символам/режимам ---
            foreach (var (symbol, regimes) in _stats)
            {
                var sym = new AiSymbolStatsDto
                {
                    Symbol = symbol
                };

                foreach (var (regimeKey, rs) in regimes)
                {
                    var regime = rs.Regime != MarketRegime.Unknown ? rs.Regime : regimeKey;

                    var dto = new AiRegimeStatsDto
                    {
                        Regime = regime,
                        Trades = rs.Count,
                        Wins = rs.Wins,
                        Losses = rs.Losses,
                        AvgPnl = rs.AvgPnl,
                        SumRr = rs.AvgPnl * rs.Count,
                        // при желании можно потом добавить реальный min/max RR
                        MaxRr = 0,
                        MinRr = 0,
                        LastUpdateUtc = DateTime.UtcNow
                    };

                    sym.Regimes.Add(dto);
                    snap.Meta.Trades += rs.Count;
                }

                snap.Symbols.Add(sym);
            }

            // =====================================================
            // DECISION GATES SNAPSHOT (NEW, NON-DESTRUCTIVE)
            // =====================================================
            snap.DecisionGates = new List<DecisionGateSnapshot>();

            foreach (var (symbol, agg) in _decisionGates)
            {
                foreach (var (regime, reg) in agg.ByRegime)
                {
                    foreach (var (gate, st) in reg.Gates)
                    {
                        snap.DecisionGates.Add(new DecisionGateSnapshot
                        {
                            Symbol = symbol,
                            Regime = regime,
                            Gate = gate,
                            Hits = st.Hits,
                            Blocks = st.Blocks
                        });
                    }
                }
            }


            // --- meta ---
            snap.Meta.Symbols = snap.Symbols.Count;
            snap.Meta.MarketStates = _marketStates.Count;

            // --- компактные ленты состояний/сделок для Dashboard ---
            snap.MarketStates = _marketStates
                .OrderByDescending(s => s.Time)
                .Take(500)
                .ToList();

            snap.Trades = _tradeHistory
                .OrderByDescending(t => t.Time)
                .Take(300)
                .ToList();

            // ✅ КОРРЕКТНЫЙ ЛОГ (ПОСЛЕ ФОРМИРОВАНИЯ)
            _logger.LogInformation(
                "[AI] Snapshot built OK → symbols={Symbols}, states={States}, trades={Trades}",
                snap.Meta.Symbols,
                snap.Meta.MarketStates,
                snap.Meta.Trades
                );
            return snap;
        } 

        // ---------------------------------------------------------------------
        // DASHBOARD EXPORT: MarketStates (фоновые данные)
        // ---------------------------------------------------------------------
        public IReadOnlyList<MarketState> GetRecentStates(int maxCount = 150)
        {
            lock (_lock)
            {
                return _marketStates
                    .OrderByDescending(s => s.Time)
                    .Take(maxCount)
                    .ToList();
            }
        }

        // ---------------------------------------------------------------------
        // DASHBOARD EXPORT: Trades (сделки для обучения)
        // ---------------------------------------------------------------------
        public IReadOnlyList<TradeHistoryEntry> GetRecentTrades(int maxCount = 100)
        {
            lock (_lock)
            {
                return _tradeHistory
                    .OrderByDescending(t => t.Time)
                    .Take(maxCount)
                    .ToList();
            }
        }

        // ---------------------------------------------------------------------
        // AI META: WinRate (for SymbolRegistry dynamic cap)
        // ---------------------------------------------------------------------
   
        public decimal GetWinRate(
    SignalSide side,
    int lastN = 30,
    int minTrades = 10)
        {
            lock (_lock)
            {
                var trades = _tradeHistory
                    .Where(t => t.Side == side)
                    .OrderByDescending(t => t.Time)
                    .Take(lastN)
                    .ToList();

                if (trades.Count < minTrades)
                    return 0.55m; // neutral bootstrap

                var wins = trades.Count(t => t.Pnl > 0);
                return (decimal)wins / trades.Count;
            }
        }
        public void RecordSimulatedTrade(
        string symbol,
        string side,
        decimal entry,
        decimal sl,
        decimal tp,
        decimal outcome,
        string reason)
        {
            if (!string.IsNullOrEmpty(reason) &&
    reason.StartsWith("FALLBACK_MKT_BLOCKED", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "[AI][SIM][{symbol}] Ignored execution-policy simulated trade | reason={reason}",
                    symbol, reason);
                return;
            }

            lock (_lock)
            {
                _logger.LogInformation(
                    "[AI-LEARN][SIM][{symbol}] side={side} entry={entry} sl={sl} tp={tp} result={outcome} reason={reason}",
                    symbol, side, entry, sl, tp, outcome, reason);

                // сохраняем как отдельный канал обучения
                if (!_learnBuffer.ContainsKey(symbol))
                    _learnBuffer[symbol] = new();

                _learnBuffer[symbol].Add(new LearningEvent
                {
                    Type = "SIMULATED_TRADE",
                    Side = side,
                    Entry = entry,
                    StopLoss = sl,
                    TakeProfit = tp,
                    Result = outcome,
                    Reason = reason,
                    Time = DateTime.UtcNow
                });
            }
            // === SOFT LEARNING IMPACT (SIMULATED) ===
            // симуляция влияет на RiskWeight, но слабее реальной сделки
            var pseudoPnl = outcome * 0.35m;

            // regime неизвестен → используем Unknown
            UpdateStats(
                symbol,
                MarketRegime.Unknown,
                pseudoPnl
            );
            // snapshot разрешён — это результат обучения
            TrySnapshot();
        }

        public decimal GetRecentPnL(
        string symbol,
        SignalSide side,
        int lookback = 10)
            {
            lock (_lock)
            {
                var now = DateTime.UtcNow;

                var trades = _tradeHistory
                    .Where(t =>
                        t.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) &&
                        t.Side == side)
                    .OrderByDescending(t => t.Time)
                    .Take(lookback)
                    .ToList();
                if (trades.Count == 0)
                    return 0m;
                decimal sum = 0m;
                foreach (var t in trades)
                {
                    var ageDays = (now - t.Time).TotalDays;
                    var decay = Math.Exp(-Math.Log(2) * ageDays / PnlHalfLifeDays);

                    sum += t.Pnl * (decimal)decay;
                }
                return sum;
            }
        }

        public decimal GetSymbolScore(string symbol, SignalSide side)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var trades = _tradeHistory
                    .Where(t =>
                        t.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) &&
                        t.Side == side)
                    .OrderByDescending(t => t.Time)
                    .Take(20)
                    .ToList();
                if (trades.Count == 0)
                    return 0m;
                decimal score = 0m;
                foreach (var t in trades)
                {
                    var ageDays = (now - t.Time).TotalDays;
                    var decay = Math.Exp(-Math.Log(2) * ageDays / PnlHalfLifeDays);
                    score += t.Pnl * (decimal)decay;
                }
                return score / trades.Count;
            }
        }

        private decimal ApplyHalfLife(DateTime tradeTime)
        {
            var age = DateTime.UtcNow - tradeTime;
            if (age <= TimeSpan.Zero)
                return 1.0m;

            // exp decay: weight = 0.5 ^ (age / halfLife)
            var decay =
                Math.Pow(
                    0.5,
                    age.TotalSeconds / PnlHalfLife.TotalSeconds
                );

            return Math.Max((decimal)decay, MinPnlWeight);
        }
      
        private const double Ln2 = 0.6931471805599453;

        private decimal ApplyConfidenceDecay(
            decimal confidence,
            DateTime lastUpdateUtc)
        {
            var dtMinutes =
                (DateTime.UtcNow - lastUpdateUtc).TotalMinutes;

            var halfLifeMinutes = 45.0;

            var decay =
                Math.Exp(-dtMinutes * Ln2 / halfLifeMinutes);

            var adjusted =
                confidence * (decimal)decay;

            return Math.Max(0.22m, adjusted);
        }

    }
}
