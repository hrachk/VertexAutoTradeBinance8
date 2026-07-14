using VertexAutoTradeBinance8.Services.MarketData;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using Binance.Net.Objects.Models.Futures;
using Binance.Net.Objects.Models.Spot;
using CryptoExchange.Net.Objects;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Drawing;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services.Interface;
using VertexAutoTradeBinance8.Strategy;
using static VertexAutoTradeBinance8.Services.OrderExecutor;
public sealed class PositionLifecycleTracker
{
    // key = symbol_side_entryPrice
    private readonly ConcurrentDictionary<string, int> _barsInTrade = new();

    public int IncBars(string key)
        => _barsInTrade.AddOrUpdate(key, 1, (_, v) => v + 1);

    public void Clear(string key)
        => _barsInTrade.TryRemove(key, out _);
}
namespace VertexAutoTradeBinance8.Services
{
    public class PositionSupervisorService
    {
        private readonly ILogger<PositionSupervisorService> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly SymbolInfoService _symbolInfo;
        private readonly AiSelfLearningService _aiLearning;
        private readonly MarketDataService _marketData;
        private readonly AiMarketRegimeService _regime;
        private readonly ManualPositionHandler _manualHandler;
        private readonly BinanceAlgoOrderRaw _algoRaw;
        private readonly LiquidityGuardService _liquidityGuard;
        private readonly IAccountStateService _accountState;
        private readonly IOrderDispatcher _dispatcher;
        private readonly EntryTracker _entryTracker;   // ← сброс активных входов при закрытии позиции
        private readonly FundingRateService _fundingRate;
        private MarketRegime _regimeNow;

        // === Anti-spam guards for EarlyTP / BE-move ===
        private readonly ConcurrentDictionary<string, long> _earlyTpDone = new();   // key -> unixMs
        private readonly ConcurrentDictionary<string, long> _beMoved = new();      // key -> unixMs
        private readonly ConcurrentDictionary<string, decimal> _restoredEntries = new();
        private readonly PositionLifecycleTracker _lifecycle;

        // === Harvest block after partial close ===
        private readonly ConcurrentDictionary<string, long> _recentPartialClose = new();

        private readonly EngineStateSnapshotService _stateSvc;

        private readonly SmartRegimeService _smartRegime;
        private readonly ReverseProbeEngine _reverseProbe;

        // === Attach idempotency (existing position attach) ===
        private readonly ConcurrentDictionary<string, bool> _attached = new();

        // ===============================
        // NEW STATE DICTIONARIES
        // ===============================
        private readonly ConcurrentDictionary<string, BeStage> _beStage = new();

        private EngineState _engineState => _stateSvc.State;
        // private readonly AtrAdaptiveProfitLockManager _atrLock;
        // tracks staircase level after each partial / BE move
        private readonly ConcurrentDictionary<string, int> _beLevel = new();
        private readonly ConcurrentDictionary<string, DateTime> _pendingReset = new();
        private readonly ConcurrentDictionary<string, bool> _finalCleanupDone = new();
        private const decimal POSITION_EPS = 0.000001m;

        private readonly IOptionsMonitor<TradingSettings> _tradingSettings;
        private readonly ConcurrentDictionary<string, decimal> _lastEntryPrice = new();
        private const string BE_PREFIX = "BE_";
        private const string SL_PREFIX = "SL_";
        private const string TR_PREFIX = "TR_";
        private const string TP_PREFIX = "TP_";
        // ConcurrentDictionary used as a thread-safe set — value is always
        // true, key presence is the signal. HashSet<string> is NOT safe for
        // concurrent read+write from the parallel Task.WhenAll(ProbeSide Long,
        // ProbeSide Short) calls in SuperviseAsync, which could corrupt the
        // collection or throw InvalidOperationException.
        private readonly ConcurrentDictionary<string, bool> _beOverrideForStrongTrend = new();
        private readonly ConcurrentDictionary<string, decimal> _lastSl = new();
        private readonly IOptionsMonitor<TradingOptions> _tradingOptions;
        private readonly IOptionsMonitor<DcaOptions> _dcaOptions;

        /// <summary>
        /// Returns true if the symbol is in the DCA accumulation list
        /// (Dca:Symbols in appsettings). DCA positions are long-term
        /// and need wider TP/SL distances than short-term signal trades.
        /// </summary>
        private bool IsDcaSymbol(string symbol)
            => _dcaOptions.CurrentValue.Enabled &&
               _dcaOptions.CurrentValue.Symbols.Any(s =>
                   s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

        public PositionSupervisorService(
            ILogger<PositionSupervisorService> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            AiSelfLearningService aiLearning,
            MarketDataService marketData,
            AiMarketRegimeService regime,
            ManualPositionHandler manualHandler,
            IConfiguration cfg,
            IHttpClientFactory httpFactory,
            LiquidityGuardService liquidityGuard,
            IOrderDispatcher dispatcher, EngineStateSnapshotService stateSvc,
            SmartRegimeService smartRegime,
            IAccountStateService accountState,
            ReverseProbeEngine reverseProbe, PositionLifecycleTracker lifecycle,
            EntryTracker entryTracker,
            FundingRateService fundingRate,
            IOptionsMonitor<TradingSettings> tradingSettings,
            IOptionsMonitor<TradingOptions> tradingOptions,
            IOptionsMonitor<DcaOptions> dcaOptions)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
            _aiLearning = aiLearning;
            _marketData = marketData;
            _regime = regime;
            _manualHandler = manualHandler;

            _regimeNow = MarketRegime.Range;

            _algoRaw = new BinanceAlgoOrderRaw(cfg, httpFactory, _logger);
            _liquidityGuard = liquidityGuard;
            _dispatcher = dispatcher;
            _stateSvc = stateSvc;
            _smartRegime = smartRegime;
            _reverseProbe = reverseProbe;
            _lifecycle = lifecycle;
            _entryTracker = entryTracker;
            _fundingRate = fundingRate;
            _accountState = accountState;
            _tradingSettings = tradingSettings;
            _tradingOptions = tradingOptions;
            _dcaOptions = dcaOptions;
        }


        private async Task CleanupProtectiveOrdersAsync(
    IBinanceRestClient client,
    string symbol,
    PositionSide side,
    CancellationToken ct)
        {
            // Regular orders (still checked in case anything was ever
            // placed through the old non-conditional path).
            var open = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct);

            if (open.Success)
            {
                foreach (var order in open.Data)
                {
                    if (order.PositionSide != side)
                        continue;

                    if (order.Type != FuturesOrderType.StopMarket)
                        continue;

                    if (order.ClientOrderId == null)
                        continue;

                    if (order.ClientOrderId.StartsWith(BE_PREFIX) ||
                        order.ClientOrderId.StartsWith(SL_PREFIX) ||
                        order.ClientOrderId.StartsWith(TR_PREFIX))
                    {
                        await client.UsdFuturesApi.Trading.CancelOrderAsync(
                            symbol,
                            order.Id,
                            ct: ct);
                    }
                }
            }

            // CRITICAL FIX: also check algo (conditional) orders — the
            // EXACT order type PlaceConditionalAsync places, which the
            // regular endpoint above genuinely cannot see since
            // Binance's Dec 2025 migration. Every BE-move/SL-update
            // path in this file places protective orders via the algo
            // API, so cleanup MUST also look there, or every single
            // one of these orders survives forever (the original bug
            // behind both the rapid-SL-duplication and leftover-orders
            // symptoms).
            var algoOrders = await _algoRaw.GetOpenAlgoOrdersAsync(symbol, ct);
            foreach (var algo in algoOrders)
            {
                if (algo.PositionSide != side) continue;
                if (!algo.IsStop) continue;

                if (algo.ClientAlgoId == null) continue;
                if (algo.ClientAlgoId.StartsWith(BE_PREFIX) ||
                    algo.ClientAlgoId.StartsWith(SL_PREFIX) ||
                    algo.ClientAlgoId.StartsWith(TR_PREFIX))
                {
                    await _algoRaw.CancelAlgoOrderAsync(algo.AlgoId, ct);
                }
            }
        }

        private async Task HandleFinalCloseAsync(
    BinanceRestClient client,
    string symbol,
    PositionSide side,
    CancellationToken ct)
        {
            var key = $"{symbol}_{side}";

            if (!_finalCleanupDone.TryAdd(key, true))
                return;

            try
            {
                _logger.LogWarning(
                    "[FINAL CLEANUP][{symbol}][{side}] start",
                    symbol, side);

                // 1️⃣ Отменяем все ордера по символу
                var cancel = await client
                    .UsdFuturesApi
                    .Trading
                    .CancelAllOrdersAsync(symbol, ct: ct);

                if (!cancel.Success)
                {
                    _logger.LogWarning(
                        "[FINAL CLEANUP][{symbol}] CancelAllOrders failed: {err}",
                        symbol, cancel.Error?.Message);
                }

                // 2️⃣ Сбрасываем BE состояние
                _beStage.TryRemove(key, out _);
                _beLevel.TryRemove(key, out _);
                _pendingReset.TryRemove(key, out _);

                _logger.LogWarning(
                    "[FINAL CLEANUP][{symbol}][{side}] done",
                    symbol, side);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[FINAL CLEANUP FAILED][{symbol}][{side}]",
                    symbol, side);

                _finalCleanupDone.TryRemove(key, out _);
            }
            finally
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(15));
                    _finalCleanupDone.TryRemove(key, out _);
                });
            }
        }

        private static string BuildExitKey(string symbol, PositionSide side, decimal entryPrice)
        {
            return $"{symbol}|{side}|{entryPrice:F8}";
        }

        enum ProfitDecision
        {
            HoldTrend,
            TakeProfit,
            ReverseSignal
        }
        public async Task SuperviseAsync(string symbol, TradeSignal? lastSignal, CancellationToken ct)
        {
            // NOTE: EnableExecution=false only blocks NEW entries (OrderExecutor).
            // Supervisor always runs — it manages TP/SL/BE of ALREADY OPEN positions.
            // A position opened before the user toggled AutoTrade OFF must still be
            // protected: trailing SL, break-even moves, partial closes all continue.
            // Do NOT add an early return here based on EnableExecution.

            // DCA detection: symbols in Dca:Symbols list get wider
            // TP/SL/BE thresholds (long-term accumulation, not short-term signal)
            bool _isDca = IsDcaSymbol(symbol);
            if (_isDca)
                _logger.LogDebug("[SUPERVISOR][{symbol}] DCA position detected — using wide TP/SL/BE multipliers", symbol);

            using var client = _factory.CreateRestClient();

            // 0) MANUAL SIGNAL INJECTION
            if (lastSignal == null)
            {
                var manualSignal = await _manualHandler.DetectManualAsync(client, symbol, ct);
                if (manualSignal != null)
                {
                    lastSignal = manualSignal;
                    _logger.LogWarning("[MANUAL][{symbol}] Virtual signal injected", symbol);
                }
            }

            // 1) LOAD POSITIONS
            var posInfo = await GetPositionsWithRetryAsync(client, symbol, ct);
            if (!posInfo.Success || posInfo.Data == null) return;

            var positions = posInfo.Data.Where(p => p.Symbol == symbol).ToList();
            var longPos = positions.FirstOrDefault(p => p.PositionSide == PositionSide.Long);
            var shortPos = positions.FirstOrDefault(p => p.PositionSide == PositionSide.Short);

            DetectClose(symbol, longPos, PositionSide.Long);
            DetectClose(symbol, shortPos, PositionSide.Short);

            bool hasLong = Math.Abs(longPos?.Quantity ?? 0m) > POSITION_EPS;
            bool hasShort = Math.Abs(shortPos?.Quantity ?? 0m) > POSITION_EPS;

            // 2) LIFECYCLE CLEANUP IF FLAT
            if (!hasLong && !hasShort)
            {
                await CleanupProtectiveOrdersAsync(client, symbol, PositionSide.Long, ct);
                await CleanupProtectiveOrdersAsync(client, symbol, PositionSide.Short, ct);

                _beLevel.Clear();
                _beStage.Clear();
                _beMoved.Clear();
                _pendingReset.Clear();
                _beOverrideForStrongTrend.Clear();
                return;
            }

            // 3) LOAD MARKET DATA
            IReadOnlyList<BinanceFuturesUsdtKline>? klines1m = null;
            SmartRegimeInfo? smart1m = null;
            decimal atr14_1m = 0m;

            try
            {
                klines1m = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 200);
                var rr = _regime.DetectRegime(symbol, KlineInterval.OneMinute, klines1m);
                if (rr != null) _regimeNow = rr.Regime;

                if (klines1m.Count >= 50)
                {
                    smart1m = _smartRegime.Evaluate(symbol, KlineInterval.OneMinute, klines1m);
                    atr14_1m = _marketData.CalculateAtr(klines1m, 14);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SUPERVISOR] Klines/SmartRegime load error {symbol}", symbol);
            }

            // 3.5) SMART TREND OVERRIDE
            if (smart1m != null && lastSignal != null)
            {
                decimal dynamicThreshold = 0.4m + smart1m.VolatilityPercent / 2;
                bool strongTrend = smart1m.SmartType == SmartRegimeType.SmartStrongTrend &&
                                   smart1m.Confidence >= dynamicThreshold &&
                                   smart1m.AllowAggressiveTrendEntries;
                if (strongTrend)
                {
                    if (longPos != null && Math.Abs(longPos.Quantity) > POSITION_EPS)
                        _beOverrideForStrongTrend.TryAdd(BuildExitKey(symbol, PositionSide.Long, longPos.EntryPrice), true);

                    if (shortPos != null && Math.Abs(shortPos.Quantity) > POSITION_EPS)
                        _beOverrideForStrongTrend.TryAdd(BuildExitKey(symbol, PositionSide.Short, shortPos.EntryPrice), true);
                }
            }


            // 4) SMART TRAILING / BE / PARTIAL CLOSE
            async Task ProbeSide(BinancePositionDetailsUsdt? pos, PositionSide side)
            {
                // ── MANUAL POSITION LOGIC ────────────────────────────────────────
                // User opened this position manually and placed their own SL/TP.
                // Policy:
                //   Phase 0 — before TP1 fires: do NOTHING. Respect the user's plan.
                //   Phase 1 — after TP1 fires (qty reduced from initial):
                //             move SL to break-even (entry) ONCE for protection.
                //             Never trail SL further — user manages from here.
                // ─────────────────────────────────────────────────────────────────
                if (lastSignal?.IsManual == true)
                {
                    if (pos == null || Math.Abs(pos.Quantity) < POSITION_EPS) return;

                    decimal currentQty  = Math.Abs(pos.Quantity);
                    decimal manualEntry = pos.EntryPrice; // renamed from 'entry' — avoids conflict with ProbeSide's own 'entry' below
                    var manualKey       = BuildExitKey(symbol, side, manualEntry);

                    // Track initial qty the first time we see this manual position.
                    // _lastSl doubles as a convenient ConcurrentDictionary available here.
                    // We use a separate key suffix _mqty to avoid colliding with SL tracking.
                    var mqtyKey = manualKey + "_mqty";
                    if (!_lastSl.ContainsKey(mqtyKey))
                    {
                        // First time seeing this position — record initial qty, do nothing yet.
                        _lastSl[mqtyKey] = currentQty;
                        _logger.LogDebug(
                            "[SUPERVISOR][{sym}][{side}] Manual pos recorded: initQty={qty:F4} entry={e:F4}",
                            symbol, side, currentQty, manualEntry);
                        return;
                    }

                    decimal initialQty = _lastSl[mqtyKey];

                    // Check if TP1 has fired: qty decreased meaningfully (>5% reduction)
                    bool tp1Hit = initialQty > 0 && currentQty < initialQty * 0.95m;

                    if (!tp1Hit)
                    {
                        _logger.LogDebug(
                            "[SUPERVISOR][{sym}][{side}] Manual pos waiting for TP1 " +
                            "(qty {cur:F4}/{init:F4})",
                            symbol, side, currentQty, initialQty);
                        return; // Phase 0: freeze, do nothing
                    }

                    // Phase 1: TP1 fired — move SL to break-even ONCE
                    var beKey = manualKey + "_be_moved";
                    if (_lastSl.ContainsKey(beKey))
                    {
                        // Already moved BE for this position — nothing more to do
                        _logger.LogDebug(
                            "[SUPERVISOR][{sym}][{side}] Manual pos: BE already moved",
                            symbol, side);
                        return;
                    }

                    // Mark BE as moved BEFORE placing order (idempotent)
                    _lastSl[beKey] = 1m;

                    // Professional approach: SL at slightly POSITIVE territory,
                    // not at exact entry (entry = breakeven = 0 profit after fees).
                    // Add 0.08% buffer above entry for LONG (below for SHORT).
                    // This covers typical Binance taker fee (0.04%) × 2 sides
                    // so the position closes in actual profit, not at zero.
                    const decimal BE_BUFFER_PCT = 0.0008m; // 0.08%
                    decimal bePrice = side == PositionSide.Long
                        ? manualEntry * (1m + BE_BUFFER_PCT)  // LONG: SL above entry
                        : manualEntry * (1m - BE_BUFFER_PCT); // SHORT: SL below entry

                    _logger.LogInformation(
                        "[SUPERVISOR][{sym}][{side}] Manual pos TP1 fired " +
                        "(qty {cur:F4} < {init:F4}) → moving SL to BE+buf={e:F4} (entry={entry:F4})",
                        symbol, side, currentQty, initialQty, bePrice, manualEntry);

                    try
                    {
                        await PlaceStopLossAtBeAsync(client, symbol, side,
                            currentQty, bePrice, pos, ct);
                    }
                    catch (Exception ex)
                    {
                        // Non-critical — log and continue; user's original SL still protects
                        _logger.LogWarning(
                            "[SUPERVISOR][{sym}][{side}] Manual BE move failed: {err}",
                            symbol, side, ex.Message);
                        _lastSl.TryRemove(beKey, out _); // allow retry next cycle
                    }
                    return; // Never fall into the normal SL-trailing logic for manual positions
                }

                if (pos == null || Math.Abs(pos.Quantity) < POSITION_EPS)
                {
                    var key = BuildExitKey(symbol, side, pos?.EntryPrice ?? 0m);
                    _pendingReset.TryRemove(key, out _);
                    _beStage.TryRemove(key, out _);
                    _beLevel.TryRemove(key, out _);
                    _beMoved.TryRemove(key, out _);
                    _lastSl.TryRemove(key, out _);
                    _lastSl.TryRemove(key + "_mqty", out _); // manual qty tracking
                    _lastSl.TryRemove(key + "_be_moved", out _); // manual BE flag
                    _beOverrideForStrongTrend.TryRemove(key, out _);
                    return;
                }

                decimal qty = Math.Abs(pos.Quantity);
                decimal entry = pos.EntryPrice;
                decimal mark = pos.MarkPrice;

                if (qty <= 0 || entry <= 0 || mark <= 0) return;

                var keyProbe = BuildExitKey(symbol, side, entry);

                // =========================
                // ROI (текущая прибыль)
                // =========================
                decimal roi = side == PositionSide.Long
                    ? (mark - entry) / entry
                    : (entry - mark) / entry;

                // =========================
                // CONFIG
                // =========================
                decimal ATR = atr14_1m;
                if (ATR <= 0) return;

                bool isToxic = _tradingSettings.CurrentValue.ToxicSymbols.Contains(symbol);
                bool isDcaPos = IsDcaSymbol(symbol);

                // ── DCA vs Signal-trade multipliers ─────────────────────────────
                // DCA positions are long-term accumulation (weekly/monthly buys).
                // They need WIDER distances so Supervisor doesn't fire partial
                // closes or move BE on normal intraday noise:
                //   BE trigger: 3.0×ATR (vs 1.3× for short-term signals)
                //   Partial step: 5.0×ATR (vs 2.0×)
                //   SL buffer: 1.2×ATR (vs 0.3-0.5×) — wide buffer, long-term hold
                //   Partial size: 20% (vs 34-42%) — smaller partial, preserve position
                decimal STEP         = ATR * (isDcaPos ? 1.0m  : 0.5m);
                decimal PARTIAL_STEP = ATR * (isDcaPos ? 5.0m  : 2.0m);
                decimal PARTIAL_SIZE = isDcaPos ? 0.20m : (isToxic ? 0.42m : 0.34m);
                decimal MIN_BUFFER   = ATR * (isDcaPos ? 1.2m  : (isToxic ? 0.5m : 0.3m));

                bool skipSoftFilters = _beOverrideForStrongTrend.ContainsKey(keyProbe);

                // =========================
                // SAFE ZONE → не дергать SL
                // DCA: 3.0×ATR — only move BE after significant move
                // Signal: 1.3×ATR — standard short-term threshold
                // =========================
                decimal BE_TRIGGER = ATR * (isDcaPos ? 3.0m : 1.3m);
                if (roi < BE_TRIGGER && !skipSoftFilters) return;

                // =========================
                // LEVEL CONTROL (анти-спам)
                // =========================
                int level = (int)((roi - BE_TRIGGER) / STEP) + 1;
                int prevLevel = _beLevel.GetOrAdd(keyProbe, 0);
                if (level <= prevLevel) return;  // не дергаем повторно
                _beLevel[keyProbe] = level;

                // =========================
                // PARTIAL CLOSE (раз в уровень)
                // Gated on SupervisorManageTP — when false, the user
                // manages their own TP/close orders and we must not
                // interfere by triggering partial closes from here.
                // =========================
                if (!skipSoftFilters && _tradingOptions.CurrentValue.SupervisorManageTP)
                {
                    int partialLevel = (int)(roi / PARTIAL_STEP);
                    int prevPartial = (int)_beStage.GetOrAdd(keyProbe, BeStage.None);

                    if (partialLevel > prevPartial && partialLevel >= 1)
                    {
                        _beStage[keyProbe] = (BeStage)partialLevel;
                        decimal closeQty = Math.Round(qty * PARTIAL_SIZE, 8);
                        closeQty = Math.Min(closeQty, Math.Round(qty * 0.9m, 8));

                        if (closeQty > 0)
                        {
                            await ClosePartialAsync(client, symbol, side, closeQty, pos, ct);
                            qty -= closeQty;
                        }
                    }
                }

                // =========================
                // CALC NEW SL
                // =========================
                decimal baseDistance = ATR * 1.2m;
                decimal profitDistance = side == PositionSide.Long
                    ? mark - entry
                    : entry - mark;
                decimal dynamicBuffer = Math.Max(ATR * 0.8m, MIN_BUFFER);

                decimal newSl =
                    profitDistance <= 0
                        ? entry
                        : profitDistance < baseDistance
                            ? (side == PositionSide.Long ? mark - dynamicBuffer : mark + dynamicBuffer)
                            : (side == PositionSide.Long ? mark - baseDistance : mark + baseDistance);

                if (skipSoftFilters)
                    newSl = entry;

                // =========================
                // DIFF CHECK → не дергать SL на шум
                // =========================
                decimal lastPlacedSl = _lastSl.GetOrAdd(keyProbe, 0m);
                if (Math.Abs(lastPlacedSl - newSl) < ATR * 0.2m) return;

                // =========================
                // COOLDOWN
                // =========================
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                // Cooldown scaled to market volatility:
                // VolatileChop/StrongTrend: longer wait to avoid rapid SL flapping
                // during high-volatility moves (was causing rapid SL duplication).
                var cooldown = _regimeNow switch
                {
                    MarketRegime.VolatileChop => 8000,
                    MarketRegime.StrongUpTrend or MarketRegime.StrongDownTrend => 5000,
                    _ => isToxic ? 4000 : 2500
                };
                if (_beMoved.TryGetValue(keyProbe, out var lastMove) && now - lastMove < cooldown) return;

                // =========================
                // CANCEL CURRENT SL
                // =========================
                // Matches the existing SL for this position regardless of
                // which prefix (if any) it carries - Supervisor's own
                // previously-placed SL (BE_/SL_/TR_), OR the plain
                // entry-time SL OrderExecutor places with no special
                // clientAlgoId at all. Without this, the first
                // break-even move would place a NEW BE_-prefixed SL on
                // top of the original entry SL without ever cancelling
                // it, leaving two simultaneous Stop Loss orders on the
                // same position instead of one replacing the other.
                var exitOrders = (await LoadOrdersAsync(client, symbol))
                    .Where(o => o.PositionSide == side && o.Type == FuturesOrderType.StopMarket)
                    .OrderByDescending(o => o.UpdateTime)
                    .ToList();

                var currentSl = exitOrders.FirstOrDefault();
                if (currentSl != null)
                {
                    decimal existing = currentSl.StopPrice ?? 0m;
                    if (Math.Abs(existing - newSl) >= ATR * 0.2m)
                        await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, currentSl.Id, ct: ct);
                }

                // =========================
                // PLACE NEW SL
                // =========================
                await Task.Delay(60, ct);
                await PlaceStopLossAtBeAsync(client, symbol, side, qty, newSl, pos, ct);

                // =========================
                // SAVE STATE
                // =========================
                _lastSl[keyProbe] = newSl;
                _beMoved[keyProbe] = now;

                _logger.LogInformation(
                    "[SL MOVE][{symbol}][{side}] → {sl} lvl={lvl} roi={roi:P2}",
                    symbol, side, newSl, level, roi);
            }

            // запуск для обеих сторон
            if (smart1m != null && atr14_1m > 0)
                await Task.WhenAll(
                    ProbeSide(longPos, PositionSide.Long),
                    ProbeSide(shortPos, PositionSide.Short)
                );

            // 5) HANDLE OPEN POSITIONS
            // Load orders once and share between both sides — previously
            // LoadOrdersAsync was called separately for Long and Short,
            // meaning the two calls could see different exchange state
            // (if an order filled between the two calls) and wasted 2x
            // the API weight unnecessarily.
            var sharedOrders = await LoadOrdersAsync(client, symbol);

            // ── MANUAL POSITION POLICY ──────────────────────────────────────
            // If lastSignal.IsManual == true: user opened this position
            // manually via Binance UI or our chart. They placed their own
            // TP/SL orders. We MUST NOT touch them.
            //
            // HandleSideAsync is completely skipped for manual positions.
            // ProbeSide handles the only allowed intervention:
            //   Phase 0 (before TP1): do nothing at all
            //   Phase 1 (after TP1 fires, qty drops >5%): move SL to BE once
            // ─────────────────────────────────────────────────────────────────
            bool isManualPosition = lastSignal?.IsManual == true;

            if (hasLong)
            {
                if (!isManualPosition)
                    await HandleSideAsync(client, symbol, PositionSide.Long, longPos!, sharedOrders, lastSignal, klines1m, ct);
                else
                    _logger.LogDebug(
                        "[SUPERVISOR][{sym}][LONG] Manual position — HandleSideAsync skipped (user manages own TP/SL)",
                        symbol);
            }
            if (hasShort)
            {
                if (!isManualPosition)
                    await HandleSideAsync(client, symbol, PositionSide.Short, shortPos!, sharedOrders, lastSignal, klines1m, ct);
                else
                    _logger.LogDebug(
                        "[SUPERVISOR][{sym}][SHORT] Manual position — HandleSideAsync skipped (user manages own TP/SL)",
                        symbol);
            }
        }

        // ===== PLACE BE SL =====
        private async Task PlaceStopLossAtBeAsync(
      IBinanceRestClient client,
      string symbol,
      PositionSide side,
      decimal qty,
      decimal entryPrice,
      BinancePositionDetailsUsdt pos,
      CancellationToken ct)
        {
            if (qty <= 0 || pos == null) return;

            var orderSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);

            // --- нормализация ---
            var (qtyPrecision, pricePrecision) = await GetSymbolPrecisionsAsync(client, symbol);
            qty = await NormalizeQuantityAsync(symbol, side, qty, client, ct);
            entryPrice = await NormalizeTriggerPriceAsync(client, symbol, side, entryPrice, filters.tickSize, true, ct);
            entryPrice = RoundPrice(entryPrice, pricePrecision);

            decimal currentStop = 0m;

            // CRITICAL FIX: BE-move protective SL orders are placed via
            // PlaceConditionalAsync (the algo API, see below) — the
            // regular GetOpenOrdersAsync check that used to live here
            // never found these (confirmed: algo orders are invisible
            // to that endpoint since Binance's Dec 2025 migration), so
            // this "cancel the old one first" step was silently doing
            // nothing every single cycle, and a new SL got placed on
            // top of every previous one. This is THE root cause of SL
            // orders multiplying rapidly that was reported.
            var algoOrders = await _algoRaw.GetOpenAlgoOrdersAsync(symbol, ct);
            // Cancel ALL existing algo SL orders for this side —
            // not just BE_-prefixed ones. When user places SL via
            // Binance UI, it lands as an algo order with a random
            // clientAlgoId (no prefix). Cancelling only BE_-prefixed
            // orders was leaving the user's original SL alive,
            // causing duplicate SL orders during BE-moves.
            // GUARD: only do this for non-manual positions — for
            // manual positions, ProbeSide already returns early.
            var currentSlList = algoOrders
                .Where(o => o.IsStop &&
                            o.PositionSide == side)
                .ToList();

            foreach (var currentSl in currentSlList)
            {
                currentStop = currentSl.TriggerPrice;

                var cancelled = await _algoRaw.CancelAlgoOrderAsync(currentSl.AlgoId, ct);
                if (!cancelled)
                {
                    _logger.LogWarning("[BE MOVE][{symbol}][{side}] Failed to cancel old SL {stop}", symbol, side, currentStop);
                }
                else
                {
                    _logger.LogInformation("[BE MOVE][{symbol}][{side}] canceled old SL {stop}", symbol, side, currentStop);
                }
            }

            await Task.Delay(130, ct); // небольшая задержка для безопасности

            var clientOrderId = $"{BE_PREFIX}{symbol}_{side}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

            var result = await client.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
                symbol: symbol,
                side: orderSide,
                type: ConditionalOrderType.StopMarket,
                quantity: qty,
                positionSide: side,
                triggerPrice: entryPrice,
                workingType: WorkingType.Mark,
                clientOrderId: clientOrderId,
                priceProtect: true,
                ct: ct
            );

            if (result.Success)
                _logger.LogInformation("[BE MOVE][{symbol}][{side}] BE SL placed at {price} qty={qty}", symbol, side, entryPrice, qty);
            else
            {
                _logger.LogError("[BE MOVE][{symbol}][{side}] Failed: {msg}", symbol, side, result.Error?.Message);

                // --- rollback старого SL, если новый не стал ---
                if (currentStop > 0m)
                {
                    await client.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
                        symbol: symbol,
                        side: orderSide,
                        type: ConditionalOrderType.StopMarket,
                        quantity: qty,
                        positionSide: side,
                        triggerPrice: currentStop,
                        workingType: WorkingType.Mark,
                        clientOrderId: $"{BE_PREFIX}{symbol}_{side}_rb_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                        priceProtect: true,
                        ct: ct
                    );
                    _logger.LogInformation("[BE MOVE][{symbol}][{side}] rollback old SL placed at {price}", symbol, side, currentStop);
                }
            }
        }

        private async Task HandleSideAsync(
       BinanceRestClient client,
       string symbol,
       PositionSide side,
       BinancePositionDetailsUsdt pos,
       List<BinanceUsdFuturesOrder> allOrders,
       TradeSignal? signal,
       IReadOnlyList<BinanceFuturesUsdtKline>? klines,
       CancellationToken ct)
            {
                decimal qtyAbs = Math.Abs(pos.Quantity);

                var key = $"{symbol}_{side}";
                var prevQty = _manualHandler.GetPrevQty(key);
                var prevEntry = _manualHandler.GetPrevEntry(key);

                // =====================================================
                // 📌 POSITION CLOSE DETECTOR (AI + STOP TRACK)
                // =====================================================
                if (prevQty != 0 && pos.Quantity == 0)
                {
                    decimal exitPrice = pos.MarkPrice > 0
                        ? pos.MarkPrice
                        : pos.EntryPrice;

                    var sigSide = side == PositionSide.Long ? SignalSide.Buy : SignalSide.Sell;

                    _aiLearning.RecordTrade(symbol, sigSide, prevEntry, exitPrice, _regimeNow);

                    _logger.LogWarning(
                        "[AI][{symbol}] POSITION CLOSED entry={entry} exit={exit}",
                        symbol, prevEntry, exitPrice);

                    bool isStopLoss =
                        side == PositionSide.Long
                            ? exitPrice < prevEntry
                            : exitPrice > prevEntry;

                    if (isStopLoss)
                    {
                        _manualHandler.RegisterStop(symbol);

                        _logger.LogWarning(
                            "[STOP][{symbol}] StopLoss detected → cooldown",
                            symbol);
                    }

                    _earlyTpDone.TryRemove(BuildPosGuardKey(symbol, side, prevEntry, prevQty), out _);
                    _beMoved.TryRemove(BuildPosGuardKey(symbol, side, prevEntry, prevQty), out _);

                    return;
                }

                _manualHandler.SetPrevState(key, pos.Quantity, pos.EntryPrice);

            // =====================================================
            // ❌ НЕТ ПОЗИЦИИ → ЧИСТИМ LIFECYCLE
            // =====================================================
            // =====================================================
            // ❌ FULL POSITION CLOSED → CLEANUP ORDERS & LIFECYCLE
            // =====================================================
            if (qtyAbs <= 0)
            {
                _logger.LogInformation("[SUPERVISOR] {symbol} {side}: fully flat → cleanup all SL/TP orders", symbol, side);

                // =====================================================
                // Сбрасываем активный счётчик входов для этой стороны
                // Сессионный счётчик остаётся — повторный вход разрешён
                // только если session < MAX_SESSION_ENTRIES (4)
                // =====================================================
                _entryTracker.OnPositionClosed(symbol, side);

                // Belt-and-suspenders: reconcile ALL entry counters against
                // current real positions so any counter that got stuck
                // (e.g. liquidation bypassing our OnPositionClosed path)
                // is cleaned up here automatically.
                try
                {
                    var allPos = await client.UsdFuturesApi.Account
                        .GetPositionInformationAsync(ct: ct);
                    if (allPos.Success && allPos.Data != null)
                    {
                        var openKeys = allPos.Data
                            .Where(p => Math.Abs(p.Quantity) > 0)
                            .Select(p => $"{p.Symbol}_{p.PositionSide}")
                            .ToList();
                        _entryTracker.ReconcileWithRealPositions(openKeys);
                    }
                }
                catch { /* reconcile is non-critical, never block supervisor */ }

                var openOrders = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct);
                if (openOrders.Success)
                {
                    var ordersToCancel = openOrders.Data
                        .Where(o => o.PositionSide == side &&
                                    (o.Type == FuturesOrderType.StopMarket || o.Type == FuturesOrderType.TakeProfitMarket))
                        .ToList();

                    foreach (var o in ordersToCancel)
                    {
                        await client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, o.Id, ct: ct);
                        _logger.LogInformation("[SUPERVISOR] {symbol} {side}: canceled leftover order {id}", symbol, side, o.Id);
                    }
                }

                // CRITICAL FIX: this is exactly the leftover-orders bug
                // reported via screenshot — algo (conditional) orders
                // are completely invisible to the regular endpoint
                // above since Binance's Dec 2025 migration, so any
                // protective order placed via PlaceConditionalAsync
                // (which is how every SL/TP in this file gets placed)
                // was never found here and survived position close
                // forever, sitting on the exchange as a dangling
                // STOP/TAKE_PROFIT order with no position behind it.
                var algoOrdersToClean = await _algoRaw.GetOpenAlgoOrdersAsync(symbol, ct);
                // When SupervisorManageTP=false, only clean up SL/BE orders
                // (Supervisor did not place any TP orders, so do not cancel user ones)
                var algoCleanPredicate = _tradingOptions.CurrentValue.SupervisorManageTP
                    ? (Func<BinanceAlgoOrderInfo, bool>)(o => o.PositionSide == side && (o.IsStop || o.IsTakeProfit))
                    : (Func<BinanceAlgoOrderInfo, bool>)(o => o.PositionSide == side && o.IsStop);
                foreach (var algo in algoOrdersToClean.Where(algoCleanPredicate))
                {
                    var cancelled = await _algoRaw.CancelAlgoOrderAsync(algo.AlgoId, ct);
                    if (cancelled)
                        _logger.LogInformation("[SUPERVISOR] {symbol} {side}: canceled leftover algo order {id}", symbol, side, algo.AlgoId);
                    else
                        _logger.LogWarning("[SUPERVISOR] {symbol} {side}: failed to cancel leftover algo order {id}", symbol, side, algo.AlgoId);
                }

                // =====================================================
                // Очистка lifecycle только для ЭТОЙ позиции
                // БЫЛО: .Clear() — сбрасывало ВСЕ позиции!
                // ТЕПЕРЬ: точечное удаление по ключу символ+сторона
                // =====================================================
                var exitKey = BuildExitKey(symbol, side, prevEntry);
                var posGuardKey = BuildPosGuardKey(symbol, side, prevEntry, prevQty);

                _beLevel.TryRemove(exitKey, out _);
                _beStage.TryRemove(exitKey, out _);
                _beMoved.TryRemove(exitKey, out _);
                _lastSl.TryRemove(exitKey, out _);
                _pendingReset.TryRemove(exitKey, out _);
                _earlyTpDone.TryRemove(posGuardKey, out _);
                _beOverrideForStrongTrend.TryRemove(exitKey, out _);
                _lifecycle.Clear(exitKey);

                return;
            }

            // =====================================================
            // 📌 ENTRY RESOLVE (если Binance не дал entry)
            // =====================================================
            decimal entry = pos.EntryPrice;

                if (entry <= 0)
                {
                    if (!_restoredEntries.TryGetValue(key, out entry))
                    {
                        var restored = await ResolveEntryFromExchangeAsync(client, symbol, side, ct);

                        if (!restored.HasValue)
                        {
                            _logger.LogError(
                                "[SUPERVISOR][{symbol}] Entry unresolved → skip",
                                symbol);

                            return;
                        }

                        entry = restored.Value;
                        _restoredEntries[key] = entry;

                        _logger.LogWarning(
                            "[SUPERVISOR][{symbol}] Entry restored = {entry}",
                            symbol, entry);
                    }
                }

                // =====================================================
                // 📌 MEAN-REVERSION TIME-STOP (v9, fully isolated)
                // =====================================================
                // Mean-reversion's thesis is "price reverts to the mean
                // within a short, bounded window". If it hasn't happened
                // after TimeStopBars closed candles, the thesis didn't
                // play out — close now regardless of current PnL rather
                // than let it sit indefinitely waiting on the original
                // SL/TP (which trend-following positions are fine doing,
                // but is wrong for this strategy's risk profile).
                //
                // Gated strictly on signal.Reason starting with "MEANREV_"
                // so trend-following positions (signal == null or any
                // other Reason) are completely unaffected — this branch
                // is a pure addition, it cannot change existing behavior.
                bool isMeanReversionPos = signal?.Reason?.StartsWith("MEANREV_", StringComparison.OrdinalIgnoreCase) == true;

                if (isMeanReversionPos && signal!.TimeStopBars.HasValue && signal.TimeStopBars.Value > 0)
                {
                    var mrKey = BuildPosGuardKey(symbol, side, entry, qtyAbs);
                    int bars = _lifecycle.IncBars(mrKey);

                    if (bars >= (int)signal.TimeStopBars.Value)
                    {
                        _logger.LogWarning(
                            "[MEANREV][TIME-STOP][{symbol}][{side}] {bars} bars elapsed without reverting to mean — closing now",
                            symbol, side, bars);

                        try
                        {
                            await ClosePartialAsync(client, symbol, side, qtyAbs, pos, ct);
                        }
                        finally
                        {
                            _lifecycle.Clear(mrKey);
                        }

                        return;
                    }
                }

                // =====================================================
                // 📌 ПРОСТО ЧИТАЕМ ОРДЕРА (БЕЗ ВМЕШАТЕЛЬСТВА)
                // =====================================================
                var orders = allOrders.Where(o => o.PositionSide == side).ToList();

                var sl = orders.FirstOrDefault(o => o.Type == FuturesOrderType.StopMarket);
                var tp = orders.FirstOrDefault(o => o.Type == FuturesOrderType.TakeProfitMarket);

                // CRITICAL FIX: allOrders only contains regular orders
                // (see LoadOrdersAsync) — algo (conditional) orders are
                // invisible to that endpoint since Binance's Dec 2025
                // migration. Without this check, an SL/TP placed via
                // the algo API (which is how every protective order in
                // this file actually gets placed) would never be found
                // here, and this code would place ANOTHER emergency
                // one on top of it every supervisor cycle.
                var algoOrdersForSide = await _algoRaw.GetOpenAlgoOrdersAsync(symbol, ct);
                var algoSlExists = algoOrdersForSide.Any(o => o.IsStop && o.PositionSide == side);
                var algoTpCount = algoOrdersForSide.Count(o => o.IsTakeProfit && o.PositionSide == side);

                // =====================================================
                // ⚠️ ТОЛЬКО FALLBACK (если вообще нет защиты)
                // =====================================================

                // ❗ SL отсутствует → ставим аварийный (один раз)
                // NOTE: HandleSideAsync is now only called for non-manual positions
                // (manual positions are guarded at the call site in SuperviseAsync).
                // This emergency SL logic is therefore always for bot-opened positions.
                bool noSlAnywhere = sl == null && !algoSlExists;
                if (noSlAnywhere)
                {
                    await CreateEmergencySLAsync(client, symbol, side, qtyAbs, entry, signal, ct);

                    _logger.LogWarning(
                        "[SUPERVISOR][{symbol}][{side}] Emergency SL created (no SL found anywhere)",
                        symbol, side);
                }

                // ❗ TP отсутствует → ставим аварийный
                // НО: OrderExecutor мог уже поставить TP1 + TP2
                // Проверяем количество TP ордеров — если уже есть 2+, не трогаем
                var existingTps = orders.Count(o =>
                    o.Type == FuturesOrderType.TakeProfitMarket ||
                    o.Type == FuturesOrderType.TakeProfit) + algoTpCount;

                if (existingTps == 0)
                {
                    // Only place emergency TP if SupervisorManageTP=true AND
                    // this is NOT a manual position (user placed their own TP).
                    // GetOpenAlgoOrdersAsync has 20s cache — it may falsely return
                    // 0 TPs for a few seconds after user places one. For manual
                    // positions skip emergency TP entirely to avoid conflicts.
                    bool isManualPos = signal?.IsManual == true;
                    if (_tradingOptions.CurrentValue.SupervisorManageTP && !isManualPos)
                    {
                        await CreateEmergencyTPAsync(client, symbol, side, qtyAbs, entry, signal, ct);
                        _logger.LogWarning(
                            "[SUPERVISOR][{symbol}][{side}] Emergency TP created (no TP orders found)",
                            symbol, side);
                    }
                    else if (isManualPos)
                    {
                        _logger.LogDebug(
                            "[SUPERVISOR][{symbol}][{side}] Manual position — skip Emergency TP (user manages their own)",
                            symbol, side);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "[SUPERVISOR][{symbol}][{side}] SupervisorManageTP=false — no TP orders but NOT creating emergency (user manages manually)",
                            symbol, side);
                    }
                }
                else
                {
                    _logger.LogDebug(
                        "[SUPERVISOR][{symbol}][{side}] {count} TP order(s) already exist — skipping Emergency TP",
                        symbol, side, existingTps);
                }

           
            }

       private async Task ClosePartialChunkedAsync(
       IBinanceRestClient client,
       string symbol,
       PositionSide side,
       decimal totalQty,
       CancellationToken ct)
            {
                decimal CHUNK_USDT =
         symbol == "BTCUSDT" ? 15000m :
         symbol == "ETHUSDT" ? 8000m :
         1500m;

                var mark = await client.UsdFuturesApi.ExchangeData.GetMarkPriceAsync(symbol, ct);

                if (!mark.Success)
                    return;

                var markPrice = mark.Data.MarkPrice;

                var chunkQty = CHUNK_USDT / markPrice;

                var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol, ct: ct);

                chunkQty = Math.Floor(chunkQty / filters.step) * filters.step;

                if (chunkQty <= 0)
                    return;

                var remaining = totalQty;

                while (remaining > 0)
                {
                    var qty = Math.Min(chunkQty, remaining);

                    var orderSide = side == PositionSide.Long
                        ? OrderSide.Sell
                        : OrderSide.Buy;


                    var book = await client
        .UsdFuturesApi
        .ExchangeData
        .GetOrderBookAsync(symbol, limit: 5, ct);

                    if (!book.Success)
                        return;
                    decimal price;
                    if (side == PositionSide.Long)
                        price = book.Data.Bids.First().Price;   // закрываем LONG
                    else
                        price = book.Data.Asks.First().Price;   // закрываем SHORT

                    var result = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
         symbol: symbol,
         side: orderSide,
         type: FuturesOrderType.Limit,
         quantity: qty,
         price: price,
         timeInForce: TimeInForce.ImmediateOrCancel,
         positionSide: side,
         selfTradePreventionMode: SelfTradePreventionMode.ExpireMaker,
         ct: ct);

                    if (!result.Success)
                    {
                        _logger.LogError(
                            "[PARTIAL CLOSE CHUNK FAILED][{symbol}] {err}",
                            symbol,
                            result.Error?.Message);

                        await Task.Delay(120, ct);
                        continue;
                    }
                    if (result.Success)
                    {
                        // ===== REALIZED PNL CALC =====

                        var entry = await GetEntryPriceSafeAsync(client, symbol, side, ct);

                        if (entry > 0)
                        {
                            decimal realizedPnl =
                                side == PositionSide.Long
                                ? (markPrice - entry) * qty
                                : (entry - markPrice) * qty;

                            _accountState.AddRealizedPnl(realizedPnl);

                            _logger.LogInformation(
                                "[REALIZED PNL][PARTIAL][{symbol}][{side}] pnl={pnl}",
                                symbol, side, realizedPnl);
                        }
                    }

                    remaining -= qty;

                    await Task.Delay(50, ct);
                }
                await CleanupProtectiveOrdersAsync(client, symbol, side, ct);
                _logger.LogInformation(
                    "[PARTIAL CLOSE OK][{symbol}] totalQty={qty}",
                    symbol,
                    totalQty);
            }

       private async Task<decimal> GetEntryPriceSafeAsync(
       IBinanceRestClient client,
       string symbol,
       PositionSide side,
       CancellationToken ct)
            {
                var posInfo = await client
                    .UsdFuturesApi
                    .Account
                    .GetPositionInformationAsync(symbol, ct: ct);

                if (!posInfo.Success)
                    return 0;

                var pos = posInfo.Data.FirstOrDefault(p =>
                    p.PositionSide == side &&
                    Math.Abs(p.Quantity) > 0);

                return pos?.EntryPrice ?? 0;
            }

        // ===== CLOSE PARTIAL =====
        private async Task ClosePartialAsync(
         IBinanceRestClient client,
         string symbol,
         PositionSide side,
         decimal qty,
         BinancePositionDetailsUsdt pos,
         CancellationToken ct)
        {
            if (qty <= 0 || pos == null)
                return;

            var orderSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy; 
            qty = await NormalizeQuantityAsync(symbol, side, qty, client, ct);
          
            if (qty <= 0)
            {
                _logger.LogWarning("[PARTIAL CLOSE SKIPPED][{symbol}][{side}] qty rounded to 0", symbol, side);
                return;
            }

            await ClosePartialChunkedAsync(
            client,
            symbol,
            side,
            qty,
            ct);

        }

        private async Task<decimal> NormalizeQuantityAsync(
      string symbol,
      PositionSide side,
      decimal requestedQty,
      IBinanceRestClient client,
      CancellationToken ct)
        {
            requestedQty = Math.Abs(requestedQty);
            // 1) получить реальную позицию
            var posInfo = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, ct: ct);
            await Task.Delay(90, ct);
            if (!posInfo.Success)
                return 0;

            var pos = posInfo.Data.FirstOrDefault(p =>
                p.PositionSide == side &&
                Math.Abs(p.Quantity) > 0);

            if (pos == null)
                return 0;

            var positionQty = Math.Abs(pos.Quantity);

            // 2) symbol filters
            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);

            var maxQty = filters.maxQty;
            var step = filters.step;
            var minQty = filters.minQty;

            // 3) clamp
            var qty = Math.Min(requestedQty, positionQty);
            if (maxQty > 0)
                qty = Math.Min(qty, maxQty);

            // 4) round to stepSize
            qty = Math.Floor(qty / step) * step;

            // 5) защита от dust (когда после floor qty становится 0)
            if (qty == 0 && positionQty >= minQty)
                qty = minQty;

            // 6) validate
            if (qty < minQty)
                return 0;

            return qty;
        }

        private async Task<decimal> NormalizeTriggerPriceAsync(
     IBinanceRestClient client,
     string symbol,
     PositionSide side,
     decimal trigger,
     decimal tick,
     bool isStopLoss,
     CancellationToken ct)
        {

            var mark = await client
                .UsdFuturesApi
                .ExchangeData
                .GetMarkPriceAsync(symbol, ct);

            if (!mark.Success)
                return trigger;

            var price = mark.Data.MarkPrice;

            if (side == PositionSide.Long)
            {
                if (isStopLoss)
                {
                    if (trigger >= price)
                        trigger = price - tick * 3;
                }
                else
                {
                    if (trigger <= price)
                        trigger = price + tick * 3;
                }
            }
            else
            {
                if (isStopLoss)
                {
                    if (trigger <= price)
                        trigger = price + tick * 3;
                }
                else
                {
                    if (trigger >= price)
                        trigger = price - tick * 3;
                }
            }

            return Math.Round(trigger / tick) * tick;
        } 

        // ===== UTILITY: Получение точностей символа =====
        private async Task<(int qtyPrecision, int pricePrecision)> GetSymbolPrecisionsAsync(
            IBinanceRestClient client, string symbol)
        {
            var infoResult = await client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();
            var sym = infoResult.Data.Symbols.FirstOrDefault(s => s.Name == symbol);
            if (sym == null)
                return (0, 0);

            int qtyPrecision = 0;
            int pricePrecision = 0;

            // ===== Количество =====
            var lot = sym.Filters.FirstOrDefault(f => f.FilterType == SymbolFilterType.LotSize);
            if (lot != null)
            {
                // безопасно приводим к BinanceSymbolLotSizeFilter
                if (lot is BinanceSymbolLotSizeFilter lotFilter)
                {
                    qtyPrecision = (int)Math.Round(Math.Log10((double)(1 / lotFilter.StepSize)));
                }
            }

            // ===== Цена =====
            var priceF = sym.Filters.FirstOrDefault(f => f.FilterType == SymbolFilterType.Price);
            if (priceF != null)
            {
                // безопасно приводим к BinanceSymbolPriceFilter
                if (priceF is BinanceSymbolPriceFilter priceFilter)
                {
                    pricePrecision = (int)Math.Round(Math.Log10((double)(1 / priceFilter.TickSize)));
                }
            }

            return (qtyPrecision, pricePrecision);
        }

        private decimal RoundPrice(decimal price, int precision)
        {
            return Math.Floor(price * (decimal)Math.Pow(10, precision)) / (decimal)Math.Pow(10, precision);
        }

        private void SafeFireAndForget(Task task)
        {
            _ = task.ContinueWith(t =>
            {
                if (t.Exception != null)
                {
                    _logger.LogError(
                        t.Exception,
                        "[FIRE&FORGET ERROR]");
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private decimal ResolveExitPrice(string symbol)
        {
            // 1) Пытаемся взять свежий стакан
            var depth = _marketData.GetCachedDepth(symbol);

            if (depth != null && depth.Bids.Count > 0 && depth.Asks.Count > 0)
            {
                var bestBid = depth.Bids[0].price;
                var bestAsk = depth.Asks[0].price;

                if (bestBid > 0 && bestAsk > 0)
                    return (bestBid + bestAsk) / 2m;
            }

            return 0m;
        }
        private void DetectClose(
     string symbol,
     BinancePositionDetailsUsdt? pos,
     PositionSide side)
        {
            var key = $"{symbol}_{side}";

            var prevQty = _manualHandler.GetPrevQty(key);
            var prevEntry = _manualHandler.GetPrevEntry(key);

            var currQty = pos?.Quantity ?? 0m;
            var currEntry = pos?.EntryPrice ?? 0m;

            // 🔥 CLOSE DETECTED
            if (prevQty != 0m && currQty == 0m)
            {
                var exitPrice = ResolveExitPrice(symbol);

                if (exitPrice <= 0m)
                {
                    _logger.LogWarning(
                        "[CLOSE][{symbol}][{side}] Exit price unresolved, skip record",
                        symbol, side);
                }
                else
                {
                    // ✅ REALIZED PNL (добавлено)
                    var qty = Math.Abs(prevQty);

                    decimal realizedPnl =
                        side == PositionSide.Long
                            ? (exitPrice - prevEntry) * qty
                            : (prevEntry - exitPrice) * qty;

                    _accountState.AddRealizedPnl(realizedPnl);

                    // AI learning остаётся как есть
                    _aiLearning.RecordTrade(
                        symbol,
                        side == PositionSide.Long ? SignalSide.Buy : SignalSide.Sell,
                        entry: prevEntry,
                        exit: exitPrice,
                        regime: _regimeNow);

                    _logger.LogWarning(
                        "[CLOSE][{symbol}][{side}] qty={qty} entry={entry} exit={exit} pnl={pnl}",
                        symbol, side, prevQty, prevEntry, exitPrice, realizedPnl);
                }

                var client = _factory.CreateRestClient();

                SafeFireAndForget(
                    HandleFinalCloseAsync(
                        client,
                        symbol,
                        side,
                        CancellationToken.None));

                // ⚠️ ОБНОВЛЯЕМ СОСТОЯНИЕ ТОЛЬКО ПОСЛЕ ПРОВЕРКИ
                _manualHandler.SetPrevState(key, currQty, currEntry);
            }
        }

        // =====================================================================
        // RETRY POSITIONS
        // =====================================================================
        private async Task<WebCallResult<BinancePositionDetailsUsdt[]>> GetPositionsWithRetryAsync(
        BinanceRestClient client,
        string symbol,
        CancellationToken ct)
        {
            const int maxAttempts = 5;
            WebCallResult<BinancePositionDetailsUsdt[]> last = null!;

            for (int i = 0; i < maxAttempts; i++)
            {
                ct.ThrowIfCancellationRequested();

                // 🔥 БЕЗ symbol-фильтра — Binance bug-safe
                var res = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                last = res;

                if (res.Success && res.Data != null)
                {
                    // ⏳ ждём, пока позиция реально появится
                    if (res.Data.Any(p => p.Symbol == symbol && p.Quantity != 0m))
                        return res;
                }

                await Task.Delay(300, ct);
            }

            return last;
        }
        
        public async Task AttachExistingPositionAsync(
         string symbol,
         PositionSide side,
         decimal qty,
         decimal entryPrice,
         CancellationToken ct)
        {
            qty = Math.Abs(qty);

            if (qty <= 0 || entryPrice <= 0) return;

            var key = $"{symbol}:{side}:{entryPrice:F8}:{qty:F8}";

            if (_attached.TryAdd(key, true) == false)
            {
                _logger.LogInformation("[SUPERVISOR][ATTACH] already attached {key}", key);
                return;
            }

            _logger.LogWarning(
                "[SUPERVISOR][ATTACH] attaching existing position {symbol} {side} qty={qty} entry={entry}",
                symbol, side, qty, entryPrice);

            await EnsureEmergencyProtectionAsync(symbol, side, qty, entryPrice, ct);
        }

        private async Task EnsureEmergencyProtectionAsync(
        string symbol,
        PositionSide side,
        decimal qty,
        decimal entryPrice,
        CancellationToken ct)
        {
            if (qty <= 0 || entryPrice <= 0)
                return;

            // Важно: qty у позиции в Binance может быть со знаком
            qty = Math.Abs(qty);

            using var client = _factory.CreateRestClient();

            // 1) Проверяем существующие ордера
            var openOrders = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(
                symbol: symbol,
                ct: ct);

            if (!openOrders.Success || openOrders.Data == null)
            {
                _logger.LogWarning(
                    "[SUPERVISOR][EMERGENCY] cannot fetch open orders {symbol}: {err}",
                    symbol, openOrders.Error?.Message);
                return;
            }

            bool hasSL = openOrders.Data.Any(o =>
                o.Type == FuturesOrderType.StopMarket &&
                o.PositionSide == side);

            // Also check algo orders — an existing SL placed via the
            // conditional/algo API (which is how this same function's
            // own fallback below places one, and how every other SL in
            // this file gets placed) is invisible to the regular check
            // above, so without this an emergency SL could be placed
            // on top of an already-existing one.
            if (!hasSL)
            {
                var existingAlgoOrders = await _algoRaw.GetOpenAlgoOrdersAsync(symbol, ct);
                hasSL = existingAlgoOrders.Any(o => o.IsStop && o.PositionSide == side);
            }

            if (hasSL)
            {
                _logger.LogInformation(
                    "[SUPERVISOR][EMERGENCY] SL already exists {symbol} {side}",
                    symbol, side);
                return;
            }

            // 2) Emergency SL без klines (bootstrap-safe)
            // Консервативно: 1.2% от entry (под твой v8.2 смысл "быстро защитить")
            const decimal emergencyPct = 0.012m;

            decimal slPrice = side == PositionSide.Long
                ? entryPrice * (1m - emergencyPct)
                : entryPrice * (1m + emergencyPct);

            _logger.LogWarning(
                "[SUPERVISOR][EMERGENCY] placing SL {symbol} {side} qty={qty} sl={sl}",
                symbol, side, qty, slPrice);

            // 3) Пробуем обычный endpoint (без reduceOnly параметра -> меньше риска -1106)
            var orderSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var sl = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: orderSide,
                type: FuturesOrderType.StopMarket,
                quantity: qty,
                stopPrice: slPrice,
                positionSide: side,
                workingType: WorkingType.Mark,
                reduceOnly: null,
                selfTradePreventionMode: SelfTradePreventionMode.ExpireMaker,
                ct: ct);

            if (sl.Success)
            {
                _logger.LogInformation(
                    "[SUPERVISOR][EMERGENCY] SL placed OK (NORMAL) {symbol} {side} sl={sl}",
                    symbol, side, slPrice);
                return;
            }

            // 4) Fallback: ALGO RAW CONDITIONAL (если -4120)
            if (IsAlgoRequired(sl.Error))
            {
                _logger.LogWarning(
                    "[SUPERVISOR][EMERGENCY] requires ALGO (-4120) -> RAW /fapi/v1/algoOrder {symbol} {side}",
                    symbol, side);

                // workingType: "MARK_PRICE" чтобы совпасть с WorkingType.Mark
                var ok = await _algoRaw.PlaceConditionalAsync(
                    symbol: symbol,
                    side: orderSide,
                    positionSide: side,
                    type: "STOP_MARKET",
                    quantity: qty,
                    triggerPrice: slPrice,
                    workingType: "MARK_PRICE",
                    reduceOnly: null,
                    ct: ct,
                    clientAlgoId: $"{SL_PREFIX}{symbol}_{side}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");

                if (ok)
                {
                    _logger.LogWarning(
                        "[SUPERVISOR][EMERGENCY] SL placed OK (ALGO-RAW) {symbol} {side} sl={sl}",
                        symbol, side, slPrice);
                    return;
                }

                _logger.LogCritical(
                    "[SUPERVISOR][EMERGENCY] ALGO-RAW FAILED {symbol} {side}",
                    symbol, side);
                return;
            }

            _logger.LogError(
                "[SUPERVISOR][EMERGENCY] SL FAILED (NORMAL) {symbol}: {err}",
                symbol, sl.Error?.Message);
        }

        private async Task TryReverseProbeAsync(
    BinanceRestClient client,
    string symbol,
    BinancePositionDetailsUsdt? longPos,
    BinancePositionDetailsUsdt? shortPos,
    SmartRegimeInfo smart,
    decimal atr,
    CancellationToken ct)
        {


            // 0) protection must exist (PROTECT stage already done by EarlyTP/BE)
            var sKey = EngineState.Key(symbol);
            if (!_engineState.Symbols.TryGetValue(sKey, out var st))
                return;

            bool protectedRecently = st.LastProtectionUtc > DateTime.UtcNow.AddMinutes(-15);
            if (!protectedRecently)
                return;

            // 1) do not probe if both sides already exist (already hedged)
            bool hasLong = longPos != null && longPos.Quantity != 0m;
            bool hasShort = shortPos != null && shortPos.Quantity != 0m;
            if (hasLong && hasShort)
                return;

            // 2) base side = existing position side
            PositionSide baseSide =
                hasLong ? PositionSide.Long :
                hasShort ? PositionSide.Short :
                PositionSide.Both;

            if (baseSide == PositionSide.Both)
                return;

            // if liquidity recent → wait a bit, but allow early TP later           
            // 🚫 no probe right after liquidity event
            if (_liquidityGuard.IsDangerRecent(TimeSpan.FromMinutes(5)))
            {
                if (DateTime.UtcNow - _liquidityGuard.LastDanger!.UtcTime < TimeSpan.FromMinutes(2))
                {
                    _logger.LogInformation(
                        "[PROBE][{symbol}] SKIP → recent liquidity danger {reason}",
                        symbol, _liquidityGuard.LastDanger?.Reason);
                    return;
                }
            }

            if (_liquidityGuard.LastDanger?.Reason == LiquidityGuardReason.LowVolume)
                return;


            // 3) flip condition (strict)
            bool flipToShort =
                baseSide == PositionSide.Long &&
                smart.BaseRegime == MarketRegime.StrongDownTrend &&
                smart.TrendSlopePercent < -0.01m;

            bool flipToLong =
                baseSide == PositionSide.Short &&
                smart.BaseRegime == MarketRegime.StrongUpTrend &&
                smart.TrendSlopePercent > 0.01m;

            if (!flipToShort && !flipToLong)
                return;

            var probeSide = flipToShort ? PositionSide.Short : PositionSide.Long;

            // 4) anti-spam (single probe per 5 minutes per symbol)
            if (!_reverseProbe.CanProbeNow(symbol))
                return;

            // 5) size = 7% of existing position qty (micro-hedge, no RiskManager needed)
            decimal baseQtyAbs = Math.Abs((baseSide == PositionSide.Long ? longPos!.Quantity : shortPos!.Quantity));
            if (baseQtyAbs <= 0m)
                return;

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            var step = filters.step > 0 ? filters.step : 1m;

            decimal probeQty = baseQtyAbs * 0.07m;
            probeQty = Math.Floor(probeQty / step) * step;

            if (probeQty < filters.minQty)
                return;

            // 6) place MARKET entry (Hedge side-aware)
            var orderSide = probeSide == PositionSide.Long ? OrderSide.Buy : OrderSide.Sell;

            _logger.LogWarning(
                "[PROBE][{symbol}] START micro-hedge: base={baseSide} -> probe={probeSide} qty={qty} smart={reg} slope={slope:P2}",
                symbol, baseSide, probeSide, probeQty, smart.BaseRegime, smart.TrendSlopePercent);

            _dispatcher.Enqueue(async token =>
            {
                using var c = _factory.CreateRestClient();

                var entryRes = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: orderSide,
                    type: FuturesOrderType.Market,
                    quantity: probeQty,
                    positionSide: probeSide,
                    selfTradePreventionMode: SelfTradePreventionMode.ExpireMaker,
                    ct: token);

                if (!entryRes.Success)
                {
                    _logger.LogWarning("[PROBE][{symbol}] Entry failed: {err}", symbol, entryRes.Error);
                    return;
                }

                // small sync lag
                await Task.Delay(350, token);

                // resolve entry from actual position
                var posInfo = await c.UsdFuturesApi.Account.GetPositionInformationAsync(ct: token);
                if (!posInfo.Success || posInfo.Data == null)
                    return;

                var p = posInfo.Data.FirstOrDefault(x =>
                    x.Symbol == symbol &&
                    x.PositionSide == probeSide &&
                    Math.Abs(x.Quantity) > 0);

                if (p == null || p.EntryPrice <= 0)
                {
                    _logger.LogWarning("[PROBE][{symbol}] Entry resolve failed after open", symbol);
                    return;
                }

                var entry = p.EntryPrice;

                // set emergency SL/TP for probe immediately
                await CreateEmergencySLAsync(c, symbol, probeSide, probeQty, entry, signal: null, token);
                await CreateEmergencyTPAsync(c, symbol, probeSide, probeQty, entry, signal: null, token);

                _logger.LogWarning(
                    "[PROBE][{symbol}] OPENED {side} qty={qty} entry={entry} -> SL/TP restored",
                    symbol, probeSide, probeQty, entry);
            });

            _reverseProbe.MarkProbe(symbol);
        }

        private async Task<List<BinanceUsdFuturesOrder>> LoadOrdersAsync(BinanceRestClient client, string symbol, CancellationToken ct = default)
        {
            var res = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: ct);
            return res.Success && res.Data != null ? res.Data.ToList() : new List<BinanceUsdFuturesOrder>();
        }

        // =====================================================================
        // HANDLE SIDE  (v8.2 PRO)
        // =====================================================================

        public async Task ClosePositionMarketAsync(
            string symbol,
            BinanceRestClient client,
            BinancePositionDetailsUsdt pos,
            CancellationToken ct)
        {
            if (pos == null || pos.Quantity == 0) return;

            var side = pos.Quantity > 0 ? OrderSide.Sell : OrderSide.Buy;
            var absQty = Math.Abs(pos.Quantity);

            absQty = await NormalizeQuantityAsync(symbol, pos.PositionSide, absQty, client, ct);   // ✅ здесь нормализуем по MaxNotional

            var result = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: side,
                type: FuturesOrderType.Market,
                quantity: absQty,
                positionSide: pos.PositionSide,
                reduceOnly: null,
                selfTradePreventionMode: SelfTradePreventionMode.ExpireMaker,
                ct: ct
            );

            if (result.Success)
                _logger.LogInformation("[FULL CLOSE OK][{symbol}][{side}] qty={qty}", symbol, pos.PositionSide, absQty);
            else
                _logger.LogError("[FULL CLOSE FAILED][{symbol}][{side}] {err}", symbol, pos.PositionSide, result.Error?.Message);
        }


        public async Task<int> GetActivePositionsCountAsync(CancellationToken ct = default)
        {
            using var client = _factory.CreateRestClient();

            try
            {
                var result = await client
                    .UsdFuturesApi
                    .Trading
                    .GetPositionsAsync(ct: ct)
                    .ConfigureAwait(false);

                if (!result.Success || result.Data == null)
                {
                    _logger.LogWarning(
                        "API вернул ошибку или null при получении позиций: {code} - {msg}",
                        result.Error?.Code,
                        result.Error?.Message);

                    return 0; // <- безопасно, бот продолжает работать
                }

                int activeCount = result.Data.Count(p => p.PositionAmt != 0);

                _logger.LogInformation(
                    "Проверка позиций завершена. Активных: {count}",
                    activeCount);

                return activeCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Критическая ошибка при получении позиций");
                return 0; // <- безопасно, BackgroundService не падает
            }
        }


        //    private async Task HandleSideAsync(
        //        BinanceRestClient client,
        //        string symbol,
        //        PositionSide side,
        //        BinancePositionDetailsUsdt pos,
        //        List<BinanceUsdFuturesOrder> allOrders,
        //        TradeSignal? signal,
        //        IReadOnlyList<BinanceFuturesUsdtKline>? klines,
        //        CancellationToken ct)
        //    {
        //        decimal qtyAbs = Math.Abs(pos.Quantity);

        //        // ---------- CLOSE DETECTOR ----------
        //        var key = $"{symbol}_{side}";
        //        var prevQty = _manualHandler.GetPrevQty(key);
        //        var prevEntry = _manualHandler.GetPrevEntry(key);



        //        if (prevQty != 0 && pos.Quantity == 0)
        //        {
        //            decimal exitPrice = pos.MarkPrice > 0
        //         ? pos.MarkPrice
        //         : pos.EntryPrice; // fallback safety
        //            var sigSide = side == PositionSide.Long ? SignalSide.Buy : SignalSide.Sell;

        //            _aiLearning.RecordTrade(symbol, sigSide, entry: prevEntry, exit: exitPrice, regime: _regimeNow);

        //            _logger.LogWarning(
        //                "[AI][{symbol}] POSITION CLOSED → saved to ai_learning.json | entry={entry} exit={exit}",
        //                symbol, prevEntry, exitPrice);


        //            // =======================================
        //            // STOP LOSS DETECT → STRATEGY COOLDOWN
        //            // =======================================
        //            bool isStopLoss =
        // side == PositionSide.Long
        //     ? exitPrice < prevEntry
        //     : exitPrice > prevEntry;

        //            if (isStopLoss)
        //            {
        //                _manualHandler.RegisterStop(symbol);

        //                _logger.LogWarning(
        //                    "[STOP][{symbol}] StopLoss detected → cooldown registered",
        //                    symbol);
        //            }
        //            // cleanup guards
        //            _earlyTpDone.TryRemove(BuildPosGuardKey(symbol, side, prevEntry, prevQty), out _);
        //            _beMoved.TryRemove(BuildPosGuardKey(symbol, side, prevEntry, prevQty), out _);

        //            return;
        //        }
        //        _manualHandler.SetPrevState(key, pos.Quantity, pos.EntryPrice);

        //        // === no position ===
        //        if (qtyAbs <= 0)
        //        {
        //            // обязательно чистим lifecycle
        //            _lifecycle.Clear(BuildExitKey(symbol, side, prevEntry));
        //            _logger.LogInformation("[SUPERVISOR] {symbol} {side} {prevEntry}: no qty", symbol, side, prevEntry);
        //            return;
        //        }

        //        // =====================================================
        //        // IMPULSE CONTINUATION — FORCE EXIT CONTRACT
        //        // =====================================================
        //        if (signal != null && signal.ForceFullExit)
        //        {
        //            // key per position lifecycle
        //            var posKey = BuildPosGuardKey(symbol, side, pos.EntryPrice, qtyAbs);

        //            // считаем бары
        //            int bars = _lifecycle.IncBars(posKey);

        //            // --- TIME STOP ---
        //            if (signal.TimeStopBars.HasValue && bars >= signal.TimeStopBars.Value)
        //            {
        //                _logger.LogWarning(
        //                    "[EXIT][{symbol}] IMPULSE_CONTINUATION TimeStop → FULL CLOSE ({bars} bars)",
        //                    symbol, bars);

        //                return;
        //            }

        //            // --- LOSS OF IMPULSE (optional but recommended) ---
        //            if (klines != null && klines.Count >= 3)
        //            {
        //                int i = klines.Count - 1;
        //                var c0 = klines[i];
        //                var c1 = klines[i - 1];

        //                decimal atr = 0m;


        //                if (signal?.Atr != null && signal.Atr.Value > 0)
        //                    atr = signal.Atr.Value;

        //                decimal body0 = Math.Abs(c0.ClosePrice - c0.OpenPrice);
        //                decimal body1 = Math.Abs(c1.ClosePrice - c1.OpenPrice);

        //                bool impulseLost =
        //                    body0 < atr * 0.2m &&
        //                    body1 < atr * 0.2m;

        //                if (impulseLost)
        //                {
        //                    _logger.LogWarning(
        //                        "[EXIT][{symbol}] IMPULSE_CONTINUATION impulse lost → FULL CLOSE",
        //                        symbol);

        //                    await ClosePositionMarketAsync(symbol, client, pos, ct);

        //                    return;
        //                }
        //            }
        //            // ⛔ ВАЖНО: если ForceFullExit — НИЧЕГО больше не делаем
        //            // запрещаем partial / BE / trailing ниже
        //            return;
        //        }



        //        decimal entry = pos.EntryPrice;

        //        if (entry <= 0)
        //        {
        //            key = $"{symbol}_{side}";

        //            if (!_restoredEntries.TryGetValue(key, out entry))
        //            {
        //                var restored = await ResolveEntryFromExchangeAsync(client, symbol, side, ct);
        //                if (!restored.HasValue)
        //                {
        //                    _logger.LogError("[SUPERVISOR][{symbol}] Entry unresolved → skip SL/TP", symbol);
        //                    return;
        //                }

        //                entry = restored.Value;
        //                _restoredEntries[key] = entry;

        //                _logger.LogWarning(
        //                    "[SUPERVISOR][{symbol}] Entry restored from exchange = {entry}",
        //                    symbol, entry);
        //            }
        //        }

        //        // In case signal missing ATR in supervisor context, try compute
        //        decimal atr14 = 0m;
        //        if (signal?.Atr != null && signal.Atr.Value > 0)
        //            atr14 = signal.Atr.Value;
        //        else if (klines != null && klines.Count >= 40)
        //            atr14 = _marketData.CalculateAtr(klines, 15);

        //        // === Side-specific orders (Hedge) ===
        //        var orders = allOrders.Where(o => o.PositionSide == side).ToList();

        //        // === Find SL/TP ===
        //        var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

        //        bool hasMultipleSL = orders.Count(o => o.Type == FuturesOrderType.StopMarket) > 1;
        //        bool hasMultipleTP = orders.Count(o => o.Type == FuturesOrderType.TakeProfitMarket) > 1;

        //        if (hasMultipleSL)
        //            _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] Multiple SL detected → skip SL create", symbol, side);

        //        if (hasMultipleTP)
        //            _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] Multiple TP detected → skip TP create", symbol, side);

        //        var sl = orders.FirstOrDefault(o => o.Side == closeSide && o.Type == FuturesOrderType.StopMarket);
        //        var tp = orders.FirstOrDefault(o => o.Side == closeSide && o.Type == FuturesOrderType.TakeProfitMarket);

        //        // =================================================================
        //        // 🔁 RESTART PROTECTION (NO KLINES / NO MEMORY)
        //        // =================================================================
        //        if (sl != null)
        //        {
        //            var slPrice =
        //sl.StopPrice > 0
        //    ? sl.StopPrice
        //    : sl.Price;


        //            if (slPrice > 0)
        //            {
        //                var last = pos.MarkPrice > 0 ? pos.MarkPrice : entry;
        //                var guardKey = BuildPosGuardKey(symbol, side, entry, qtyAbs);

        //                bool slBelowEntry =
        //                    side == PositionSide.Long
        //                        ? slPrice < entry
        //                        : slPrice > entry;

        //                bool marketInProfit =
        //                    side == PositionSide.Long
        //                        ? last > entry
        //                        : last < entry;

        //                if (marketInProfit && slBelowEntry && !_beMoved.ContainsKey(guardKey))
        //                {
        //                    decimal minimalBe =
        //                        side == PositionSide.Long
        //                            ? entry + entry * 0.0005m   // ~0.05%
        //                            : entry - entry * 0.0005m;

        //                    await UpdateSL_ProAsync(
        //                        client,
        //                        symbol,
        //                        side,
        //                        qtyAbs,
        //                        sl,
        //                        entry,
        //                        minimalBe,
        //                        signal,
        //                        ct);

        //                    _beMoved[guardKey] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        //                    _logger.LogWarning(
        //                        "[RESTART-BE][{symbol}][{side}] SL moved to minimal BE (no klines)",
        //                        symbol, side);
        //                }
        //            }
        //        }

        //        // =================================================================
        //        // v8.2 PRO: STARTUP / LIVE BE REHYDRATION (WITH KLINES)
        //        // =================================================================
        //        if (klines != null && klines.Count >= 50 && atr14 > 0 && entry > 0)
        //        {
        //            var guardKey = BuildPosGuardKey(symbol, side, entry, qtyAbs);

        //            // Если позиция уже в плюсе, а BE ещё не отмечен — двигаем SL в минимальный BE
        //            // Порог мягкий (0.30 ATR), чтобы не ждать 1.2 ATR

        //            if (sl != null)
        //            {
        //                var keey = BuildBeKey(symbol, side, entry);
        //                var stage = _beStage.GetValueOrDefault(keey, BeStage.None);

        //                if (stage < BeStage.Rehydrate)
        //                {
        //                    var last = klines[^1].ClosePrice;

        //                    bool eligible =
        //                        side == PositionSide.Long
        //                            ? last >= entry + atr14 * 0.30m
        //                            : last <= entry - atr14 * 0.30m;

        //                    if (eligible)
        //                    {
        //                        decimal rehydrateBe =
        //                            side == PositionSide.Long
        //                                ? entry + entry * 0.0004m
        //                                : entry - entry * 0.0004m;

        //                        var ok = await UpdateSL_ProAsync(
        //                            client, symbol, side, qtyAbs, sl, entry, rehydrateBe, signal, ct);

        //                        if (ok)
        //                        {
        //                            _beStage[keey] = BeStage.Rehydrate;

        //                            _logger.LogWarning(
        //                                "[BE][REHYDRATE][{symbol}][{side}] SL={sl}",
        //                                symbol, side, rehydrateBe);
        //                        }
        //                    }
        //                }
        //            }

        //            // 1) EARLY TP (ЖЁСТКО после BE) — у тебя уже есть HARD RULE внутри метода
        //            await TryEarlyPartialTakeAsync(client, symbol, side, qtyAbs, entry, atr14, signal, klines, ct);

        //            // 2) Стандартный BE по ATR (сработает позже, когда реально будет +1.2 ATR)
        //            if (sl != null)
        //                await TryMoveSlToBeAsync(client, symbol, side, qtyAbs, entry, atr14, sl, signal, klines, ct);
        //        }


        //        // PROFIT HARVEST (ПОСЛЕ early/BE, ДО restore SL/TP)
        //        // =================================================================
        //        if (klines != null && klines.Count >= 50)
        //        {
        //            decimal aiEdgeScore =
        //                _regimeNow is MarketRegime.StrongUpTrend or MarketRegime.StrongDownTrend
        //                    ? 0.82m
        //                    : 0.62m;

        //            await TryHarvestProfitAsync(
        //                client,
        //                _engineState,
        //                symbol,
        //                side,
        //                pos,
        //                klines,
        //                aiEdgeScore,
        //                minUsd: 4m,
        //                ct);
        //        }



        //        // 1) SL отсутствует → аварийный SL (если нет дублей)
        //        if (sl == null && !hasMultipleSL)
        //        {
        //            await CreateEmergencySLAsync(client, symbol, side, qtyAbs, entry, signal, ct);
        //            _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] SL restored", symbol, side);
        //            //return;
        //        }

        //        // 2) TP отсутствует → аварийный TP (если нет дублей)
        //        if (tp == null && !hasMultipleTP)
        //        {
        //            await CreateEmergencyTPAsync(client, symbol, side, qtyAbs, entry, signal, ct);
        //            _logger.LogWarning("[SUPERVISOR][{symbol}][{side}] TP restored", symbol, side);
        //            // return;
        //        }

        //        // 3) Трейлинг + раннер
        //        if (klines != null && klines.Count >= 50)
        //        {
        //            await ManageRunnerTpAsync(client, symbol, side, qtyAbs, entry, orders, signal, klines, ct);
        //            await ManageRunnerTpExtensionAsync(client, symbol, side, qtyAbs, entry, signal, orders, klines, ct);
        //            await MultiLayerTrailingAsync(client, symbol, side, qtyAbs, entry, signal, orders, klines, ct);
        //        }
        //    }


        private async Task<decimal?> ResolveEntryFromExchangeAsync(
    BinanceRestClient client,
    string symbol,
    PositionSide side,
    CancellationToken ct)
        {
            var trades = await client.UsdFuturesApi.Trading.GetUserTradesAsync(
                symbol: symbol,
                limit: 50,
                ct: ct);

            if (!trades.Success || trades.Data == null)
                return null;

            var lastOpen = trades.Data
                .Where(t => t.PositionSide == side)
                .OrderByDescending(t => t.Timestamp)
                .FirstOrDefault();

            return lastOpen?.Price;
        }

        private void MarkProtection(string symbol)
        {
            var key = EngineState.Key(symbol);
            var st = _engineState.Symbols.GetOrAdd(key, _ => new SymbolState());
            st.LastProtectionUtc = DateTime.UtcNow;

            _logger.LogInformation(
                "[PROTECTION][{symbol}] protection marked @ {time}",
                symbol, st.LastProtectionUtc);
        }

        // =====================================================================
        // EARLY TP (Partial close) — ключевой фикс v8.2
        // =====================================================================
        private async Task TryEarlyPartialTakeAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entry,
            decimal atr,
            TradeSignal? signal,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            CancellationToken ct)
        {
            // MANUAL POSITION GUARD: never auto-close partial on manual positions.
            // The user manages their own exits when trading manually.
            if (signal?.IsManual == true)
            {
                _logger.LogDebug("[EARLY-TP][{sym}][{side}] Manual position — skip early partial", symbol, side);
                return;
            }

            // Блокируем, если LiquidityGuard сигналит опасность (не лезем в рынок лишний раз)
            if (_liquidityGuard.LastDanger?.Block == true)
                return;

            // ⚠️ skip early TP if liquidity was recent (soft protection)
            // if liquidity recent → wait a bit, but allow early TP later
            if (_liquidityGuard.IsDangerRecent(TimeSpan.FromMinutes(5)))
            {
                if (DateTime.UtcNow - _liquidityGuard.LastDanger!.UtcTime < TimeSpan.FromMinutes(2))
                    return;
            }

            var last = klines[^1].ClosePrice;

            var lastCandle = klines[^1];
            var body = Math.Abs(lastCandle.ClosePrice - lastCandle.OpenPrice);
            var wickAgainst =
                side == PositionSide.Long
                    ? lastCandle.HighPrice - lastCandle.ClosePrice
                    : lastCandle.ClosePrice - lastCandle.LowPrice;

            // если свеча с хвостом против — это не импульс
            if (wickAgainst > body * 0.8m)
                return;


            bool reached =
    side == PositionSide.Long
        ? last >= entry + atr * 0.90m
        : last <= entry - atr * 0.90m;

            // =====================================================
            // FUNDING RATE ACCELERATION
            // Если funding высокий И до следующего списания < 30 мин —
            // снижаем порог EarlyTP с 0.90 до 0.65 ATR
            // Цель: зафиксировать прибыль ДО списания funding
            // =====================================================
            if (!reached && _fundingRate != null)
            {
                var funding = _fundingRate.Get(symbol);
                if (funding?.ShouldAccelerateTP == true)
                {
                    bool reachedEarly =
                        side == PositionSide.Long
                            ? last >= entry + atr * 0.65m
                            : last <= entry - atr * 0.65m;

                    if (reachedEarly)
                    {
                        reached = true;
                        _logger.LogInformation(
                            "[EARLY-TP][{symbol}][{side}] Accelerated by funding: rate={rate:P4} nextIn={min:F0}min",
                            symbol, side,
                            funding.PredictedRate,
                            funding.MinutesToNextFunding);
                    }
                }
            }

            if (!reached) return;

            var guardKey = BuildPosGuardKey(symbol, side, entry, qty);

            // =======================
            // 🔒 RULE: EarlyTP запускается если:
            // 1) BE уже перемещён (штатный путь), ИЛИ
            // 2) BE ещё не перемещён, но цена достигла +1.5 ATR
            //    (fallback — защита если ProbeSide не успел)
            // =======================
            bool beAlreadyMoved = _beMoved.ContainsKey(BuildExitKey(symbol, side, entry));

            if (!beAlreadyMoved)
            {
                // fallback: разрешаем EarlyTP только при более глубоком профите
                bool deepProfit =
                    side == PositionSide.Long
                        ? last >= entry + atr * 1.5m
                        : last <= entry - atr * 1.5m;

                if (!deepProfit)
                    return;
            }

            if (_earlyTpDone.ContainsKey(guardKey))
                return;

            var closeQty = Math.Round(qty * 0.35m, 8);
            if (closeQty <= 0) return;

            // =====================================================
            // MIN NOTIONAL GUARD — после частичных закрытий
            // qty уже уменьшен, closeQty может быть слишком мал
            // =====================================================
            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            closeQty = Math.Max(closeQty, filters.minQty);
            closeQty = Math.Floor(closeQty / filters.step) * filters.step;

            decimal closeNotional = closeQty * last;
            if (closeNotional < filters.minNotional || closeQty <= 0)
            {
                _logger.LogDebug("[EARLY-TP][{symbol}][{side}] skip — closeQty too small notional={n}", symbol, side, closeNotional);
                return;
            }

            // Не закрываем больше чем есть
            closeQty = Math.Min(closeQty, Math.Round(qty * 0.9m, 8));

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            _dispatcher.Enqueue(async ct =>
            {
                using var c = _factory.CreateRestClient();
                var res = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: closeSide,
                    type: FuturesOrderType.Market,
                    quantity: closeQty,
                    positionSide: side,
                    selfTradePreventionMode: SelfTradePreventionMode.ExpireMaker,
                    ct: ct);


                if (!res.Success)
                {
                    _logger.LogWarning("[EARLY-TP][{symbol}][{side}] Market partial close failed: {err}", symbol, side, res.Error);
                    return;
                }


                MarkProtection(symbol);

            });


            _earlyTpDone[guardKey] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _earlyTpDone[guardKey] = now;

            // 🔒 BLOCK HARVEST for 8 seconds after EARLY-TP
            _recentPartialClose[$"{symbol}|{side}"] = now;

            _logger.LogWarning(
                "[EARLY-TP][{symbol}][{side}] Partial profit fixed {closed}/{total} @price={price} (+0.9ATR)",
                symbol, side, closeQty, qty, last);

            // Optional learning hook
            try
            {
                if (signal != null && !signal.IsManual)
                {
                    var sigSide = side == PositionSide.Long ? SignalSide.Buy : SignalSide.Sell;
                    _aiLearning.RecordTrade(symbol, sigSide, entry, last, _regimeNow);
                }
            }
            catch { }
        }

        enum BeStage
        {
            None = 0,
            Rehydrate = 1,
            Atr = 2,
            Trailing = 3
        }

        private string BuildBeKey(string symbol, PositionSide side, decimal entry)
    => $"{symbol}:{side}:{entry}";


        // =====================================================================
        // SL → BE (+ buffer, structural-aware, liquidity-safe)
        // =====================================================================
        private async Task TryMoveSlToBeAsync(
        BinanceRestClient client,
        string symbol,
        PositionSide side,
        decimal qty,
        decimal entry,
        decimal atr,
        BinanceUsdFuturesOrder slOrder,
        TradeSignal? signal,
        IReadOnlyList<BinanceFuturesUsdtKline> klines,
        CancellationToken ct)
        {
            if (klines == null || klines.Count < 10) return;
            if (atr <= 0 || entry <= 0) return;

            var key = BuildBeKey(symbol, side, entry);
            var stage = _beStage.GetValueOrDefault(key, BeStage.None);

            if (stage >= BeStage.Atr)
                return;

            var last = klines[^1].ClosePrice;

            bool lowAtr = atr / entry < 0.004m;

            decimal trigger = lowAtr
                ? Math.Max(atr * 0.25m, entry * 0.0006m)
                : atr * 1.20m;

            bool reached =
                side == PositionSide.Long
                    ? last >= entry + trigger
                    : last <= entry - trigger;

            if (!reached)
            {
                _logger.LogDebug("[BE][ATR][{symbol}] not reached last={last} trigger={trigger}");
                return;
            }

            decimal buffer = lowAtr
                ? entry * 0.0005m
                : atr * 0.15m;

            if (_liquidityGuard.IsDangerRecent(TimeSpan.FromSeconds(90)))
                buffer *= 0.5m;

            decimal structural =
                side == PositionSide.Long
                    ? klines.TakeLast(5).Min(x => x.LowPrice)
                    : klines.TakeLast(5).Max(x => x.HighPrice);

            decimal beBase =
                side == PositionSide.Long
                    ? entry + buffer
                    : entry - buffer;

            // ❗ structural НЕ МОЖЕТ УХУДШАТЬ BE
            decimal finalSl =
                side == PositionSide.Long
                    ? Math.Max(beBase, structural)
                    : Math.Min(beBase, structural);

            decimal? oldSl =
                slOrder.StopPrice > 0
                    ? slOrder.StopPrice
                    : entry;


            bool improves =
                side == PositionSide.Long
                    ? finalSl > oldSl
                    : finalSl < oldSl;

            if (!improves)
            {
                _logger.LogDebug(
                    "[BE][ATR][{symbol}] no improve old={old} new={new}",
                    symbol, oldSl, finalSl);
                return;
            }

            var ok = await UpdateSL_ProAsync(
                client, symbol, side, qty, slOrder, entry, finalSl, signal, ct);

            if (!ok) return;

            _beStage[key] = BeStage.Atr;
            MarkProtection(symbol);

            _logger.LogWarning(
                "[BE][ATR][{symbol}][{side}] SL={sl}",
                symbol, side, finalSl);
        }


        private static string BuildPosGuardKey(string symbol, PositionSide side, decimal entry, decimal qty)
        {
            // грубый, но рабочий ключ: символ+side+entry+qty (округлим)
            string E(decimal v) => v.ToString("0.########", CultureInfo.InvariantCulture);
            return $"{symbol}|{side}|e={E(entry)}|q={E(qty)}";
        }
        // =====================================================================
        // EMERGENCY SL  (TRY NORMAL → FALLBACK ALGO RAW on -4120)
        // =====================================================================
        private async Task CreateEmergencySLAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entryPrice,
            TradeSignal? signal,
            CancellationToken ct)
        {
            try
            {
                // ==========================================================
                // 1) ЖЁСТКАЯ ПРОВЕРКА ФАКТИЧЕСКОЙ ПОЗИЦИИ
                // ==========================================================
                var posInfo = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, ct: ct);
                if (!posInfo.Success || posInfo.Data == null)
                    return;

                var pos = posInfo.Data.FirstOrDefault(p =>
                    p.PositionSide == side &&
                    Math.Abs(p.Quantity) > 0);

                if (pos == null)
                {
                    _logger.LogWarning(
                        "[SUPERVISOR][{symbol}][{side}] SKIP SL → no open position",
                        symbol, side);
                    return;
                }

                // ==========================================================
                // 2) КОЛИЧЕСТВО ЗАКРЫТИЯ
                // ==========================================================
                var closeQty = Math.Min(Math.Abs(pos.Quantity), qty);
                if (closeQty <= 0)
                    return;

                // ==========================================================
                // 3) РАСЧЁТ SL
                // ==========================================================
                decimal rawSl;

                if (signal?.StopLoss > 0)
                {
                    rawSl = signal.StopLoss;
                }
                else
                {
                    var kl = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 100);
                    if (kl.Count < 30) return;

                    var atr = _marketData.CalculateAtr(kl, 14);
                    if (atr <= 0) return;

                    var atrMult = _regimeNow switch
                    {
                        MarketRegime.Range => 1.2m,
                        MarketRegime.Squeeze => 1.5m,
                        MarketRegime.UpTrend or MarketRegime.DownTrend => 1.8m,
                        MarketRegime.VolatileChop => 2.0m,
                        _ => 2.2m
                    };

                    rawSl = side == PositionSide.Long
                        ? entryPrice - atr * atrMult
                        : entryPrice + atr * atrMult;
                }

                // ==========================================================
                // 4) TICK + MARK PRICE SAFETY
                // ==========================================================
                var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
                var tick = filters.tickSize > 0 ? filters.tickSize : 0.0001m;

                decimal sl = Math.Round(rawSl / tick) * tick;
                decimal mark = await GetMarkPriceSafeAsync(client, symbol, entryPrice, ct);

                if (mark > 0)
                {
                    if (side == PositionSide.Long && sl >= mark)
                        sl = mark - tick;

                    if (side == PositionSide.Short && sl <= mark)
                        sl = mark + tick;
                }

                var orderSide = side == PositionSide.Long
                    ? OrderSide.Sell
                    : OrderSide.Buy;

                // ==========================================================
                // 5) ОТПРАВКА (NORMAL → ALGO RAW)
                // ==========================================================
                _dispatcher.Enqueue(async ct =>
                {
                    using var c = _factory.CreateRestClient();
                    var res = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: symbol,
                        side: orderSide,
                        type: FuturesOrderType.StopMarket,
                        quantity: closeQty,
                        positionSide: side,
                        stopPrice: sl,
                        reduceOnly: null,
                        selfTradePreventionMode: SelfTradePreventionMode.ExpireMaker,
                        ct: ct);

                    if (res.Success)
                    {
                        _logger.LogInformation(
                            "[SUPERVISOR] SL CREATED (NORMAL) {symbol} {side} sl={sl}",
                            symbol, side, sl);
                        return;
                    }

                    // ======================================================
                    // 6) FALLBACK → ALGO RAW (-4120)
                    // ======================================================
                    if (IsAlgoRequired(res.Error))
                    {
                        _logger.LogWarning(
                            "[SUPERVISOR] SL requires ALGO endpoint (-4120) → RAW ALGO {symbol} {side}",
                            symbol, side);

                        var ok = await _algoRaw.PlaceConditionalAsync(
                            symbol: symbol,
                            side: orderSide,
                            positionSide: side,
                            type: "STOP_MARKET",
                            quantity: closeQty,
                            triggerPrice: sl,
                            workingType: "CONTRACT_PRICE",
                            reduceOnly: null,
                            ct: ct,
                            clientAlgoId: $"{SL_PREFIX}{symbol}_{side}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");

                        if (ok)
                        {
                            _logger.LogInformation(
                                "[SUPERVISOR] SL CREATED (ALGO-RAW) {symbol} {side} sl={sl}",
                                symbol, side, sl);
                            return;
                        }

                        _logger.LogWarning(
                            "[SUPERVISOR] SL skipped: ALGO-RAW failed {symbol} {side}",
                            symbol, side);
                        return;
                    }

                    _logger.LogError(
                        "[SUPERVISOR] ERROR SL create (NORMAL) {symbol}: {err}",
                        symbol, res.Error);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SUPERVISOR] EX SL create {symbol}", symbol);
            }
        }

        // =====================================================================
        // EMERGENCY TP  (TRY NORMAL → FALLBACK ALGO RAW on -4120)
        // =====================================================================
        private async Task CreateEmergencyTPAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entryPrice,
            TradeSignal? signal,
            CancellationToken ct)
        {
            try
            {
                // ==========================================================
                // 1) ЖЁСТКАЯ ПРОВЕРКА ФАКТИЧЕСКОЙ ПОЗИЦИИ (ОБЯЗАТЕЛЬНО)
                // ==========================================================
                //var posInfo = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, ct: ct);
                //if (!posInfo.Success || posInfo.Data == null)
                //    return;

                //var pos = posInfo.Data.FirstOrDefault(p =>
                //    p.PositionSide == side &&
                //    Math.Abs(p.Quantity) > 0);

                //if (pos == null)
                //{
                //    _logger.LogWarning(
                //        "[SUPERVISOR][{symbol}][{side}] SKIP TP → no open position",
                //        symbol, side);
                //    return;
                //}

                // ==========================================================
                // 2) КОЛИЧЕСТВО ЗАКРЫТИЯ (НЕ БОЛЬШЕ ЧЕМ ФАКТИЧЕСКАЯ ПОЗИЦИЯ)
                // ==========================================================
                //var closeQty = Math.Min(Math.Abs(pos.Quantity), qty);
                //if (closeQty <= 0)
                //    return;
                qty = Math.Abs(qty);

                if (qty == 0)
                {
                    _logger.LogWarning(
                        "[SUPERVISOR][{symbol}][{side}] SKIP TP → qty=0",
                        symbol, side);
                    return;
                }
                // ==========================================================
                // 3) РАСЧЁТ TP
                // ==========================================================
                decimal trigger;

                if (signal?.TakeProfits != null && signal.TakeProfits.Count > 0)
                {
                    trigger = signal.TakeProfits[0];
                    _logger.LogInformation(
                        "[SUPERVISOR][{symbol}][{side}] TP from signal: {tp}",
                        symbol, side, trigger);
                }
                else
                {
                    var kl = await _marketData.GetKlines(symbol, KlineInterval.OneMinute, 100);
                    if (kl.Count < 30) return;

                    var atr = _marketData.CalculateAtr(kl, 14);
                    if (atr <= 0) return;

                    // Получаем текущую mark price
                    decimal markNow = 0m;
                    try
                    {
                        var mk = await client.UsdFuturesApi.ExchangeData.GetMarkPriceAsync(symbol, ct);
                        if (mk.Success) markNow = mk.Data.MarkPrice;
                    }
                    catch { }

                    // DCA positions: TP at 8% ROI from entry (long-term hold).
                    // Signal positions: TP at ATR×2.0 from mark (short-term).
                    decimal basePrice = markNow > 0 ? markNow : entryPrice;
                    bool isDcaEmergency = IsDcaSymbol(symbol);
                    if (isDcaEmergency)
                    {
                        // 8% from entry — conservative long-term TP
                        trigger = side == PositionSide.Long
                            ? entryPrice * 1.08m
                            : entryPrice * 0.92m;
                        _logger.LogInformation(
                            "[SUPERVISOR][{symbol}][{side}] DCA emergency TP at 8% ROI: entry={entry} → tp={tp}",
                            symbol, side, entryPrice, trigger);
                    }
                    else
                    {
                        trigger = side == PositionSide.Long
                            ? basePrice + atr * 2.0m
                            : basePrice - atr * 2.0m;
                        _logger.LogInformation(
                            "[SUPERVISOR][{symbol}][{side}] TP from ATR: entry={entry} mark={mark} atr={atr} → tp={tp}",
                            symbol, side, entryPrice, basePrice, atr, trigger);
                    }
                }

                // ==========================================================
                // 4) ПРИВЯЗКА К TICK SIZE
                // ==========================================================
                var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);

                var tick = filters.tickSize > 0 ? filters.tickSize : 0.0001m;
                var step = filters.step > 0 ? filters.step : 0.0001m;

                // normalize qty
                qty = Math.Floor(qty / step) * step;
                if (qty <= 0)
                {
                    _logger.LogWarning(
                        "[SUPERVISOR][{symbol}] TP qty normalized to 0",
                        symbol);
                    return;
                }

                // Округляем trigger к tick size
                if (tick > 0)
                    trigger = Math.Round(trigger / tick) * tick;

                // Валидация против mark price
                var markRes = await client.UsdFuturesApi.ExchangeData.GetMarkPriceAsync(symbol, ct);
                if (markRes.Success)
                {
                    var mp = markRes.Data.MarkPrice;
                    // TP для лонга должен быть ВЫШЕ mark, для шорта — НИЖЕ
                    if (side == PositionSide.Long && trigger <= mp)
                    {
                        trigger = mp + tick * 5;
                        _logger.LogWarning("[SUPERVISOR] TP adjusted above mark: {tp} (mark={mp})", trigger, mp);
                    }
                    else if (side == PositionSide.Short && trigger >= mp)
                    {
                        trigger = mp - tick * 5;
                        _logger.LogWarning("[SUPERVISOR] TP adjusted below mark: {tp} (mark={mp})", trigger, mp);
                    }
                }

                //if (side == PositionSide.Long && trigger <= entryPrice)
                //    trigger = entryPrice + tick * 3;

                //if (side == PositionSide.Short && trigger >= entryPrice)
                //    trigger = entryPrice - tick * 3;

                var orderSide = side == PositionSide.Long
                    ? OrderSide.Sell
                    : OrderSide.Buy;

                // ==========================================================
                // 5) ОТПРАВКА ЧЕРЕЗ ДИСПЕТЧЕР (NORMAL → ALGO RAW)
                // ==========================================================
                _dispatcher.Enqueue(async _ =>
                {
                    using var c = _factory.CreateRestClient();
                    var res = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: symbol,
                        side: orderSide,
                        type: FuturesOrderType.TakeProfitMarket,
                        quantity: qty,
                        positionSide: side,
                        stopPrice: trigger,
                        reduceOnly: null,
                        selfTradePreventionMode: SelfTradePreventionMode.ExpireMaker,
                        ct: CancellationToken.None);

                    if (res.Success)
                    {
                        _logger.LogInformation(
                            "[SUPERVISOR] TP CREATED (NORMAL) {symbol} {side} tp={tp}",
                            symbol, side, trigger);
                        return;
                    }

                    // ======================================================
                    // 6) FALLBACK → ALGO RAW (-4120)
                    // ======================================================
                    if (IsAlgoRequired(res.Error))
                    {
                        _logger.LogWarning(
                            "[SUPERVISOR] TP requires ALGO endpoint (-4120) → RAW ALGO {symbol} {side}",
                            symbol, side);

                        _dispatcher.Enqueue(async _ =>
                        {
                            var ok = await _algoRaw.PlaceConditionalAsync(
                                symbol: symbol,
                                side: orderSide,
                                positionSide: side,
                                type: "TAKE_PROFIT_MARKET",
                                quantity: qty,
                                triggerPrice: trigger,
                                workingType: "CONTRACT_PRICE",
                                reduceOnly: null,
                                ct: CancellationToken.None,
                                clientAlgoId: $"{TP_PREFIX}{symbol}_{side}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");

                            if (ok)
                            {
                                _logger.LogInformation(
                     "[SUPERVISOR] TP CREATED (ALGO) {symbol} {side}",
                     symbol, side);
                                return;
                            }

                            _logger.LogError(
                  "[SUPERVISOR] TP ALGO FAILED {symbol} {side}",
                  symbol, side);
                        });

                        return;
                    }


                    _logger.LogError(
                        "[SUPERVISOR] TP FAILED {symbol} {side} {err}",
                        symbol, side, res.Error?.Message);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SUPERVISOR] EX create TP {symbol}", symbol);
            }
        }


        private static bool IsAlgoRequired(CryptoExchange.Net.Objects.Error? err)
        {
            if (err == null) return false;

            if (err.Code == -4120) return true;

            var msg = (err.Message ?? string.Empty).ToLowerInvariant();
            return msg.Contains("algo order api") || msg.Contains("algoorder") || msg.Contains("not supported for this endpoint");
        }

        // =====================================================================
        // PLACEHOLDERS: keep your existing methods below
        // =====================================================================
        private async Task ManageRunnerTpExtensionAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entryPrice,
            TradeSignal? signal,
            List<BinanceUsdFuturesOrder> orders,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            CancellationToken ct)
        {
            await Task.CompletedTask;
        }
        // =====================================================================
        // RUNNER (твоя логика) + SL update (v8.2 PRO)
        // =====================================================================
        private async Task ManageRunnerTpAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entryPrice,
            List<BinanceUsdFuturesOrder> orders,
            TradeSignal? signal,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            CancellationToken ct)
        {
            // ===== STRONG TREND RUNNER FIX =====
            bool strongTrend =
                _regimeNow == MarketRegime.StrongUpTrend ||
                _regimeNow == MarketRegime.StrongDownTrend;

            if (!strongTrend)
                return;


            if (signal?.TakeProfit is null || signal.TakeProfit <= 0)
                return;

            var priceRes = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(symbol, ct: ct);
            if (!priceRes.Success || priceRes.Data == null)
                return;

            var price = priceRes.Data.Price;

            bool tpHit =
                side == PositionSide.Long ? price >= signal.TakeProfit : price <= signal.TakeProfit;

            if (!tpHit) return;

            bool trendOk =
                side == PositionSide.Long
                    ? _regimeNow is MarketRegime.StrongUpTrend or MarketRegime.UpTrend
                    : _regimeNow is MarketRegime.StrongDownTrend or MarketRegime.DownTrend;

            if (!trendOk) return;

            var atr = signal.Atr ?? 0m;
            if (atr <= 0 || klines.Count < 2) return;

            var last = klines[^1];
            var body = Math.Abs(last.ClosePrice - last.OpenPrice);
            if (body < atr * 0.7m) return;

            if (_liquidityGuard.LastDanger?.Block == true) return;

            // 1) частичное закрытие (70%)
            var closeQty = Math.Round(qty * 0.7m, 8);
            var runnerQty = qty - closeQty;
            if (closeQty <= 0 || runnerQty <= 0) return;

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
            _dispatcher.Enqueue(async ct =>
            {
                await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: closeSide,
                type: FuturesOrderType.Market,
                quantity: closeQty,
                positionSide: side,
                selfTradePreventionMode: SelfTradePreventionMode.ExpireMaker,
                ct: ct);

                _logger.LogInformation("[TP-EXT][{symbol}] Partial TP executed {closed}/{total}, runner={runner}",
                    symbol, closeQty, qty, runnerQty);

                // 2) перенос SL → BE + 0.25 ATR
                decimal newSl =
                    side == PositionSide.Long ? entryPrice + atr * 0.25m : entryPrice - atr * 0.25m;

                var slOrder = orders.FirstOrDefault(o =>
                    o.Type == FuturesOrderType.StopMarket &&
                    o.PositionSide == side);

                if (slOrder != null)
                {
                    await UpdateSL_ProAsync(client, symbol, side, runnerQty, slOrder, entryPrice, newSl, signal, ct);
                }

                _logger.LogWarning("[TP-EXT][{symbol}] Runner activated | new SL={sl}", symbol, newSl);
            });
        }

        /// <summary>
        /// v8.2 PRO SL update:
        /// - Cancel old SL
        /// - Place new SL via NORMAL endpoint
        /// - If -4120 -> ALGO-RAW
        /// - NO reduceOnly (важно для Hedge/ошибок -1106)
        /// - WorkingType.Mark используем осторожно: сначала пробуем, если Binance ругается — повтор без него
        /// </summary>
        private Task<bool> UpdateSL_ProAsync(
         BinanceRestClient client,
         string symbol,
         PositionSide side,
         decimal qty,
         BinanceUsdFuturesOrder slOrder,
         decimal entry,
         decimal newSl,
         TradeSignal? signal,
         CancellationToken ct)
        {
            if (qty <= 0 || newSl <= 0) return Task.FromResult(false);

            decimal oldSl = slOrder.StopPrice ?? slOrder.Price;
            if (oldSl <= 0) return Task.FromResult(false);

            if (side == PositionSide.Long && newSl <= oldSl) return Task.FromResult(false);
            if (side == PositionSide.Short && newSl >= oldSl) return Task.FromResult(false);

            _dispatcher.Enqueue(async token =>
            {
                using var c = _factory.CreateRestClient();

                var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
                var tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;
                var s = Math.Round(newSl / tick) * tick;

                try
                {
                    try
                    {
                        await c.UsdFuturesApi.Trading.CancelOrderAsync(symbol, slOrder.Id, ct: token);
                    }
                    catch { }

                    // CRITICAL FIX: slOrder above comes from the regular
                    // GetOpenOrdersAsync endpoint, which cannot see algo
                    // orders at all — the real protective SL almost
                    // certainly lives there instead, meaning the cancel
                    // attempt above was silently doing nothing for it.
                    // This is the exact bug reported: a new BE-move SL
                    // gets placed while the previous one never actually
                    // gets removed. Explicitly find and cancel any
                    // existing BE/SL-tagged algo order for this side
                    // before placing the new one, independent of whatever
                    // slOrder did or didn't contain.
                    try
                    {
                        var existingAlgoOrders = await _algoRaw.GetOpenAlgoOrdersAsync(symbol, token);
                        foreach (var algo in existingAlgoOrders.Where(o =>
                            o.IsStop && o.PositionSide == side &&
                            o.ClientAlgoId != null &&
                            (o.ClientAlgoId.StartsWith(BE_PREFIX) || o.ClientAlgoId.StartsWith(SL_PREFIX))))
                        {
                            await _algoRaw.CancelAlgoOrderAsync(algo.AlgoId, token);
                        }
                    }
                    catch { }

                    var orderSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

                    var r1 = await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: symbol,
                        side: orderSide,
                        type: FuturesOrderType.StopMarket,
                        quantity: qty,
                        stopPrice: s,
                        positionSide: side,
                        workingType: WorkingType.Mark,
                        selfTradePreventionMode: SelfTradePreventionMode.ExpireMaker,
                        ct: token);

                    if (!r1.Success && IsAlgoRequired(r1.Error))
                    {
                        await _algoRaw.PlaceConditionalAsync(
                            symbol: symbol,
                            side: orderSide,
                            positionSide: side,
                            type: "STOP_MARKET",
                            quantity: qty,
                            triggerPrice: s,
                            workingType: "CONTRACT_PRICE",
                            reduceOnly: null,
                            ct: token,
                            clientAlgoId: $"{BE_PREFIX}{symbol}_{side}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
                    }
                    else if (!r1.Success)
                    {
                        await c.UsdFuturesApi.Trading.PlaceOrderAsync(
                            symbol: symbol,
                            side: orderSide,
                            type: FuturesOrderType.StopMarket,
                            quantity: qty,
                            stopPrice: s,
                            positionSide: side,
                            selfTradePreventionMode: SelfTradePreventionMode.ExpireMaker,
                            ct: token);
                    }
                    HookAiLearningOnSlMove(signal, symbol, side, entry, s);
                }
                catch { }
            });

            return Task.FromResult(true);
        }
        private void HookAiLearningOnSlMove(TradeSignal? signal, string symbol, PositionSide side, decimal entry, decimal newSl)
        {
            try
            {
                if (signal != null && !signal.IsManual)
                {
                    var sigSide = side == PositionSide.Long ? SignalSide.Buy : SignalSide.Sell;

                    _manualHandler.RegisterStop(symbol);
                    _aiLearning.RecordTrade(symbol, sigSide, entry, newSl, _regimeNow);
                }
            }
            catch { }
        }
        private async Task MultiLayerTrailingAsync(
            BinanceRestClient client,
            string symbol,
            PositionSide side,
            decimal qty,
            decimal entryPrice,
            TradeSignal? signal,
            List<BinanceUsdFuturesOrder> orders,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            CancellationToken ct)
        {
            await Task.CompletedTask;
        }
        // =====================================================================
        // MARK PRICE SAFE
        // =====================================================================
        private static async Task<decimal> GetMarkPriceSafeAsync(
            BinanceRestClient client,
            string symbol,
            decimal fallback,
            CancellationToken ct)
        {
            try
            {
                var r = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(symbol, ct: ct);
                if (r.Success && r.Data != null && r.Data.Price > 0)
                    return r.Data.Price;
            }
            catch { }

            return fallback > 0 ? fallback : 0m;
        }
        // =====================================================================
        // RAW BINANCE ALGO ORDER (POST /fapi/v1/algoOrder)
        // =====================================================================
        private sealed class BinanceAlgoOrderInfo
        {
            public long AlgoId;
            public string? ClientAlgoId;
            public string Symbol = "";
            public OrderSide Side;
            public PositionSide PositionSide;
            public string OrderType = ""; // "STOP" / "TAKE_PROFIT" (per Binance's algo-order naming)
            public decimal TriggerPrice;
            public decimal Quantity;

            public bool IsStop => OrderType.Contains("STOP", StringComparison.OrdinalIgnoreCase);
            public bool IsTakeProfit => OrderType.Contains("TAKE_PROFIT", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class BinanceAlgoOrderRaw
        {
            private readonly HttpClient _http;
            private readonly ILogger _logger;
            private readonly string _apiKey;
            private readonly string _apiSecret;
            private readonly string _baseUrl;

            // ── Server-time sync (fixes -1021 "timestamp ahead" errors) ──────
            // Binance rejects signed requests when local clock differs from
            // their NTP by more than 1000ms.  We fetch /fapi/v1/time once
            // and cache the offset; recvWindow=5000 provides an extra safety
            // buffer so occasional drift doesn't cause failures.
            private long   _timeOffsetMs  = 0;
            private DateTime _lastTimeSync = DateTime.MinValue;
            private readonly SemaphoreSlim _timeSyncLock = new(1, 1);
            private static readonly TimeSpan TimeSyncInterval = TimeSpan.FromMinutes(10);

            // ── GetOpenAlgoOrders 20-second cache (fixes HTTP 429) ────────────
            private readonly SemaphoreSlim _cacheLock = new(1, 1);
            private List<BinanceAlgoOrderInfo>? _cachedOrders;
            private string?   _cacheSymbol;
            private DateTime  _cacheExpiry = DateTime.MinValue;
            private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(20);

            public BinanceAlgoOrderRaw(IConfiguration cfg, IHttpClientFactory httpFactory, ILogger logger)
            {
                _logger = logger;

                _apiKey = cfg["Binance:ApiKey"] ?? string.Empty;
                // CRITICAL FIX: appsettings.json's real field name is
                // "SecretKey" (confirmed directly), not "ApiSecret" —
                // this was reading a key that doesn't exist, meaning
                // _apiSecret was empty the entire time and every
                // algo-order call (including this session's BE-move/
                // cleanup fixes) was silently failing the credentials
                // check before ever reaching the network.
                _apiSecret = cfg["Binance:SecretKey"] ?? cfg["Binance:ApiSecret"] ?? string.Empty;
                _baseUrl = (cfg["Binance:FuturesBaseUrl"] ?? "https://fapi.binance.com").TrimEnd('/');

                _http = httpFactory.CreateClient("BinanceAlgoRaw");
                _http.Timeout = TimeSpan.FromSeconds(8);
            }
            private async Task<long> GetBinanceTimestampAsync(CancellationToken ct)
            {
                // Fast path
                if (DateTime.UtcNow - _lastTimeSync < TimeSyncInterval)
                    return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _timeOffsetMs;

                await _timeSyncLock.WaitAsync(ct);
                try
                {
                    if (DateTime.UtcNow - _lastTimeSync < TimeSyncInterval)
                        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _timeOffsetMs;

                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/fapi/v1/time");
                        using var resp = await _http.SendAsync(req, ct);
                        if (resp.IsSuccessStatusCode)
                        {
                            var body = await resp.Content.ReadAsStringAsync(ct);
                            using var doc = System.Text.Json.JsonDocument.Parse(body);
                            if (doc.RootElement.TryGetProperty("serverTime", out var st))
                            {
                                long serverMs = st.GetInt64();
                                long localMs  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                _timeOffsetMs = serverMs - localMs;
                                _lastTimeSync = DateTime.UtcNow;
                                if (Math.Abs(_timeOffsetMs) > 500)
                                    _logger.LogWarning("[ALGO-RAW] Clock offset={off}ms — timestamps adjusted", _timeOffsetMs);
                            }
                        }
                    }
                    catch { /* non-fatal — use local clock */ }
                }
                finally { _timeSyncLock.Release(); }

                return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _timeOffsetMs;
            }

            public async Task<bool> PlaceConditionalAsync(
                string symbol,
                OrderSide side,
                PositionSide positionSide,
                string type,
                decimal quantity,
                decimal triggerPrice,
                string workingType,
                bool? reduceOnly,
                CancellationToken ct,
                string? clientAlgoId = null)
            {
                if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
                {
                    _logger.LogError("[ALGO-RAW] Missing Binance:ApiKey / Binance:ApiSecret in config");
                    return false;
                }

                var ts = await GetBinanceTimestampAsync(ct);
                string D(decimal v) => v.ToString("0.########", CultureInfo.InvariantCulture);

                var q = new List<KeyValuePair<string, string>>
                {
                    new("algoType",    "CONDITIONAL"),
                    new("symbol",      symbol),
                    new("side",        side == OrderSide.Buy ? "BUY" : "SELL"),
                    new("type",        type),
                    new("timestamp",   ts.ToString(CultureInfo.InvariantCulture)),
                    new("recvWindow",  "5000"),
                    new("workingType", workingType),
                    new("triggerPrice", D(triggerPrice)),
                    new("positionSide", positionSide.ToString().ToUpperInvariant()),
                    new("quantity",    D(quantity))
                };

                // CRITICAL FIX: this parameter previously didn't exist at
                // all on this method — every caller passing a BE_PREFIX/
                // SL_PREFIX/TR_PREFIX-tagged id (see the call sites below)
                // had that value silently dropped, since nothing here ever
                // added it to the request. Binance's clientAlgoId parameter
                // (max 32 chars, confirmed via official docs example) is
                // what actually gets echoed back in clientAlgoId on every
                // subsequent read — without sending it, Binance generates
                // its own random one, which is exactly why every cleanup
                // function's ClientAlgoId.StartsWith(prefix) check never
                // matched a single order.
                if (!string.IsNullOrWhiteSpace(clientAlgoId))
                    q.Add(new("clientAlgoId", clientAlgoId.Length > 32 ? clientAlgoId[..32] : clientAlgoId));

                // reduceOnly — только если positionSide == BOTH (в Hedge не шлём)
                if (reduceOnly.HasValue && positionSide == PositionSide.Both)
                    q.Add(new("reduceOnly", reduceOnly.Value ? "true" : "false"));

                var (query, rawQuery) = BuildQuery(q);
                var sig = Sign(rawQuery, _apiSecret);  // подписываем RAW строку

                var url = $"{_baseUrl}/fapi/v1/algoOrder?{query}&signature={sig}";

                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.TryAddWithoutValidation("X-MBX-APIKEY", _apiKey);

                try
                {
                    using var resp = await _http.SendAsync(req, ct);
                    var body = await resp.Content.ReadAsStringAsync(ct);

                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogError("[ALGO-RAW] HTTP {code} body={body}", (int)resp.StatusCode, body);
                        return false;
                    }

                    _logger.LogInformation("[ALGO-RAW] OK {symbol} {type} posSide={ps} trig={tp} body={body}",
                        symbol, type, positionSide, triggerPrice, body);

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ALGO-RAW] EX PlaceConditionalAsync {symbol}", symbol);
                    return false;
                }
            }

            // =====================================================
            // GetOpenAlgoOrdersAsync / CancelAlgoOrderAsync
            // =====================================================
            // CRITICAL FIX (found via the Web dashboard's TP/SL
            // display investigation): every BE-move / SL-update code
            // path in this file was calling the REGULAR
            // GetOpenOrdersAsync to check "does a protective order
            // already exist", and CancelOrderAsync to remove it.
            // Since Binance's mandatory Dec 9 2025 migration, ALL
            // conditional orders (STOP_MARKET/TAKE_PROFIT_MARKET —
            // exactly what this class places via PlaceConditionalAsync
            // above) live in a completely separate "Algo Order"
            // service. The regular open-orders endpoint genuinely does
            // not know these orders exist at all — so every "cancel
            // the old SL first" check here was finding nothing, every
            // single time, and immediately placing a brand new SL
            // without ever removing the previous one. That's the
            // direct cause of both reported symptoms: SL orders
            // multiplying rapidly (each BE-move cycle adds another,
            // since it never sees/cancels the last one) and leftover
            // orders surviving position close (the close-cleanup path
            // has the exact same blind spot).
            public async Task<List<BinanceAlgoOrderInfo>> GetOpenAlgoOrdersAsync(string? symbol, CancellationToken ct)
            {
                var result = new List<BinanceAlgoOrderInfo>();
                if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
                {
                    _logger.LogError("[ALGO-RAW] Missing Binance:ApiKey / Binance:ApiSecret in config");
                    return result;
                }

                // Fast path: return cached result (prevents 429)
                var now = DateTime.UtcNow;
                if (_cachedOrders != null && _cacheSymbol == symbol && now < _cacheExpiry)
                    return _cachedOrders;

                await _cacheLock.WaitAsync(ct);
                try
                {
                    now = DateTime.UtcNow;
                    if (_cachedOrders != null && _cacheSymbol == symbol && now < _cacheExpiry)
                        return _cachedOrders;

                var ts = await GetBinanceTimestampAsync(ct);
                var q = new List<KeyValuePair<string, string>>
                {
                    new("timestamp",  ts.ToString(CultureInfo.InvariantCulture)),
                    new("recvWindow", "5000"),
                };
                if (!string.IsNullOrEmpty(symbol)) q.Add(new("symbol", symbol));

                var (query, rawQuery) = BuildQuery(q);
                var sig = Sign(rawQuery, _apiSecret);
                var url = $"{_baseUrl}/fapi/v1/openAlgoOrders?{query}&signature={sig}";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("X-MBX-APIKEY", _apiKey);

                try
                {
                    using var resp = await _http.SendAsync(req, ct);
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        if (body.Contains("-1021")) _lastTimeSync = DateTime.MinValue;
                        _logger.LogError("[ALGO-RAW] GetOpenAlgoOrders HTTP {code} body={body}", (int)resp.StatusCode, body);
                        return result;
                    }

                    // CONFIRMED real response shape via official Binance
                    // docs: a plain top-level JSON array, not wrapped in
                    // an "orders" property.
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

                    foreach (var o in doc.RootElement.EnumerateArray())
                    {
                        decimal GetDec(string name) =>
                            o.TryGetProperty(name, out var v) && decimal.TryParse(v.GetString(), CultureInfo.InvariantCulture, out var d) ? d : 0m;
                        string GetStr(string name) => o.TryGetProperty(name, out var v) ? (v.GetString() ?? "") : "";
                        long GetLong(string name) =>
                            o.TryGetProperty(name, out var v)
                                ? (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l
                                   : long.TryParse(v.GetString(), out var l2) ? l2 : 0L)
                                : 0L;
                        string? GetClientId(string name) => o.TryGetProperty(name, out var v) ? v.GetString() : null;

                        result.Add(new BinanceAlgoOrderInfo
                        {
                            AlgoId = GetLong("algoId"),
                            ClientAlgoId = GetClientId("clientAlgoId"),
                            Symbol = GetStr("symbol"),
                            Side = GetStr("side").Equals("BUY", StringComparison.OrdinalIgnoreCase) ? OrderSide.Buy : OrderSide.Sell,
                            PositionSide = Enum.TryParse<PositionSide>(GetStr("positionSide"), true, out var ps) ? ps : PositionSide.Both,
                            OrderType = GetStr("orderType"), // "STOP" / "TAKE_PROFIT" / etc
                            TriggerPrice = GetDec("triggerPrice"),
                            Quantity = GetDec("quantity"),
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ALGO-RAW] EX GetOpenAlgoOrdersAsync {symbol}", symbol);
                }

                // Cache even empty result to prevent 429 hammering
                _cachedOrders = result;
                _cacheSymbol  = symbol;
                _cacheExpiry  = DateTime.UtcNow.Add(CacheTtl);
                return result;
                } // end cache lock try
                finally { _cacheLock.Release(); }
            }

            public async Task<bool> CancelAlgoOrderAsync(long algoId, CancellationToken ct)
            {
                if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret)) return false;

                var ts = await GetBinanceTimestampAsync(ct);
                var q = new List<KeyValuePair<string, string>>
                {
                    new("algoId",     algoId.ToString(CultureInfo.InvariantCulture)),
                    new("timestamp",  ts.ToString(CultureInfo.InvariantCulture)),
                    new("recvWindow", "5000"),
                };
                var (query, rawQuery) = BuildQuery(q);
                var sig = Sign(rawQuery, _apiSecret);
                var url = $"{_baseUrl}/fapi/v1/algoOrder?{query}&signature={sig}";

                using var req = new HttpRequestMessage(HttpMethod.Delete, url);
                req.Headers.TryAddWithoutValidation("X-MBX-APIKEY", _apiKey);

                try
                {
                    using var resp = await _http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync(ct);
                        _logger.LogWarning("[ALGO-RAW] CancelAlgoOrder HTTP {code} body={body}", (int)resp.StatusCode, body);
                        return false;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ALGO-RAW] EX CancelAlgoOrderAsync algoId={id}", algoId);
                    return false;
                }
            }

            // =====================================================
            // BuildQuery: строим query string для URL (percent-encoded)
            // Sign: подписываем RAW строку ДО encoding — требование Binance с декабря 2025
            // =====================================================
            private static (string encoded, string raw) BuildQuery(IEnumerable<KeyValuePair<string, string>> q)
            {
                var encoded = new StringBuilder();
                var raw = new StringBuilder();

                foreach (var kv in q)
                {
                    if (encoded.Length > 0) { encoded.Append('&'); raw.Append('&'); }

                    // raw — без encoding, используется для подписи
                    raw.Append(kv.Key).Append('=').Append(kv.Value);

                    // encoded — для URL
                    encoded.Append(Uri.EscapeDataString(kv.Key))
                           .Append('=')
                           .Append(Uri.EscapeDataString(kv.Value));
                }

                return (encoded.ToString(), raw.ToString());
            }

            private static string Sign(string rawQueryString, string secret)
            {
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawQueryString));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }



        }

        private async Task TryHarvestProfitAsync(
            BinanceRestClient client,
            EngineState state,
            string symbol,
            PositionSide side,
            BinancePositionDetailsUsdt pos,
            IReadOnlyList<BinanceFuturesUsdtKline> klines,
            decimal aiEdgeScore,
            decimal minUsd,
            CancellationToken ct)
        {

            // Manual position guard: TryHarvestProfitAsync has no signal parameter.
            // The caller (HandleSideAsync) already skips harvest for manual positions
            // before calling this method, so no guard needed here.

            // ==========================================================
            // 🔒 BLOCK HARVEST right after EARLY-TP (Binance sync lag)
            // ==========================================================
            var harvestKey = $"{symbol}|{side}";

            if (_recentPartialClose.TryGetValue(harvestKey, out var ts))
            {
                var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ts;
                if (ageMs < 8000) // 8 seconds hard block
                {
                    _logger.LogInformation(
                        "[HARVEST][{symbol}][{side}] SKIP → recent EARLY-TP ({ms}ms)",
                        symbol, side, ageMs);
                    return;
                }

                _recentPartialClose.TryRemove(harvestKey, out _);
            }




            var sKey = EngineState.Key(symbol);
            var st = state.Symbols.GetOrAdd(sKey, _ => new SymbolState());

            // throttle
            if ((DateTime.UtcNow - st.LastHarvestUtc) < TimeSpan.FromMinutes(3))
                return;

            // ==========================================================
            // 🔒 ЖЁСТКАЯ ПРОВЕРКА ФАКТИЧЕСКОЙ ПОЗИЦИИ
            // ==========================================================
            var posInfo = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
            if (!posInfo.Success || posInfo.Data == null)
                return;

            var realPos = posInfo.Data.FirstOrDefault(p =>
                p.Symbol == symbol &&
                p.PositionSide == side &&
                Math.Abs(p.Quantity) > 0);

            if (realPos == null)
            {
                _logger.LogWarning("[HARVEST][{symbol}][{side}] SKIP → no open position", symbol, side);
                return;
            }

            decimal qty = Math.Abs(realPos.Quantity);
            if (qty <= 0) return;

            // ==========================================================
            // uPnL
            // ==========================================================
            decimal uPnl;
            try { uPnl = realPos.UnrealizedPnl; }
            catch { return; }

            // ==========================================================
            // dynamic minUsd (PRO, non-invasive)
            // ==========================================================

            // absolute floor (fees / noise protection)
            const decimal ABS_MIN_USD = 3.8m;

            // notional-based floor (≈0.12% position size)
            decimal notionalUsd = Math.Abs(realPos.EntryPrice * realPos.Quantity);
            decimal minByNotional = notionalUsd * 0.0012m;

            // ATR-based floor (will be clamped later)
            decimal atrTmp = _marketData.CalculateAtr(klines);
            if (atrTmp <= 0) atrTmp = 0.00000001m;
            decimal minByAtr = atrTmp * Math.Abs(realPos.Quantity) * 0.25m;

            // final minUsd (keep variable name, keep logic below)
            minUsd = Math.Max(
              ABS_MIN_USD,
              Math.Min(minByNotional, minByAtr)
          );

            if (symbol == "BTCUSDT")
                return;
            if (uPnl <= 0m || uPnl < minUsd)
                return;

            decimal atr = _marketData.CalculateAtr(klines);
            if (atr <= 0) atr = 0.00000001m;

            var last = klines[^1];
            var body = Math.Abs(last.ClosePrice - last.OpenPrice);

            // сильный импульс → не режем
            if (
                (_regimeNow == MarketRegime.StrongUpTrend ||
                 _regimeNow == MarketRegime.StrongDownTrend)
                && body > atr * 1.1m
            )
            {
                _logger.LogInformation(
                    "[HARVEST][{symbol}] SKIP → trend expansion",
                    symbol);
                return;
            }



            decimal rr = Math.Abs(realPos.MarkPrice - realPos.EntryPrice) / atr;

            decimal harvestPct =
                aiEdgeScore >= 0.80m && rr >= 1.4m ? 0.22m :
                aiEdgeScore >= 0.70m ? 0.28m :
                0.45m;

            //  decimal closeQty = Math.Round(qty * harvestPct, 8);
            // if (closeQty <= 0) return;

            // ==========================================================
            // 🔥 FULL vs PARTIAL CLOSE LOGIC (КЛЮЧЕВО)
            // ==========================================================

            decimal closeQty = qty * harvestPct;

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            var step = filters.step > 0 ? filters.step : 1m;

            // ❗ КЛЮЧЕВО: всегда вниз
            closeQty = Math.Floor(closeQty / step) * step;

            if (closeQty < filters.minQty)
            {
                _logger.LogInformation(
                    "[HARVEST][{symbol}][{side}] SKIP → rounded closeQty {q} < minQty {min}",
                    symbol, side, closeQty, filters.minQty);
                return;
            }


            bool isFullClose = closeQty >= qty;

            var closeSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
     symbol: symbol,
     side: closeSide,
     type: FuturesOrderType.Market,
     quantity: closeQty,
     positionSide: side,
     selfTradePreventionMode: SelfTradePreventionMode.ExpireMaker,
     ct: ct);


            if (!res.Success)
            {
                _logger.LogWarning("[HARVEST][{symbol}][{side}] FAIL: {err}", symbol, side, res.Error);
                return;
            }

            decimal addToBucket = uPnl * harvestPct;
            st.RealizedPnlBucketUsd += Math.Max(0m, addToBucket);
            st.LastHarvestUtc = DateTime.UtcNow;
            st.HarvestsToday++;

            _logger.LogInformation(
                "[HARVEST][{symbol}][{side}] OK closeQty={q} uPnl={pnl:F2} addBucket={b:F2} edge={e:F2} rr={rr:F2}",
                symbol, side, closeQty, uPnl, addToBucket, aiEdgeScore, rr);

            _recentPartialClose[$"{symbol}|{side}"] =
    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        }

    }

}




