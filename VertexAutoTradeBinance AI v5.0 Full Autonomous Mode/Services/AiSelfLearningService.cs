using System.Text.Json;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class AiSelfLearningService
    {
        private readonly ILogger<AiSelfLearningService> _logger;
        private readonly object _lock = new();

        private static readonly string FilePath =
            Path.Combine(AppContext.BaseDirectory, "ai-models/ai_learning.json");

        private static readonly string BackupPath =
            Path.Combine(AppContext.BaseDirectory, "ai-models/ai_learning_backup.json");

        private DateTime _lastSnapshot = DateTime.MinValue;
        private readonly TimeSpan SnapshotInterval = TimeSpan.FromMinutes(15);


        private DateTime _lastHybridSnapshot = DateTime.MinValue;
        private readonly TimeSpan HybridInterval = TimeSpan.FromSeconds(60);


        // =====================================================================
        // CORE STORAGE v6
        // =====================================================================
        private readonly Dictionary<string, Dictionary<MarketRegime, RegimeStats>> _stats
            = new(StringComparer.OrdinalIgnoreCase);

        // QUANT STORAGE
        private readonly List<MarketState> _marketStates = new();
        private readonly List<TradeHistoryEntry> _tradeHistory = new();

        // TREND MODEL
        public record AiTrendPrediction(int Direction, decimal Confidence, decimal RrBias);

        public AiSelfLearningService(ILogger<AiSelfLearningService> logger)
        {
            _logger = logger;
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
            public int Count { get; set; }
            public int Wins { get; set; }
            public int Losses { get; set; }
            public decimal AvgPnl { get; set; }
            public decimal RiskWeight { get; set; } = 1.0m;
        }

        // HYBRID: универсальный триггер логирования
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
                    Time = DateTime.UtcNow
                });

                if (_marketStates.Count > 2000)
                    _marketStates.RemoveRange(0, 1000);

                _logger.LogDebug($"[HYBRID][{symbol}] MarketState logged ({reason}) slope={slope} vol={volatility} atr={atr} conf={confidence}");
            }
        }

        // GLOBAL 60s periodic logger
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
                reason: "PERIODIC_60s",
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
        // MARKET STATE
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
                    Time = DateTime.UtcNow
                });

                if (_marketStates.Count > 2000)
                    _marketStates.RemoveRange(0, 1000);
            }
        }

        // =====================================================================
        // TRADE ENTRY (вызывается из PositionSupervisor / PositionProtector)
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

                if (_tradeHistory.Count > 2000)
                    _tradeHistory.RemoveRange(0, 1000);
            }

            UpdateStats(symbol, regime, pnl);
            Save();
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
                    .Where(x => x.Symbol == symbol)
                    .OrderByDescending(x => x.Time)
                    .Take(60)
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
        public AiLearningSnapshot ExportState()
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
        // SAVE / LOAD
        // =====================================================================
        private void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(_stats, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

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
                // === 1) Авто-создание файла, если он отсутствует ===
                if (!File.Exists(FilePath))
                {
                    var empty = JsonSerializer.Serialize(
                        new Dictionary<string, Dictionary<MarketRegime, RegimeStats>>(),
                        JsonOptions);

                    File.WriteAllText(FilePath, empty);
                    File.WriteAllText(BackupPath, empty);

                    _logger.LogInformation("[AI] Создан новый пустой ai_learning.json");
                    return;
                }


                var json = File.ReadAllText(FilePath);

                // Загружаем сырые данные как Dictionary<string, JsonElement>
                var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions);
                if (raw == null)
                    return;

                // Создаём чистый контейнер
                var cleaned = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

                foreach (var kv in raw)
                {
                    // 🎯 ВАЖНО: фильтруем весь мусор
                    if (kv.Key.Equals("CreatedAtUtc", StringComparison.OrdinalIgnoreCase)) continue;
                    if (kv.Key.Equals("SnapshotVersion", StringComparison.OrdinalIgnoreCase)) continue;
                    if (kv.Key.Equals("Meta", StringComparison.OrdinalIgnoreCase)) continue;

                    cleaned[kv.Key] = kv.Value;
                }

                // Теперь сериализуем очищенный JSON
                var purifiedJson = JsonSerializer.Serialize(cleaned, JsonOptions);

                // И десериализуем в нашу структуру
                var data = JsonSerializer.Deserialize<
                    Dictionary<string, Dictionary<MarketRegime, RegimeStats>>
                >(purifiedJson, JsonOptions);

                if (data != null)
                {
                    foreach (var kv in data)
                        _stats[kv.Key] = kv.Value;

                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AI] LOAD ERROR → попытка отката");
            }

            // === БЭКАП ===
            try
            {
                if (!File.Exists(BackupPath))
                    return;

                var json = File.ReadAllText(BackupPath);

                // Повторяем процесс загрузки бэкапа
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
                }
            }
            catch
            {
                // ignored
            }
        }
        // =====================================================================
        // PERIODIC SNAPSHOT (каждые 15 минут)
        // =====================================================================
        public void TrySnapshot()
        {
            lock (_lock)
            {
                if (DateTime.UtcNow - _lastSnapshot < SnapshotInterval)
                    return;

                Save();
                _lastSnapshot = DateTime.UtcNow;
            }
        }
    }
}
