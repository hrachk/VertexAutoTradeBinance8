using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Helpers;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.Formatting;
using VertexAutoTradeBinance8.Strategy;

namespace VertexAutoTradeBinance8
{
    /// <summary>
    /// TradingWorker v7 — главный цикл.
    ///
    /// Исправлено относительно v6:
    ///  • Любое исключение в цикле больше не убивает BackgroundService (а с ним и host).
    ///  • Fail-closed: если биржа не ответила по позициям — входы запрещены (раньше «fail-open»).
    ///  • SL-оптимизация выполняется ДО расчёта объёма (раньше qty считался от одного SL,
    ///    а ордер ставился с другим → фактический риск отличался от расчётного).
    ///  • Убран «ADD ×1.25/×1.40» после расчёта риска (раздувал риск сверх лимита).
    ///  • Один вход на символ: нет ни усреднения, ни встречной (hedge) позиции.
    ///  • Сигналы 1m по умолчанию не торгуются (только 5m+), решение — по закрытым свечам.
    ///  • Supervisor обходит ВСЕ managed-позиции, в т.ч. выпавшие из ротации символов.
    ///  • Дневной стоп по реализованному убытку.
    ///  • Проверка hedge-mode: если не удалось включить — входы блокируются (ордера с positionSide
    ///    в one-way режиме отклоняются биржей).
    /// </summary>
    public class TradingWorker : BackgroundService
    {
        private readonly ILogger<TradingWorker> _logger;
        private readonly IOptionsMonitor<TradingOptions> _optionsMon;
        private TradingOptions _options => _optionsMon.CurrentValue;

        private readonly MarketDataService _market;
        private readonly StrategyEngine _strategy;
        private readonly RiskManager _risk;
        private readonly OrderExecutor _executor;
        private readonly BinanceClientFactory _factory;
        private readonly LiquidityGuardService _liq;
        private readonly PositionGuardService _guard;
        private readonly PredictiveEngineV4ConfirmationService _predict;
        private readonly PositionSupervisorService _supervisor;
        private readonly AiStopLossOptimizer _slOpt;
        private readonly AiRiskScalerV2 _riskScaler;
        private readonly AiSelfLearningService _learn;
        private readonly AiModelSnapshotService _snapshot;
        private readonly SymbolRegistryService _symbols;
        private readonly AiTimeframeSelectorService _tfSelector;
        private readonly EngineStateBuilder _engineState;
        private readonly EngineStateSnapshotService _engineStateSnapshot;
        private readonly ExecutedSignalService _journal;
        private readonly TradingSessionGate _sessionGate;
        private readonly TradingControlService _tradeCtrl;
        private readonly AccountStateCache _account;
        private readonly ManagedPositionRegistry _managed;

        private readonly Dictionary<string, DateTime> _lastTrade = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastQuantTick = DateTime.UtcNow;
        private DateTime _lastEngineStateSave = DateTime.MinValue;
        private string? _entriesBlockedReason;

        public TradingWorker(
            ILogger<TradingWorker> logger,
            IOptionsMonitor<TradingOptions> options,
            MarketDataService market,
            StrategyEngine strategy,
            RiskManager risk,
            OrderExecutor executor,
            BinanceClientFactory factory,
            LiquidityGuardService liq,
            PositionGuardService guard,
            PredictiveEngineV4ConfirmationService predict,
            AiStopLossOptimizer slOpt,
            AiRiskScalerV2 riskScaler,
            PositionSupervisorService supervisor,
            AiSelfLearningService learn,
            AiModelSnapshotService snapshot,
            SymbolRegistryService symbols,
            AiTimeframeSelectorService tfSelector,
            EngineStateBuilder engineState,
            EngineStateSnapshotService engineStateSnapshot,
            ExecutedSignalService executedSignals,
            TradingSessionGate sessionGate,
            TradingControlService tradeCtrl,
            AccountStateCache account,
            ManagedPositionRegistry managed)
        {
            _logger = logger;
            _optionsMon = options;
            _market = market;
            _strategy = strategy;
            _risk = risk;
            _executor = executor;
            _factory = factory;
            _liq = liq;
            _guard = guard;
            _predict = predict;
            _slOpt = slOpt;
            _riskScaler = riskScaler;
            _supervisor = supervisor;
            _learn = learn;
            _snapshot = snapshot;
            _symbols = symbols;
            _tfSelector = tfSelector;
            _engineState = engineState;
            _engineStateSnapshot = engineStateSnapshot;
            _journal = executedSignals;
            _sessionGate = sessionGate;
            _tradeCtrl = tradeCtrl;
            _account = account;
            _managed = managed;
        }

        // ================================================================
        // MAIN LOOP
        // ================================================================
        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            _logger.LogWarning("TradingWorker v7 started — env={env}, data={dir}", _factory.EnvironmentName, VertexPaths.DataDir);

            if (!_factory.HasCredentials)
            {
                _entriesBlockedReason = "API credentials not configured";
                _logger.LogCritical("[WORKER] {r} — running in OBSERVE-ONLY mode", _entriesBlockedReason);
            }

            await SafeStartupAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunQuantRealtimeTick(ct);
                    _sessionGate.LogStatusThrottled();

                    // 1) Сопровождение всех позиций бота (независимо от ротации символов)
                    await _supervisor.SuperviseAllAsync(ct);

                    // 2) Поиск новых входов
                    foreach (var symbol in _symbols.ActiveSymbols)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var tf = await ProcessSymbol(symbol, ct);
                            SaveEngineStateThrottled(symbol, tf);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[WORKER][{symbol}] symbol cycle failed", symbol);
                        }

                        await Task.Delay(25, ct);
                    }

                    await Task.Delay(250, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[WORKER] main loop error — retry in 5s");
                    try { await Task.Delay(5000, ct); } catch { break; }
                }
            }

            try { await _snapshot.SaveSnapshotAsync(_learn.ExportState(), CancellationToken.None); } catch { }
            _logger.LogWarning("TradingWorker stopped");
        }

        private async Task SafeStartupAsync(CancellationToken ct)
        {
            try { await _symbols.LoadAsync(ct); }
            catch (Exception ex) { _logger.LogError(ex, "[WORKER] symbol registry load failed"); }

            try
            {
                var state = await _snapshot.LoadLatestAsync(ct);
                if (state != null) _learn.ImportState(state);
                _learn.ForceSnapshot();
            }
            catch (Exception ex) { _logger.LogError(ex, "AI snapshot load failed"); }

            if (_factory.HasCredentials)
            {
                try { await EnsureHedgeModeAsync(ct); }
                catch (Exception ex)
                {
                    _entriesBlockedReason = "hedge mode check failed";
                    _logger.LogError(ex, "[WORKER] hedge mode check failed — entries blocked");
                }
            }
        }

        private async Task RunQuantRealtimeTick(CancellationToken ct)
        {
            if ((DateTime.UtcNow - _lastQuantTick).TotalSeconds < 60) return;
            _lastQuantTick = DateTime.UtcNow;
            try
            {
                await _snapshot.SaveSnapshotAsync(_learn.ExportState(), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[QUANT] snapshot save failed");
            }
        }

        private void SaveEngineStateThrottled(string symbol, KlineInterval? tf)
        {
            if ((DateTime.UtcNow - _lastEngineStateSave).TotalSeconds < 2) return;
            _lastEngineStateSave = DateTime.UtcNow;
            try
            {
                var state = _engineState.Build(symbol, (tf ?? KlineInterval.FiveMinutes).ToString());
                _engineStateSnapshot.Save(state);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[WORKER] engine state save failed");
            }
        }

        // ================================================================
        // RE-ENTRY POLICY
        // ================================================================
        private bool InCooldown(string symbol, SignalSide side, out string why)
        {
            why = "";
            int anyMin = _options.PostCloseCooldownMinutes > 0 ? _options.PostCloseCooldownMinutes : 45;
            int sameMin = _options.SameSideCooldownMinutes > 0 ? _options.SameSideCooldownMinutes : 240;

            if (_journal.ShouldBlockReentry(symbol, side, anyMin, sameMin, out why))
                return true;

            int openCd = _options.CooldownMinutes > 0 ? _options.CooldownMinutes : Math.Max(1, _options.CooldownSeconds / 60);
            if (_lastTrade.TryGetValue(symbol, out var lastOpen) && DateTime.UtcNow - lastOpen < TimeSpan.FromMinutes(openCd))
            {
                why = "anti-duplicate after open";
                return true;
            }
            return false;
        }

        private KlineInterval ResolveTradingTf(AiTimeframeSelectorService.DominantTF decision)
        {
            var configured = (_options.TimeframeMinutes > 0 ? _options.TimeframeMinutes : 5).ToTimeframeString().ToKlineInterval();
            var selected = decision == AiTimeframeSelectorService.DominantTF.OneMinute
                ? KlineInterval.OneMinute
                : KlineInterval.FiveMinutes;

            if (selected == KlineInterval.OneMinute && !_options.AllowOneMinuteEntries)
                selected = KlineInterval.FiveMinutes;

            // торгуем не мельче настроенного TF
            return (int)selected >= (int)configured ? selected : configured;
        }

        // ================================================================
        // PER-SYMBOL PROCESSOR
        // ================================================================
        private async Task<KlineInterval?> ProcessSymbol(string symbol, CancellationToken ct)
        {
            // --- TF selection (кэшированные свечи) ---
            var m1 = await _market.GetMarketSnapshot(symbol, KlineInterval.OneMinute, ct);
            var m5 = await _market.GetMarketSnapshot(symbol, KlineInterval.FiveMinutes, ct);
            if (m1 == null || m5 == null) return null;

            var decision = _tfSelector.SelectTF(m1, m5);
            if (decision == AiTimeframeSelectorService.DominantTF.None)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, KlineInterval.FiveMinutes, "⏸ FLAT", "TF selector: none");
                return null;
            }
            var tf = ResolveTradingTf(decision);

            ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "⏳ RUN", "Анализ");

            var raw = await _market.GetKlines(symbol, tf, 201);
            IReadOnlyList<BinanceFuturesUsdtKline> klines = MarketDataService.DropFormingCandle(raw);
            if (klines.Count < 60)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "⏸ HOLD", "Мало свечей");
                return tf;
            }

            // --- 1. STRATEGY ---
            TradeSignal? signal;
            try
            {
                signal = _strategy.GenerateSignal(symbol, tf, klines);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[STRATEGY ERROR] {symbol} {tf}", symbol, tf);
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "❌ ERR", ex.GetType().Name);
                return tf;
            }

            if (signal == null)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🔴 NO", "Нет сигнала");
                return tf;
            }

            // --- 2. RE-ENTRY POLICY ---
            if (InCooldown(symbol, signal.Side, out var why))
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🕒 CD", why);
                _logger.LogDebug("[COOLDOWN][{symbol}] {why}", symbol, why);
                return tf;
            }

            // --- 3. ONE POSITION PER SYMBOL (fail-closed) ---
            bool hasPos = await _account.HasAnyPositionAsync(symbol, ct);
            if (!_account.IsFresh)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "⚠ API", "positions unknown");
                return tf;
            }
            if (hasPos || _managed.IsManagedAny(symbol))
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "⏸ POS", "Position open");
                return tf;
            }

            // --- 4. AI CONFIRM ---
            var ai = _predict.Decide(symbol, tf, klines, signal);
            if (!ai.Allow)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🟡 AI-BLK", ai.Reason);
                return tf;
            }

            decimal riskMult = _riskScaler.Scale(ai.Grade);
            if (riskMult <= 0)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🟡 RISK", "AI block");
                return tf;
            }

            // --- 5. LIQUIDITY GUARD ---
            var liq = _liq.Analyze(symbol, tf, klines, signal.Side, signal.IsSuperSignal);
            if (liq.Block)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🟡 LIQ", liq.Reason.ToString());
                return tf;
            }

            // --- 6. SL OPTIMIZATION (ДО расчёта объёма!) ---
            signal.StopLoss = _slOpt.OptimizeSlAndTp(symbol, klines, signal, ai);
            if (!StopDistanceOk(signal, out var slWhy))
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🟡 SL", slWhy);
                _logger.LogInformation("[TRADE][{symbol}] skip — {why}", symbol, slWhy);
                return tf;
            }

            // --- 7. GLOBAL GATES ---
            if (!EntryGatesOpen(out var gate))
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "👁 OBS", gate);
                _logger.LogInformation("[TRADE][{symbol}] signal {side} ready but entries closed — {gate}", symbol, signal.Side, gate);
                return tf;
            }

            int openCount = await _account.CountOpenPositionsAsync(ct);
            int maxOpen = _options.MaxOpenPositions > 0 ? _options.MaxOpenPositions : 4;
            if (openCount >= maxOpen)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "⛔ MAX", $"Open {openCount}/{maxOpen}");
                return tf;
            }

            if (await DailyLossLimitHitAsync(ct))
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "⛔ DAY", "Daily loss limit");
                return tf;
            }

            var entryDecision = await _guard.EvaluateAsync(symbol, signal.Side == SignalSide.Buy, signal.IsSuperSignal);
            if (entryDecision.Block)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🛡 GUARD", entryDecision.Reason);
                return tf;
            }

            // --- 8. POSITION SIZE ---
            decimal leverage = Math.Clamp(_options.Leverage, 1, 50);
            decimal qty = await _risk.CalculateSafeQty(signal, signal.Symbol, signal.EntryPrice, signal.StopLoss,
                riskMult, signal.SafetyRiskMultiplier, leverage, signal.Side, signal.TakeProfits, ct);

            if (qty <= 0)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "❌ QTY=0", "Size zero");
                return tf;
            }

            // --- 9. EXECUTE ---
            _logger.LogInformation("[TRADE][{symbol}] EXECUTE {side} qty={qty} entry={e} SL={sl} TP={tp}",
                symbol, signal.Side, qty, signal.EntryPrice, signal.StopLoss, string.Join("/", signal.TakeProfits.Select(x => x.ToString("0.#####"))));

            var result = await _executor.ExecuteAsync(signal, qty, ct);
            _lastTrade[symbol] = DateTime.UtcNow; // и при неудаче — пауза, чтобы не долбить биржу тем же сигналом
            if (!result.Success)
            {
                ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "❌ ORD", result.Error ?? "order error");
                return tf;
            }

            _guard.RegisterSuccessfulEntry(symbol);
            await _supervisor.SuperviseAsync(symbol, signal, ct);

            ConsoleSymbolTableFormatter.UpdateTf(symbol, tf, "🟩 OK", $"{signal.Side} qty={qty}");
            return tf;
        }

        private bool StopDistanceOk(TradeSignal s, out string why)
        {
            why = "";
            if (s.EntryPrice <= 0 || s.StopLoss <= 0) { why = "invalid SL"; return false; }
            bool sideOk = s.Side == SignalSide.Buy ? s.StopLoss < s.EntryPrice : s.StopLoss > s.EntryPrice;
            if (!sideOk) { why = "SL on wrong side of entry"; return false; }

            decimal pct = Math.Abs(s.EntryPrice - s.StopLoss) / s.EntryPrice * 100m;
            decimal min = _options.MinStopDistancePercent > 0 ? _options.MinStopDistancePercent : 0.30m;
            decimal max = _options.MaxStopDistancePercent > 0 ? _options.MaxStopDistancePercent : 6.0m;
            if (pct < min) { why = $"SL {pct:F2}% < min {min:F2}% (fees)"; return false; }
            if (pct > max) { why = $"SL {pct:F2}% > max {max:F2}%"; return false; }
            return true;
        }

        private bool EntryGatesOpen(out string reason)
        {
            if (_entriesBlockedReason != null) { reason = _entriesBlockedReason; return false; }

            if (!_tradeCtrl.IsTradingEnabled()) { reason = "trading OFF (UI)"; return false; }
            if (!_options.TradingEnabled) { reason = "trading OFF (config)"; return false; }

            if (!_sessionGate.IsTradingAllowed(out var sessionReason, out _))
            {
                reason = sessionReason;
                return false;
            }

            reason = "";
            return true;
        }

        private async Task<bool> DailyLossLimitHitAsync(CancellationToken ct)
        {
            if (_options.MaxDailyLossPercent <= 0) return false;
            var (wallet, _) = await _account.GetBalancesAsync(ct);
            if (wallet <= 0) return false;

            decimal pnlToday = _journal.GetRealizedPnlTodayUtc();
            decimal limit = wallet * _options.MaxDailyLossPercent / 100m;
            if (pnlToday <= -limit)
            {
                _logger.LogWarning("[RISK] DAILY STOP: realized today {pnl:F2} ≤ -{lim:F2} USDT — no new entries until 00:00 UTC",
                    pnlToday, limit);
                return true;
            }
            return false;
        }

        // ================================================================
        // HEDGE MODE
        // ================================================================
        private async Task EnsureHedgeModeAsync(CancellationToken ct)
        {
            var client = _factory.CreateRestClient();
            var current = await client.UsdFuturesApi.Account.GetPositionModeAsync(ct: ct);
            if (current.Success && current.Data?.IsHedgeMode == true)
            {
                _logger.LogInformation("Hedge Mode already active.");
                return;
            }

            var set = await client.UsdFuturesApi.Account.ModifyPositionModeAsync(true, ct: ct);
            if (set.Success)
            {
                _logger.LogWarning("Hedge Mode enabled.");
                return;
            }

            _entriesBlockedReason = "hedge mode is OFF and cannot be enabled (close open positions first)";
            _logger.LogCritical("[WORKER] {r}: {err}", _entriesBlockedReason, set.Error);
        }
    }
}
