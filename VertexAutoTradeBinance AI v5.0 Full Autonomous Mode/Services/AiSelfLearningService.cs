using System.Text.Json;
using VertexAutoTradeBinance8.Models;
using static VertexAutoTradeBinance8.Services.AiLearningSnapshot;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// AiSelfLearningService v7.0 (QUANT-REALTIME MAX, 3-Channel Learning)
    ///
    /// 1) Signal-Based Learning:
    ///    - RecordMarketStateTriggered(...)  → события сигналов/блокировок/soft-входов
    ///    - StrategyEngine вызывает:
    ///        • reason = "MICRO_SIGNAL"
    ///        • reason = "SOFT_ENTRY"
    ///        • reason = "AI_PATTERN_BLOCK"
    ///        • reason = "LIQUIDITY_DANGER"
    ///        • reason = "RR_BLOCK"
    ///
    /// 2) Trade-Based Learning:
    ///    - RecordTrade(symbol, side, entry, exit, regime)
    ///    - Вызывается из:
    ///        • PositionSupervisorService.UpdateSLAsync (trail / защита)
    ///        • TradeResultMonitorService.CheckClosedPositionAsync (факт закрытия)
    ///    - Строит _stats → используется GetAiRiskAdjustment(...)
    ///
    /// 3) Background Market Learning:
    ///    - RecordMarketState(...)         → базовые режимы из SmartRegimeService
    ///    - TryHybridPeriodicSnapshot(...) → периодический snapshot раз ~30 сек
    ///
    /// ФАЙЛ ХРАНЕНИЯ:
    ///   ai-models/ai_learning.json
    ///     - верхний уровень: { "SYMBOL": { "Regime": {...} }, ... }
    ///     - + служебные ключи: CreatedAtUtc, SnapshotVersion, Meta
    ///   Load() фильтрует служебные ключи и поднимает только реальную статистику.
    /// </summary>
    public class AiSelfLearningService
    {
        private readonly ILogger<AiSelfLearningService> _logger;
        private readonly object _lock = new();

        private static readonly string FilePath =
            Path.Combine(AppContext.BaseDirectory, "ai-models/ai_learning.json");

        private static readonly string BackupPath =
            Path.Combine(AppContext.BaseDirectory, "ai-models/ai_learning_backup.json");

        // Снимок статистики каждые N минут (для trade-based / signal-based)
        private DateTime _lastSnapshot = DateTime.MinValue;
        private readonly TimeSpan SnapshotInterval = TimeSpan.FromMinutes(15);

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

        // TREND MODEL
        public record AiTrendPrediction(int Direction, decimal Confidence, decimal RrBias);

        public AiSelfLearningService(ILogger<AiSelfLearningService> logger)
        {
            _logger = logger;

            // Гарантируем, что каталог существует, чтобы не было проблем с сохранением
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AI] Error creating ai-models directory");
            }

            Load();
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
            public MarketRegime Regime { get; set; }
        }

        // =====================================================================
        // 1) HYBRID: универсальный триггер логирования (signals + blocks + RR)
        // =====================================================================
        public void RecordMarketStateTriggered(
            string reason,
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

                // Ограничиваем память – скользящее окно
                if (_marketStates.Count > 5000)
                    _marketStates.RemoveRange(0, 2500);

                _logger.LogDebug(
                    "[HYBRID][{Symbol}] MarketState logged ({Reason}) slope={Slope} vol={Vol} atr={Atr} conf={Conf}",
                    symbol, reason, slope, volatility, atr, confidence);
            }

            // Периодически снимаем snapshot, даже если не было сделок
            TrySnapshot();
        }

        // =====================================================================
        // 3) BACKGROUND MARKET LEARNING – глобальный 30s snapshot по режиму
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
            if (DateTime.UtcNow - _lastHybridSnapshot < HybridInterval)
                return;

            _lastHybridSnapshot = DateTime.UtcNow;

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
            decimal pnl = (side == SignalSide.Buy)
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

            // Для сделок — сразу пишем на диск (они редкие, это безопасно)
            Save();

            bool win = side == SignalSide.Buy ? exit > entry : exit < entry;

            lock (_lock)
            {
                var rs = GetOrCreateRegimeStats(symbol, regime);

                rs.Trades++;
                if (win)
                    rs.Wins++;
            }

            TrySnapshot();
        }

        private void UpdateStats(string symbol, MarketRegime regime, decimal pnl)
        {
            if (!_stats.TryGetValue(symbol, out var regimes))
            {
                regimes = new Dictionary<MarketRegime, RegimeStats>();
                _stats[symbol] = regimes;
            }

            if (!regimes.TryGetValue(regime, out var s))
            {
                s = new RegimeStats();
                regimes[regime] = s;
            }

            s.Count++;
            if (pnl >= 0) s.Wins++;
            else s.Losses++;

            s.AvgPnl = (s.AvgPnl * (s.Count - 1) + pnl) / s.Count;

            if (s.Count >= 20)
            {
                decimal wr = s.Wins / (decimal)s.Count;
                s.RiskWeight = Math.Clamp(wr, 0.65m, 1.35m);
            }
        }

        // =====================================================================
        // AI RISK (для StrategyEngine / AiLeverage / RiskManager)
        // =====================================================================
        public decimal GetAiRiskAdjustment(string symbol, MarketRegime regime)
        {
            if (_stats.TryGetValue(symbol, out var regimes) &&
                regimes.TryGetValue(regime, out var s))
            {
                return s.RiskWeight;
            }

            return 1.00m;
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
                    .Take(80) // чуть больше истории для сглаживания
                    .ToList();
            }

            if (recent.Count < 10)
                return new AiTrendPrediction(0, 0.20m, 1.00m);

            decimal avgSlope = recent.Average(x => x.TrendSlopePercent);
            decimal avgConf = recent.Average(x => x.Confidence);
            decimal avgVol = recent.Average(x => x.VolatilityPercent);

            int dir =
                avgSlope > 0.001m ? 1 :
                avgSlope < -0.001m ? -1 : 0;

            decimal confidence =
                Math.Clamp(Math.Abs(avgSlope) * 25m + avgConf, 0.05m, 0.85m);

            if (avgVol < 0.005m)
                confidence += 0.10m;

            confidence = Math.Clamp(confidence, 0.05m, 0.85m);

            decimal rrBias = dir == 0 ? 1.00m : 0.90m;

            return new AiTrendPrediction(dir, confidence, rrBias);
        }

        // =====================================================================
        // EXPORT STATE (для TradingWorker v6 / AiModelSnapshotService)
        // =====================================================================
       /* public AiLearningSnapshot ExportState()
        {
            lock (_lock)
            {
                var snap = new AiLearningSnapshot
                {
                    CreatedAtUtc = DateTime.UtcNow
                };

                foreach (var (symbol, regimes) in _stats)
                {
                    var symDto = new AiLearningSnapshot.AiSymbolStatsDto
                    {
                        Symbol = symbol
                    };

                    foreach (var (regime, st) in regimes)
                    {
                        symDto.Regimes.Add(new AiLearningSnapshot.AiRegimeStatsDto
                        {
                            Regime = regime,
                            Trades = st.Count,
                            Wins = st.Wins,
                            SumRr = st.AvgPnl * st.Count,
                            MaxRr = 0,
                            MinRr = 0,
                            LastUpdateUtc = DateTime.UtcNow
                        });
                    }

                    snap.Symbols.Add(symDto);
                }

                return snap;
            }
        }*/
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
        private void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                // Корневой объект:
                //  • ключи-символы → RegimeStats (как раньше)
                //  • служебные поля → CreatedAtUtc / SnapshotVersion / Meta
                var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                foreach (var kv in _stats)
                {
                    root[kv.Key] = kv.Value; // Dictionary<MarketRegime, RegimeStats>
                }

                root["CreatedAtUtc"] = DateTime.UtcNow;
                root["SnapshotVersion"] = 7;
                root["Meta"] = new
                {
                    Engine = "AiSelfLearningService.v7",
                    Symbols = _stats.Count,
                    MarketStates = _marketStates.Count,
                    Trades = _tradeHistory.Count
                };

                var json = JsonSerializer.Serialize(root, JsonOptions);

                File.WriteAllText(FilePath, json);
                File.WriteAllText(BackupPath, json);
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
                    Directory.CreateDirectory(dir);

                // === 1) Авто-создание файла, если он отсутствует ===
                if (!File.Exists(FilePath))
                {
                    var emptyRoot = new Dictionary<string, object>
                    {
                        ["CreatedAtUtc"] = DateTime.UtcNow,
                        ["SnapshotVersion"] = 7,
                        ["Meta"] = new { Engine = "AiSelfLearningService.v7", Symbols = 0, MarketStates = 0, Trades = 0 }
                    };

                    var empty = JsonSerializer.Serialize(emptyRoot, JsonOptions);
                    File.WriteAllText(FilePath, empty);
                    File.WriteAllText(BackupPath, empty);

                    _logger.LogInformation("[AI] Создан новый ai_learning.json (v7, пустая статистика)");
                    return;
                }

                var json = File.ReadAllText(FilePath);

                // Загружаем сырые данные как Dictionary<string, JsonElement>
                var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions);
                if (raw == null)
                    return;

                // Чистим служебные ключи, оставляем только символы
                var cleaned = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

                foreach (var kv in raw)
                {
                    if (kv.Key.Equals("CreatedAtUtc", StringComparison.OrdinalIgnoreCase)) continue;
                    if (kv.Key.Equals("SnapshotVersion", StringComparison.OrdinalIgnoreCase)) continue;
                    if (kv.Key.Equals("Meta", StringComparison.OrdinalIgnoreCase)) continue;

                    cleaned[kv.Key] = kv.Value;
                }

                var purifiedJson = JsonSerializer.Serialize(cleaned, JsonOptions);

                var data = JsonSerializer.Deserialize<
                    Dictionary<string, Dictionary<MarketRegime, RegimeStats>>
                >(purifiedJson, JsonOptions);

                if (data != null)
                {
                    foreach (var kv in data)
                        _stats[kv.Key] = kv.Value;

                    _logger.LogInformation(
                        "[AI] ai_learning.json загружен: symbols={Count}",
                        _stats.Count);

                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AI] LOAD ERROR → попытка отката на backup");
            }

            // === БЭКАП ===
            try
            {
                if (!File.Exists(BackupPath))
                    return;

                var json = File.ReadAllText(BackupPath);

                var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions);
                if (raw == null)
                    return;

                var cleaned = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in raw)
                {
                    if (kv.Key.Equals("CreatedAtUtc", StringComparison.OrdinalIgnoreCase)) continue;
                    if (kv.Key.Equals("SnapshotVersion", StringComparison.OrdinalIgnoreCase)) continue;
                    if (kv.Key.Equals("Meta", StringComparison.OrdinalIgnoreCase)) continue;

                    cleaned[kv.Key] = kv.Value;
                }

                var purifiedJson = JsonSerializer.Serialize(cleaned, JsonOptions);

                var data = JsonSerializer.Deserialize<
                    Dictionary<string, Dictionary<MarketRegime, RegimeStats>>
                >(purifiedJson, JsonOptions);

                if (data != null)
                {
                    foreach (var kv in data)
                        _stats[kv.Key] = kv.Value;

                    _logger.LogInformation(
                        "[AI] ai_learning_backup.json восстановлен: symbols={Count}",
                        _stats.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AI] BACKUP LOAD ERROR");
            }
        }

        // =====================================================================
        // PERIODIC SNAPSHOT (каждые 15 минут) – вызывается:
        //  • снаружи (TradingWorker)
        //  • из RecordMarketStateTriggered (после событий)
        // =====================================================================
        //public void TrySnapshot()
        //{
        //    lock (_lock)
        //    {
        //        if (DateTime.UtcNow - _lastSnapshot < SnapshotInterval)
        //            return;

        //        _lastSnapshot = DateTime.UtcNow;
        //    }

        //    // Сохранение делаем вне lock, чтобы не блокировать обработку сигналов
        //    Save();
        //}
        private void TrySnapshot()
        {
            if (DateTime.UtcNow - _lastSnapshot < SnapshotInterval)
                return;

            _lastSnapshot = DateTime.UtcNow;

            try
            {
                var snapshot = BuildSnapshot();

                var json = JsonSerializer.Serialize(
                    snapshot,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                File.WriteAllText(FilePath, json);

                // простой бэкап
                File.Copy(FilePath, BackupPath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AI-LEARN] Snapshot save error");
            }
        }


        private AiLearningSnapshot BuildSnapshot()
        {
            var snap = new AiLearningSnapshot
            {
                CreatedAtUtc = DateTime.UtcNow,
                SnapshotVersion = 7,
                Meta = new AiLearningMeta
                {
                    Engine = "AiSelfLearningService.v7"
                }
            };

            foreach (var (symbol, regimes) in _stats)
            {
                var sym = new AiSymbolStatsDto
                {
                    Symbol = symbol
                };

                foreach (var kv in regimes)
                {
                    var rs = kv.Value;

                    sym.Regimes.Add(new AiRegimeStatsDto
                    {
                        Regime = rs.Regime,
                        Trades = rs.Count,
                        Wins = rs.Wins,
                        Losses = rs.Losses,
                        AvgPnl = rs.AvgPnl
                    });

                    snap.Meta.Trades += rs.Count;
                }

                snap.Symbols.Add(sym);
            }

            snap.Meta.Symbols = snap.Symbols.Count;
            snap.Meta.MarketStates = _marketStates.Count;

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

        private RegimeStats GetOrCreateRegimeStats(string symbol, MarketRegime regime)
        {
           

            if (!_stats.TryGetValue(symbol, out var regimes))
            {
                regimes = new Dictionary<MarketRegime, RegimeStats>();
                _stats[symbol] = regimes;
            }

            if (!regimes.TryGetValue(regime, out var rs))
            {
                rs = new RegimeStats();
                regimes[regime] = rs;
            }

            return rs;
        }



    }
}
