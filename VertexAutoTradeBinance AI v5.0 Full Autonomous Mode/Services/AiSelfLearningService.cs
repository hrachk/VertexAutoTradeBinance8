using System.Text.Json;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class AiSelfLearningService
    {
        private readonly ILogger<AiSelfLearningService> _logger;

        private readonly object _lock = new();

        private static readonly string FilePath =
            Path.Combine(AppContext.BaseDirectory, "ai_learning.json");

        private static readonly string BackupPath =
            Path.Combine(AppContext.BaseDirectory, "ai_learning_backup.json");

        private DateTime _lastSnapshot = DateTime.MinValue;
        private readonly TimeSpan SnapshotInterval = TimeSpan.FromMinutes(15);

        private readonly Dictionary<string, Dictionary<MarketRegime, RegimeStats>> _stats
            = new(StringComparer.OrdinalIgnoreCase);

        public AiSelfLearningService(ILogger<AiSelfLearningService> logger)
        {
            _logger = logger;
            Load();
        }

        // =====================================================================
        // INTERNAL MODELS
        // =====================================================================

        private class RegimeStats
        {
            public MarketRegime Regime { get; set; }
            public int Trades { get; set; }
            public int Wins { get; set; }
            public decimal SumRr { get; set; }
            public decimal MaxRr { get; set; } = decimal.MinValue;
            public decimal MinRr { get; set; } = decimal.MaxValue;
            public DateTime LastUpdateUtc { get; set; }

            public decimal AvgRr => Trades > 0 ? SumRr / Trades : 0m;
            public decimal WinRate => Trades > 0 ? (decimal)Wins / Trades : 0m;
        }

        public class AiRegimeStatsDto
        {
            public MarketRegime Regime { get; set; }
            public int Trades { get; set; }
            public int Wins { get; set; }
            public decimal SumRr { get; set; }
            public decimal MaxRr { get; set; }
            public decimal MinRr { get; set; }
            public DateTime LastUpdateUtc { get; set; }
        }

        public class AiSymbolStatsDto
        {
            public string Symbol { get; set; } = string.Empty;
            public List<AiRegimeStatsDto> Regimes { get; set; } = new();
        }

        /// <summary>
        /// Снапшот для сохранения/загрузки (бывший AiLearningState).
        /// Название изменено, чтобы не путаться с Models.AiLearningState.
        /// </summary>
        public class AiLearningSnapshot
        {
            public DateTime CreatedAtUtc { get; set; }
            public List<AiSymbolStatsDto> Symbols { get; set; } = new();
        }

        // =====================================================================
        // LOAD / SAVE
        // =====================================================================

        private void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(FilePath))
                    {
                        _logger.LogWarning("AI-Learning: file not found → starting fresh");
                        return;
                    }

                    string json = File.ReadAllText(FilePath);
                    if (string.IsNullOrWhiteSpace(json))
                        return;

                    var state = JsonSerializer.Deserialize<AiLearningSnapshot>(json);
                    if (state != null)
                        ImportState(state);

                    _logger.LogInformation("AI-Learning: memory loaded successfully ({FilePath})", FilePath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "AI-Learning: FAILED to load memory. Restoring from backup.");

                    // Восстановление из бэкапа
                    try
                    {
                        if (File.Exists(BackupPath))
                        {
                            var json = File.ReadAllText(BackupPath);
                            var state = JsonSerializer.Deserialize<AiLearningSnapshot>(json);

                            if (state != null)
                            {
                                ImportState(state);
                                _logger.LogWarning("AI-Learning: restored from backup file");
                            }
                        }
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogError(ex2, "AI-Learning: failed to restore from backup");
                    }
                }
            }
        }

        private void Save()
        {
            lock (_lock)
            {
                try
                {
                    var state = ExportState();
                    var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                    File.WriteAllText(FilePath, json);
                    File.WriteAllText(BackupPath, json);

                    _logger.LogDebug("AI-Learning: saved");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "AI-Learning: FAILED to save memory");
                }
            }
        }

        private void AutoSnapshotIfNeeded()
        {
            if (DateTime.UtcNow - _lastSnapshot > SnapshotInterval)
            {
                _lastSnapshot = DateTime.UtcNow;
                Save();
            }
        }

        // =====================================================================
        // REGISTER TRADE RESULT
        // =====================================================================

        public void RegisterTradeResult(
            string symbol,
            MarketRegime regime,
            decimal rr,
            bool isWin)
        {
            lock (_lock)
            {
                if (!_stats.TryGetValue(symbol, out var regimes))
                {
                    regimes = new Dictionary<MarketRegime, RegimeStats>();
                    _stats[symbol] = regimes;
                }

                if (!regimes.TryGetValue(regime, out var rs))
                {
                    rs = new RegimeStats { Regime = regime };
                    regimes[regime] = rs;
                }

                rs.Trades++;
                if (isWin)
                    rs.Wins++;

                rs.SumRr += rr;

                if (rr > rs.MaxRr) rs.MaxRr = rr;
                if (rr < rs.MinRr) rs.MinRr = rr;

                rs.LastUpdateUtc = DateTime.UtcNow;

                _logger.LogInformation(
                    "[AI-LEARN] {Symbol} regime={Regime}, trades={Trades}, win={Wins}, rr={RR:F2}",
                    symbol, regime, rs.Trades, rs.Wins, rr);

                Save();
                AutoSnapshotIfNeeded();
            }
        }

        // =====================================================================
        // DYNAMIC RISK WEIGHT
        // =====================================================================

        public decimal GetDynamicRiskWeight(string symbol, MarketRegime regime)
        {
            lock (_lock)
            {
                if (!_stats.TryGetValue(symbol, out var regimes) ||
                    !regimes.TryGetValue(regime, out var rs) ||
                    rs.Trades < 5)
                {
                    _logger.LogDebug(
                        "[AI-LEARN] RiskWeight {Symbol} {Regime}: no stats → 1.00",
                        symbol, regime);
                    return 1.0m;
                }

                decimal winRate = rs.WinRate;
                decimal avgRr = rs.AvgRr;

                decimal weight = 1.0m;

                if (winRate >= 0.65m && avgRr >= 1.20m)
                    weight = 1.35m;
                else if (winRate >= 0.55m && avgRr >= 1.00m)
                    weight = 1.15m;
                else if (winRate <= 0.45m && avgRr < 1.00m)
                    weight = 0.80m;
                else if (winRate <= 0.35m)
                    weight = 0.60m;

                weight = Math.Clamp(weight, 0.5m, 1.5m);

                _logger.LogInformation(
                    "[AI-LEARN] RiskWeight {Symbol} {Regime}: WR={WR:P1}, avgRR={RR:F2} → weight={W:F2}",
                    symbol, regime, winRate, avgRr, weight);

                return weight;
            }
        }

        // =====================================================================
        // EXPORT / IMPORT
        // =====================================================================

        public AiLearningSnapshot ExportState()
        {
            lock (_lock)
            {
                var state = new AiLearningSnapshot
                {
                    CreatedAtUtc = DateTime.UtcNow
                };

                foreach (var (symbol, regimes) in _stats)
                {
                    var symDto = new AiSymbolStatsDto { Symbol = symbol };

                    foreach (var rs in regimes.Values)
                    {
                        symDto.Regimes.Add(new AiRegimeStatsDto
                        {
                            Regime = rs.Regime,
                            Trades = rs.Trades,
                            Wins = rs.Wins,
                            SumRr = rs.SumRr,
                            MaxRr = rs.MaxRr == decimal.MinValue ? 0 : rs.MaxRr,
                            MinRr = rs.MinRr == decimal.MaxValue ? 0 : rs.MinRr,
                            LastUpdateUtc = rs.LastUpdateUtc
                        });
                    }

                    state.Symbols.Add(symDto);
                }

                return state;
            }
        }

        public void ImportState(AiLearningSnapshot? state)
        {
            lock (_lock)
            {
                if (state == null || state.Symbols.Count == 0)
                {
                    _logger.LogWarning("[AI-LEARN] ImportState: empty snapshot");
                    return;
                }

                _stats.Clear();

                foreach (var sym in state.Symbols)
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
                            Trades = r.Trades,
                            Wins = r.Wins,
                            SumRr = r.SumRr,
                            MaxRr = r.MaxRr,
                            MinRr = r.MinRr,
                            LastUpdateUtc = r.LastUpdateUtc
                        };
                    }
                }

                _logger.LogInformation(
                    "[AI-LEARN] ImportState: loaded {Symbols} symbols, regimes={Regimes}",
                    _stats.Count,
                    _stats.Values.Sum(v => v.Count));
            }
        }

        // =====================================================================
        // CONVENIENCE FOR SUPERVISOR / PROTECTORS
        // =====================================================================

        public void RecordTrade(
            string symbol,
            decimal entryPrice,
            decimal exitPrice,
            decimal liquidationPrice,
            bool isWin,
            MarketRegime regime,
            TradeSignal? signal = null)
        {
            // 1. Manual trades → не обучаем AI
            if (signal != null && signal.IsManual)
            {
                _logger.LogInformation(
                    "[AI-LEARN] Manual trade detected → SKIP learning. symbol={Symbol}, regime={Regime}",
                    symbol, regime);
                return;
            }

            if (entryPrice <= 0 || exitPrice <= 0)
                return;

            // RR по простому (можно улучшать позже)
            decimal rr = Math.Abs(exitPrice - entryPrice) /
                         Math.Max(1, Math.Abs(entryPrice * 0.001m));

            if (!isWin)
                rr = -Math.Abs(rr);

            RegisterTradeResult(symbol, regime, rr, isWin);
        }
    }
}
