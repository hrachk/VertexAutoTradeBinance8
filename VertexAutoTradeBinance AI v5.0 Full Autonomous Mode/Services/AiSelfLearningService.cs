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

        // =====================================================================
        // CORE STORAGE (твоя оригинальная структура)
        // =====================================================================
        private readonly Dictionary<string, Dictionary<MarketRegime, RegimeStats>> _stats
            = new(StringComparer.OrdinalIgnoreCase);

        // =====================================================================
        // ML MODULE: расширенная статистика по сигналам
        // =====================================================================

        private readonly Dictionary<string, AiSymbolMlStats> _mlStats
            = new(StringComparer.OrdinalIgnoreCase);

        // ---------------- ML internal model ----------------
        private class AiSymbolMlStats
        {
            public string Symbol { get; }
            public int TotalTrades { get; private set; }
            public decimal EmaRr { get; private set; }
            public decimal EmaWinRate { get; private set; }

            // контекст по режимам
            public readonly Dictionary<MarketRegime, MlRegimeStats> Regimes
                = new();

            public AiSymbolMlStats(string symbol)
            {
                Symbol = symbol;
            }

            public void Update(decimal rr, bool isWin, MarketRegime regime)
            {
                TotalTrades++;

                // EMA RR
                if (TotalTrades == 1)
                    EmaRr = rr;
                else
                    EmaRr = EmaRr * 0.90m + rr * 0.10m;

                // EMA winrate
                decimal winVal = isWin ? 1m : 0m;
                if (TotalTrades == 1)
                    EmaWinRate = winVal;
                else
                    EmaWinRate = EmaWinRate * 0.95m + winVal * 0.05m;

                // per-regime stats
                if (!Regimes.TryGetValue(regime, out var rs))
                {
                    rs = new MlRegimeStats(regime);
                    Regimes[regime] = rs;
                }

                rs.Update(rr, isWin);
            }
        }

        private class MlRegimeStats
        {
            public MarketRegime Regime { get; }
            public int Trades { get; private set; }
            public decimal EmaRr { get; private set; }
            public decimal EmaWinRate { get; private set; }

            public MlRegimeStats(MarketRegime reg)
            {
                Regime = reg;
            }

            public void Update(decimal rr, bool isWin)
            {
                Trades++;

                if (Trades == 1)
                    EmaRr = rr;
                else
                    EmaRr = EmaRr * 0.9m + rr * 0.1m;

                decimal winVal = isWin ? 1m : 0m;

                if (Trades == 1)
                    EmaWinRate = winVal;
                else
                    EmaWinRate = EmaWinRate * 0.95m + winVal * 0.05m;
            }
        }

        // =====================================================================
        // ORIGINAL MODELS (без изменений)
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

        public class AiLearningSnapshot
        {
            public DateTime CreatedAtUtc { get; set; }
            public List<AiSymbolStatsDto> Symbols { get; set; } = new();
        }

        // =====================================================================
        // LOAD / SAVE (оставил твой код 1:1)
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
                    if (state != null && state.Symbols.Count > 0)
                    {
                        ImportState(state);
                        _logger.LogInformation("AI-Learning: memory loaded successfully ({FilePath})", FilePath);
                    }
                    else
                    {
                        _logger.LogWarning("AI-Learning: file exists but contains NO symbols → skip load");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "AI-Learning: FAILED to load memory. Restoring from backup.");

                    try
                    {
                        if (File.Exists(BackupPath))
                        {
                            var json = File.ReadAllText(BackupPath);
                            var state = JsonSerializer.Deserialize<AiLearningSnapshot>(json);

                            if (state != null && state.Symbols.Count > 0)
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
                    if (_stats.Count == 0)
                    {
                        _logger.LogWarning("AI-Learning: SAVE skipped → no symbols in memory");
                        return;
                    }

                    bool hasTrades = _stats.Values.Any(r => r.Values.Any(x => x.Trades > 0));
                    if (!hasTrades)
                    {
                        _logger.LogWarning("AI-Learning: SAVE skipped → no trades recorded");
                        return;
                    }

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

                if (_stats.Count > 0 && _stats.Values.Any(r => r.Values.Any(x => x.Trades > 0)))
                    Save();
            }
        }

        // =====================================================================
        // TRADE REGISTRATION + ML UPDATE
        // =====================================================================

        public void RegisterTradeResult(
            string symbol,
            MarketRegime regime,
            decimal rr,
            bool isWin)
        {
            lock (_lock)
            {
                // ===========================
                // ORIGINAL STATISTICS
                // ===========================
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

                // ===========================
                // NEW ML ENGINE UPDATE
                // ===========================
                if (!_mlStats.TryGetValue(symbol, out var ml))
                {
                    ml = new AiSymbolMlStats(symbol);
                    _mlStats[symbol] = ml;
                }

                ml.Update(rr, isWin, regime);

                _logger.LogInformation(
                    "[AI-LEARN] {Symbol} regime={Regime}, trades={Trades}, win={Wins}, rr={RR:F2}, emaRR={emaR:F2}, emaWin={ew:F2}",
                    symbol, regime, rs.Trades, rs.Wins, rr, ml.EmaRr, ml.EmaWinRate);

                Save();
                AutoSnapshotIfNeeded();
            }
        }

        // =====================================================================
        // ML-BASED RISK ADJUSTMENT
        // =====================================================================

        public decimal GetAiRiskAdjustment(string symbol, MarketRegime regime)
        {
            lock (_lock)
            {
                if (!_mlStats.TryGetValue(symbol, out var ml) || ml.TotalTrades < 10)
                    return 1.0m;

                decimal adj = 1.0m;

                // ГЛОБАЛЬНОЕ качество символа
                if (ml.EmaWinRate > 0.60m && ml.EmaRr > 1.2m)
                    adj *= 1.25m;

                if (ml.EmaWinRate < 0.40m)
                    adj *= 0.80m;

                // Качество конкретного режима
                if (ml.Regimes.TryGetValue(regime, out var reg))
                {
                    if (reg.EmaWinRate > 0.65m && reg.EmaRr > 1.3m && reg.Trades > 5)
                        adj *= 1.30m;

                    if (reg.EmaWinRate < 0.35m && reg.Trades > 5)
                        adj *= 0.75m;
                }

                return Math.Clamp(adj, 0.5m, 1.7m);
            }
        }

        // =====================================================================
        // EXPORT / IMPORT (оставил твоё)
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
                    _logger.LogWarning("[AI-LEARN] ImportState: empty snapshot → SKIP");
                    return;
                }

                _stats.Clear();
                _mlStats.Clear(); // reset ML

                foreach (var sym in state.Symbols)
                {
                    if (string.IsNullOrWhiteSpace(sym.Symbol))
                        continue;

                    var regimes = new Dictionary<MarketRegime, RegimeStats>();
                    _stats[sym.Symbol] = regimes;

                    var ml = new AiSymbolMlStats(sym.Symbol);
                    _mlStats[sym.Symbol] = ml;

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

                        // ML warm-up
                        for (int i = 0; i < r.Trades; i++)
                        {
                            bool win = i < r.Wins;
                            decimal rr = r.SumRr / Math.Max(1, r.Trades);
                            ml.Update(rr, win, r.Regime);
                        }
                    }
                }

                _logger.LogInformation(
                    "[AI-LEARN] ImportState: loaded {Symbols} symbols, regimes={Regimes}",
                    _stats.Count,
                    _stats.Values.Sum(v => v.Count));
            }
        }

        // =====================================================================
        // EXTERNAL RECORD API (оставил твой)
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
            if (signal != null && signal.IsManual)
            {
                _logger.LogInformation("[AI-LEARN] Manual trade detected → SKIP");
                return;
            }

            if (entryPrice <= 0 || exitPrice <= 0)
                return;

            decimal rr = Math.Abs(exitPrice - entryPrice) /
                         Math.Max(1, Math.Abs(entryPrice * 0.001m));

            if (!isWin)
                rr = -Math.Abs(rr);

            RegisterTradeResult(symbol, regime, rr, isWin);
        }
    }
}
