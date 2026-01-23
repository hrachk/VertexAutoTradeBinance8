using System.Text.Json;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Strategy;

namespace VertexAutoTradeBinance8.Services
{
    internal sealed class RegimeGateProfile
    {
        public MarketRegime Regime { get; init; }

        // базовые веса (ручной дизайн)
        public Dictionary<string, decimal> BaseWeights { get; }
            = new(StringComparer.OrdinalIgnoreCase);

        // 🔥 АДАПТИВНЫЕ ВЕСА (AI, СОХРАНЯЮТСЯ)
        public Dictionary<string, decimal> AdaptiveBias { get; }
            = new(StringComparer.OrdinalIgnoreCase);

        public decimal GetWeight(string gate)
        {
            BaseWeights.TryGetValue(gate, out var baseW);
            AdaptiveBias.TryGetValue(gate, out var bias);

            var w =
                (baseW == 0 ? 1.0m : baseW) *
                (bias == 0 ? 1.0m : bias);

            return Math.Clamp(w, 0.70m, 1.30m);
        }
    }
    public class AiSelfLearningService
    {
        // symbol-agnostic, режим — главный ключ
        private readonly Dictionary<MarketRegime, RegimeGateProfile> _gateProfiles = new();

        private DateTime _lastForceSaveUtc = DateTime.MinValue;

        private readonly ILogger<AiSelfLearningService> _logger;
        private readonly object _lock = new();

        private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "ai-models", "ai_learning.json");

        private static readonly string BackupPath = Path.Combine(AppContext.BaseDirectory, "ai-models", "ai_learning_backup.json");

        // Снимок статистики каждые N минут (для trade-based / signal-based)
        private DateTime _lastSnapshot = DateTime.MinValue;
        private readonly TimeSpan SnapshotInterval = TimeSpan.FromMinutes(5);

        // Глобальный HYBRID snapshot раз в N секунд (background learning)
        private DateTime _lastHybridSnapshot = DateTime.MinValue;

        // Было 60s, делаю более «квантовым» — 30s (в твои 15–30s)
        private readonly TimeSpan HybridInterval = TimeSpan.FromSeconds(30);

        private readonly Dictionary<string, Dictionary<MarketRegime, RegimeStats>> _stats
      = new(StringComparer.OrdinalIgnoreCase);

        private readonly List<MarketState> _marketStates = new();
        private readonly List<TradeHistoryEntry> _tradeHistory = new();
        // TREND MODEL
        public record AiTrendPrediction(int Direction, decimal Confidence, decimal RrBias);

        private Dictionary<string, List<LearningEvent>> _learnBuffer = new Dictionary<string, List<LearningEvent>>();
        // Decision intelligence layer (NEW)
        private readonly Dictionary<string, DecisionTraceAggregate> _decisionGates
            = new(StringComparer.OrdinalIgnoreCase);

        public AiSelfLearningService(ILogger<AiSelfLearningService> logger)
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

            // 2) Потом подгружаем missed_trades.json и учимся на них БЕЗ снапшотов
            var missed = LoadMissedTradesFromFile();

            missed = missed
                .GroupBy(x => $"{x.Symbol}-{x.Time:O}-{x.Reason}")
                .Select(g => g.First())
                .ToList();

            foreach (var m in missed)
                LearnFromMissedTrade(m);   // внутри будет skipSnapshot = true

            _logger.LogInformation("[AI] Loaded and trained on {Count} missed trades.", missed.Count);

            // 3) Один финальный снапшот (обновляем файл с учётом старых + новых данных)
            ForceSnapshot();
        }


        public decimal GetGateMultiplier(string symbol, MarketRegime regime, string gate)
        {
            // v1: symbol пока не участвует — оставлен для будущего symbol-aware обучения
            EnsureGateProfiles();
            return _gateProfiles.TryGetValue(regime, out var p)
                ? p.GetWeight(gate)
                : 1.0m;
        }

        private void EnsureGateProfiles()
        {
            // thread-safe lazy init
            if (_gateProfiles.Count > 0)
                return;

            void add(MarketRegime r, Action<Dictionary<string, decimal>> cfg)
            {
                if (_gateProfiles.ContainsKey(r)) return;

                var p = new RegimeGateProfile
                {
                    Regime = r
                };

                cfg(p.BaseWeights);
                _gateProfiles[r] = p;
            }

            // ===== STRONG TRENDS =====
            add(MarketRegime.StrongUpTrend, w =>
            {
                w["RR"] = 0.90m;
                w["PATTERN"] = 0.95m;
                w["LIQ"] = 1.05m;
                w["EXPO"] = 1.00m;
            });

            add(MarketRegime.StrongDownTrend, w =>
            {
                w["RR"] = 0.90m;
                w["PATTERN"] = 0.95m;
                w["LIQ"] = 1.05m;
                w["EXPO"] = 1.00m;
            });

            // ===== NORMAL TRENDS =====
            add(MarketRegime.UpTrend, w =>
            {
                w["RR"] = 0.95m;
                w["PATTERN"] = 1.00m;
                w["LIQ"] = 1.00m;
                w["EXPO"] = 1.00m;
            });

            add(MarketRegime.DownTrend, w =>
            {
                w["RR"] = 0.95m;
                w["PATTERN"] = 1.00m;
                w["LIQ"] = 1.00m;
                w["EXPO"] = 1.00m;
            });

            // ===== RANGE / SQUEEZE / CHOP =====
            add(MarketRegime.Range, w =>
            {
                w["RR"] = 1.15m;
                w["PATTERN"] = 1.10m;
                w["LIQ"] = 0.95m;
                w["EXPO"] = 1.00m;
            });

            add(MarketRegime.Squeeze, w =>
            {
                w["RR"] = 1.10m;
                w["PATTERN"] = 1.05m;
                w["LIQ"] = 0.90m;
                w["EXPO"] = 0.95m;
            });

            add(MarketRegime.VolatileChop, w =>
            {
                w["RR"] = 1.20m;
                w["PATTERN"] = 1.15m;
                w["LIQ"] = 0.85m;
                w["EXPO"] = 0.90m;
            });

            // ===== UNKNOWN =====
            add(MarketRegime.Unknown, _ => { });
        }

        private List<MissedTradeRecord> LoadMissedTradesFromFile()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "missed_trades.json");

            if (!File.Exists(path))
                return new List<MissedTradeRecord>();

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<List<MissedTradeRecord>>(json, JsonOptions)
                       ?? new List<MissedTradeRecord>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AI] Failed to load missed_trades.json");
                return new List<MissedTradeRecord>();
            }
        }

        private void LearnFromMissedTrade(MissedTradeRecord r)
        {
            try
            {
                RecordMarketStateTriggered(
                    reason: r.Reason,
                    symbol: r.Symbol,
                    timeframe: "MissedTrade",
                    regime: ParseMarketRegime(r.Regime),
                    slope: r.Slope,
                    volatility: r.Vol,
                    atr: r.Atr,
                    confidence: r.Confidence,
                    skipSnapshot: true
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AI] Failed to learn from missed trade");
            }
        }

        private MarketRegime ParseMarketRegime(string regime)
        {
            return Enum.TryParse<MarketRegime>(regime, out var parsed)
                ? parsed
                : MarketRegime.Unknown;
        }

        // =====================================================================
        // JSON OPTIONS (CANONICAL)
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
        // DECISION TRACE LEARNING (CANONICAL, SAFE)
        // =====================================================================
        public void RecordDecisionTrace(
            string symbol,
            MarketRegime regime,
            IReadOnlyList<FastFailResult> gates)
        {
            if (regime == MarketRegime.Unknown)
                return;

            EnsureGateProfiles();

            if (!_gateProfiles.TryGetValue(regime, out var profile))
                return;

            // --- ADAPTIVE GATE BIAS (OUTSIDE LOCK) ---
            foreach (var g in gates)
            {
                var strict = GetGateStrictness(symbol, regime, g.Gate);

                profile.AdaptiveBias.TryGetValue(g.Gate, out var cur);

                var updated = cur == 0
                    ? strict
                    : (cur * 0.9m + strict * 0.1m);

                profile.AdaptiveBias[g.Gate] =
                    Math.Clamp(updated, 0.85m, 1.15m);
            }

            // --- DECISION TRACE STATS ---
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
     bool skipSnapshot = false)
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

                if (_marketStates.Count > 5000)
                    _marketStates.RemoveRange(0, 2500);

                _logger.LogDebug(
                    "[HYBRID][{Symbol}] MarketState logged ({Reason}) slope={Slope} vol={Vol} atr={Atr} conf={Conf}",
                    symbol, reason, slope, volatility, atr, confidence);
            }

            if (!skipSnapshot)
                TrySnapshot();
        }

        // =====================================================================
        // BACKGROUND MARKET LEARNING – GLOBAL 30s SNAPSHOT
        // =====================================================================
        public void TryHybridPeriodicSnapshot(
            string symbol,
            string timeframe,
            MarketRegime regime,
            decimal slope,
            decimal volatility,
            decimal atr,
            decimal confidence)
        {
            lock (_lock)
            {
                if (DateTime.UtcNow - _lastHybridSnapshot < HybridInterval)
                    return;

                _lastHybridSnapshot = DateTime.UtcNow;
            }

            RecordMarketStateTriggered(
                reason: "PERIODIC_30s",
                symbol: symbol,
                timeframe: timeframe,
                regime: regime,
                slope: slope,
                volatility: volatility,
                atr: atr,
                confidence: confidence
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
            }
        }

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
            var pnl = side == SignalSide.Buy
                ? exit - entry
                : entry - exit;

            lock (_lock)
            {
                _tradeHistory.Add(new TradeHistoryEntry
                {
                    Symbol = symbol,
                    Side = side,
                    Entry = entry,
                    Exit = exit,
                    Regime = regime,
                    Pnl = pnl,
                    Time = DateTime.UtcNow
                });

                if (_tradeHistory.Count > 5000)
                    _tradeHistory.RemoveRange(0, 2500);
            }

            UpdateStats(symbol, regime, pnl);

            Save(force: true);

            var win = side == SignalSide.Buy
                ? exit > entry
                : exit < entry;

            lock (_lock)
            {
                var rs = GetOrCreateRegimeStats(symbol, regime);
                rs.Trades++;
                if (win)
                    rs.Wins++;
            }

            TrySnapshot();
        }

        private void UpdateStats(
            string symbol,
            MarketRegime regime,
            decimal pnl)
        {
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

            // ===== BASE STATS =====
            s.Count++;
            if (pnl >= 0) s.Wins++;
            else s.Losses++;

            s.AvgPnl = (s.AvgPnl * (s.Count - 1) + pnl) / s.Count;

            if (s.Count >= 20)
            {
                var wr = s.Wins / (decimal)s.Count;
                s.RiskWeight = Math.Clamp(wr, 0.65m, 1.35m);
            }

            // ===== PnL-WEIGHTED LEARNING =====
            var absPnl = Math.Abs(pnl);
            if (absPnl <= 0)
                return;

            const decimal K = 25m;
            const decimal minWeight = 0.3m;
            const decimal maxWeight = 1.3m;
            const decimal lossMultiplier = 1.6m;

            var weight = Math.Clamp(absPnl * K, minWeight, maxWeight);

            if (pnl < 0)
                weight *= lossMultiplier;

            s.RiskWeight *= pnl >= 0
                ? weight
                : 1m / weight;

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
                return 0.85m;

            if (st.BlockRate < 0.15m)
                return 1.10m;

            return 1.0m;
        }

        // =====================================================================
        // AI RISK
        // =====================================================================
        public decimal GetAiRiskAdjustment(
            string symbol,
            MarketRegime regime)
        {
            if (_stats.TryGetValue(symbol, out var regimes) &&
                regimes.TryGetValue(regime, out var s))
                return s.RiskWeight;

            return 1.0m;
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
                    .Take(80)
                    .ToList();
            }

            if (recent.Count < 10)
                return new AiTrendPrediction(0, 0.20m, 1.00m);

            var avgSlope = recent.Average(x => x.TrendSlopePercent);
            var avgConf = recent.Average(x => x.Confidence);
            var avgVol = recent.Average(x => x.VolatilityPercent);

            var dir =
                avgSlope > 0.001m ? 1 :
                avgSlope < -0.001m ? -1 : 0;

            var confidence = Math.Clamp(
                Math.Abs(avgSlope) * 25m + avgConf,
                0.05m,
                0.85m);

            if (avgVol < 0.005m)
                confidence = Math.Min(0.85m, confidence + 0.10m);

            var rrBias = dir == 0 ? 1.00m : 0.90m;

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

                var symbols = snap.Symbols ?? new List<AiSymbolStatsDto>();

                foreach (var sym in symbols)
                {
                    if (sym == null || string.IsNullOrWhiteSpace(sym.Symbol))
                        continue;

                    var regimes = new Dictionary<MarketRegime, RegimeStats>();
                    _stats[sym.Symbol] = regimes;

                    var symRegimes = sym.Regimes ?? new List<AiRegimeStatsDto>();

                    foreach (var r in symRegimes)
                    {
                        if (r == null)
                            continue;

                        var trades = Math.Max(0, r.Trades);
                        var wins = Math.Clamp(r.Wins, 0, trades);

                        regimes[r.Regime] = new RegimeStats
                        {
                            Regime = r.Regime,
                            Count = trades,                 // Count == Trades
                            Trades = trades,                // (если где-то используешь Trades отдельно)
                            Wins = wins,
                            Losses = Math.Max(0, trades - wins),
                            AvgPnl = trades > 0 ? (r.SumRr / trades) : 0m,
                            RiskWeight = 1.0m
                        };
                    }
                }
            }
        }


        // =====================================================================
        // SAVE / LOAD (v7 – BACKWARD COMPATIBLE)
        // =====================================================================
        private void Save(bool force)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                AiLearningSnapshot snapshot;

                lock (_lock)
                {
                    if (!force &&
                        _stats.Count == 0 &&
                        _tradeHistory.Count == 0 &&
                        _marketStates.Count == 0)
                    {
                        _logger.LogWarning("[AI] Snapshot skipped: empty state");
                        return;
                    }

                    snapshot = BuildSnapshot();
                }

                var json = JsonSerializer.Serialize(snapshot, JsonOptions);

                File.WriteAllText(FilePath, json);
                File.Copy(FilePath, BackupPath, overwrite: true);

                _logger.LogInformation(
                    "[AI] Snapshot saved OK → symbols={Symbols}, states={States}, trades={Trades}",
                    snapshot.Meta.Symbols,
                    snapshot.Meta.MarketStates,
                    snapshot.Meta.Trades);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AI] SAVE ERROR");
            }
        }

        private void Load()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                    _logger.LogInformation("[AI] Directory ready: {Directory}", dir);
                }

                if (!File.Exists(FilePath))
                {
                    _logger.LogInformation(
                        "[AI] ai_learning.json not found → starting with empty in-memory state");
                    return;
                }

                var json = File.ReadAllText(FilePath);
                _logger.LogInformation("[AI] File read successfully: {FilePath}", FilePath);

                AiLearningSnapshot snap;
                try
                {
                    snap = JsonSerializer.Deserialize<AiLearningSnapshot>(json, JsonOptions)
                           ?? new AiLearningSnapshot();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AI] Error deserializing ai_learning.json");
                    snap = new AiLearningSnapshot();
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
                // RESTORE DECISION GATES
                // =====================================================
                if (snap.DecisionGates != null)
                {
                    lock (_lock)
                    {
                        _decisionGates.Clear();

                        foreach (var d in snap.DecisionGates)
                        {
                            if (d == null || string.IsNullOrWhiteSpace(d.Symbol) || string.IsNullOrWhiteSpace(d.Gate))
                                continue;

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
                    }

                    _logger.LogInformation("[AI] DecisionGates restored: symbols={Count}", _decisionGates.Count);
                }


                _logger.LogInformation(
                    "[AI] ai_learning loaded successfully: Symbols={Symbols}, Trades={Trades}, States={States}",
                    snap.Meta?.Symbols ?? _stats.Count,
                    snap.Meta?.Trades ?? _tradeHistory.Count,
                    snap.Meta?.MarketStates ?? _marketStates.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AI] Fatal error loading ai_learning.json");
            }
        }


        public void ForceSnapshot()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;

                // anti-spam: не чаще чем раз в 30 секунд
                if (now - _lastForceSaveUtc < TimeSpan.FromSeconds(30))
                {
                    _logger.LogDebug("[AI][SNAPSHOT] ForceSnapshot skipped (anti-spam)");
                    return;
                }

                Save(force: true);
                _lastSnapshot = now;
                _lastForceSaveUtc = now;

                _logger.LogInformation("[AI][SNAPSHOT] Force save triggered");
            }
        }

        private void TrySnapshot()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;

                if (now - _lastSnapshot < SnapshotInterval)
                    return;

                Save(force: false);
                _lastSnapshot = now;

                _logger.LogInformation(
                    "[AI][SNAPSHOT] Saved (interval={Min} min)",
                    SnapshotInterval.TotalMinutes);
            }
        }

        private AiLearningSnapshot BuildSnapshot()
        {
            var snap = new AiLearningSnapshot
            {
                CreatedAtUtc = DateTime.UtcNow,
                SnapshotVersion = 8,
                Meta = new AiLearningMeta
                {
                    Engine = "AiSelfLearningService.v8",
                    Symbols = 0,
                    Trades = 0,
                    MarketStates = 0
                },

                // ВАЖНО: если в твоих моделях это не инициализировано конструктором — делаем здесь
                Symbols = new List<AiSymbolStatsDto>(),
                MarketStates = new List<MarketState>(),
                Trades = new List<TradeHistoryEntry>(),
                DecisionGates = new List<DecisionGateSnapshot>()
            };


            _logger.LogInformation(
                "[AI] Building snapshot: statsSymbols={StatsSymbols}, stateSymbols≈{StateSymbols}, trades={TradesCount}, states={StatesCount}",
                _stats.Count,
                _marketStates.Select(x => x.Symbol)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                _tradeHistory.Count,
                _marketStates.Count);

            // =====================================================
            // SYMBOL / REGIME STATS
            // =====================================================
            foreach (var (symbol, regimes) in _stats)
            {
                var sym = new AiSymbolStatsDto
                {
                    Symbol = symbol
                };

                foreach (var (regimeKey, rs) in regimes)
                {
                    var regime = rs.Regime != MarketRegime.Unknown
                        ? rs.Regime
                        : regimeKey;

                    var dto = new AiRegimeStatsDto
                    {
                        Regime = regime,
                        Trades = rs.Count,
                        Wins = rs.Wins,
                        Losses = rs.Losses,
                        AvgPnl = rs.AvgPnl,
                        SumRr = rs.AvgPnl * rs.Count,
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
            // DECISION GATES SNAPSHOT
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

            // =====================================================
            // META
            // =====================================================
            snap.Meta.Symbols = snap.Symbols.Count;
            snap.Meta.MarketStates = _marketStates.Count;

            // =====================================================
            // DASHBOARD DATA
            // =====================================================
            snap.MarketStates = _marketStates
                .OrderByDescending(s => s.Time)
                .Take(500)
                .ToList();

            snap.Trades = _tradeHistory
                .OrderByDescending(t => t.Time)
                .Take(300)
                .ToList();

            _logger.LogInformation(
                "[AI] Snapshot built OK → symbols={Symbols}, states={States}, trades={Trades}",
                snap.Meta.Symbols,
                snap.Meta.MarketStates,
                snap.Meta.Trades);

            return snap;
        }

        // ---------------------------------------------------------------------
        // DASHBOARD EXPORT: MarketStates
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

        private RegimeStats GetOrCreateRegimeStats(
            string symbol,
            MarketRegime regime)
        {
            if (!_stats.TryGetValue(symbol, out var regimes))
            {
                regimes = new Dictionary<MarketRegime, RegimeStats>();
                _stats[symbol] = regimes;
            }

            if (!regimes.TryGetValue(regime, out var rs))
            {
                rs = new RegimeStats
                {
                    Regime = regime
                };
                regimes[regime] = rs;
            }
            else if (rs.Regime == MarketRegime.Unknown)
            {
                rs.Regime = regime;
            }

            return rs;
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
            lock (_lock)
            {
                _logger.LogInformation(
                    "[AI-LEARN][SIM][{symbol}] side={side} entry={entry} sl={sl} tp={tp} result={outcome} reason={reason}",
                    symbol, side, entry, sl, tp, outcome, reason);

                if (!_learnBuffer.TryGetValue(symbol, out var list))
                {
                    list = new List<LearningEvent>();
                    _learnBuffer[symbol] = list;
                }

                list.Add(new LearningEvent
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
        }
    }
}
