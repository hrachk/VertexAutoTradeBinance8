// ============================================================================
// - Если ордер Filled/позиция появилась → возвращаем SUCCESS,
//   а SL/TP СТАВИТ PositionSupervisorService v8.1 (NORMAL → ALGO RAW)
// - OrderExecutor НЕ ставит SL/TP вообще.

// ============================================================================

using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services.Interface;

namespace VertexAutoTradeBinance8.Services
{
    public class OrderExecutor
    {
        private readonly ILogger<OrderExecutor> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly SymbolInfoService _symbolInfo;
        private readonly SimulatedTradeService _simulator;
        private readonly ExecutedSignalService _executedSignalService;
        private readonly MarketDataService _marketData;
        private readonly AiMarketRegimeService _marketRegimeService;
        private readonly SmartRegimeService _smartRegime;
        private readonly LiquidityGuardService _liquidityGuard;
        private readonly AiSelfLearningService _ai;
        private readonly RiskManager _risk;
        private readonly MarketDataFacade _marketDataFacade;
        private readonly AiLiquidityClusterService _liquidityClusterService;
        private readonly MarketDataPushClient? _pushClient;


        // ===== ENTRY EXECUTION TUNING (PRODUCTION) =====

        private const decimal AGGR_LIMIT_OFFSET_PCT = 0.0006m;  // 0.06% агрессивный лимит (тюнится)
        private const decimal MARKET_FALLBACK_MAX_SLIP_PCT = 0.0015m; // 0.15% макс. слип для fallback-market
        private bool? _isHedgeMode;
        private const int MAX_ENTRIES_PER_SYMBOL = 3;   // макс входов пока позиция открыта (initial + 1 DCA)
        private const decimal MAX_LIMIT_STALE_DRIFT = 0.0047m; // 0.45%
        private readonly IOptionsMonitor<TradingSettings> _tradingSettings;
        private readonly IOptionsMonitor<TradingOptions> _tradingOptions;
        private readonly IAccountStateService _accountState;
        private readonly BinanceAlgoOrderService _algoOrders;

        // =====================================================
        // Максимум открытых позиций глобально (по всем символам)
        // 4 позиции = до 4 разных символов, каждый может иметь
        // Long + Short одновременно (хедж)
        // =====================================================
        private const int MAX_GLOBAL_POSITIONS = 11;

        // =====================================================
        // EntryTracker — двойной счётчик:
        //
        // _active  = входы пока позиция открыта (сбрасывается при закрытии)
        //            Лимит: MAX_ENTRIES_PER_SYMBOL = 2
        //            Цель: разрешить initial вход + 1 DCA на откате
        //
        // _session = все входы за сессию (НЕ сбрасывается до рестарта)
        //            Лимит: MAX_SESSION_ENTRIES = 4
        //            Цель: ограничить суммарные сделки включая хедж
        // =====================================================
        public class EntryTracker
        {
            // _active: входы пока позиция открыта, сбрасывается при закрытии
            // Контролирует DCA — не более 2 входов на одну сторону
            private readonly ConcurrentDictionary<string, int> _active = new();

            // _lastEntry: timestamp of last RegisterEntry per key.
            // Safety TTL: if a position was closed outside the bot (liquidation,
            // manual close via Binance app) and OnPositionClosed was never called,
            // the counter would remain permanently blocking new entries. The TTL
            // (4 hours) resets the counter automatically as a last resort.
            // 4h is long enough to cover any real multi-DCA position lifetime
            // but short enough to recover from a missed close event.
            private readonly ConcurrentDictionary<string, DateTime> _lastEntry = new();
            private static readonly TimeSpan EntryTtl = TimeSpan.FromHours(4);

            private string Key(string symbol, PositionSide side) => $"{symbol}_{side}";

            public int GetActiveEntries(string symbol, PositionSide side)
                => _active.TryGetValue(Key(symbol, side), out var v) ? v : 0;

            // Вызывается при каждом успешном входе
            public void RegisterEntry(string symbol, PositionSide side)
            {
                var key = Key(symbol, side);
                _active.AddOrUpdate(key, 1, (_, v) => v + 1);
                _lastEntry[key] = DateTime.UtcNow;
            }

            // Вызывается при полном закрытии позиции — сбрасывает счётчик
            // После закрытия бот может снова войти (глобальный cap контролирует MAX_GLOBAL_POSITIONS=4)
            public void OnPositionClosed(string symbol, PositionSide side)
            {
                var key = Key(symbol, side);
                _active.TryRemove(key, out _);
                _lastEntry.TryRemove(key, out _);
            }

            // Проверяет лимит активных входов на одну сторону.
            // Safety TTL: if the entry counter is stale (position closed outside
            // the bot — liquidation, manual Binance app close — and OnPositionClosed
            // was never called), auto-reset after EntryTtl (4h) so the bot
            // can trade the symbol again without needing a restart.
            public bool CanEnter(string symbol, PositionSide side, out string reason)
            {
                var key = Key(symbol, side);

                // TTL check — reset stale counter
                if (_lastEntry.TryGetValue(key, out var lastTs) &&
                    DateTime.UtcNow - lastTs > EntryTtl)
                {
                    _active.TryRemove(key, out _);
                    _lastEntry.TryRemove(key, out _);
                }

                int active = GetActiveEntries(symbol, side);

                if (active >= MAX_ENTRIES_PER_SYMBOL)
                {
                    reason = $"MAX_ACTIVE_ENTRIES ({active}/{MAX_ENTRIES_PER_SYMBOL})";
                    return false;
                }

                reason = string.Empty;
                return true;
            }

            // Обратная совместимость
            public int GetEntries(string symbol, PositionSide side)
                => GetActiveEntries(symbol, side);

            public void Reset(string symbol, PositionSide side)
                => OnPositionClosed(symbol, side);

            public bool TryReserveSlot(string symbol, PositionSide side, int max)
                => CanEnter(symbol, side, out _);

            /// <summary>
            /// Belt-and-suspenders: reconcile active-entry counters against a
            /// live snapshot of real open positions. Any symbol+side that has a
            /// counter but NO real position on exchange is forcibly reset.
            /// Called by PositionSupervisorService on each monitoring cycle so
            /// counters can never get permanently stuck even if OnPositionClosed
            /// is missed (e.g. liquidation handled entirely by the exchange).
            /// </summary>
            public void ReconcileWithRealPositions(IReadOnlyList<string> openKeys)
            {
                // openKeys format: "SOLUSDT_Long", "BTCUSDT_Short" — same as our Key()
                var openSet = new HashSet<string>(openKeys, StringComparer.OrdinalIgnoreCase);
                foreach (var key in _active.Keys.ToList())
                {
                    if (!openSet.Contains(key))
                    {
                        _active.TryRemove(key, out _);
                        _lastEntry.TryRemove(key, out _);
                    }
                }
            }
        }

        public class CooldownGuard
        {
            private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();

            public bool IsActive(string key)
            {
                if (_cooldowns.TryGetValue(key, out var ts))
                    return DateTime.UtcNow < ts;

                return false;
            }

            public void Activate(string key, TimeSpan duration)
            {
                _cooldowns[key] = DateTime.UtcNow.Add(duration);
            }
        }
        private readonly EntryTracker _entryTracker;
        private readonly CooldownGuard _cooldown;

        public OrderExecutor(
            ILogger<OrderExecutor> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            SimulatedTradeService simulator,
            ExecutedSignalService executedSignalService,
            MarketDataService marketData,
            AiMarketRegimeService marketRegimeService,
            SmartRegimeService smartRegime,
            LiquidityGuardService liquidityGuard, AiSelfLearningService ai, RiskManager risk,
            MarketDataFacade marketDataFacade,
            IOptionsMonitor<TradingSettings> tradingSettings, 
            AiLiquidityClusterService liquidityClusterService,
            EntryTracker entryTracker,
            CooldownGuard cooldown,
            IOptionsMonitor<TradingOptions> tradingOptions,
            IAccountStateService accountState,
            BinanceAlgoOrderService algoOrders,
            MarketDataPushClient? pushClient = null) // optional — null OK if not registered
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
            _simulator = simulator;
            _executedSignalService = executedSignalService;
            _marketData = marketData;
            _marketRegimeService = marketRegimeService;
            _smartRegime = smartRegime;
            _liquidityGuard = liquidityGuard;
            _ai = ai;
            _risk = risk;
            _marketDataFacade = marketDataFacade;
            _tradingSettings = tradingSettings;
            _liquidityClusterService = liquidityClusterService;
            _entryTracker = entryTracker;
            _cooldown = cooldown;
            _tradingOptions = tradingOptions;
            _accountState = accountState;
            _algoOrders  = algoOrders;
            _pushClient  = pushClient;
        }

        /// <summary>
        /// Smart 1M micro-timing confirmation.
        ///
        /// Replaces the old "last.Close > prev.High" stub (which blocked ~80% of
        /// valid entries) with a multi-condition system that scores the quality of
        /// the 1-minute setup independently of whether the price already broke out.
        ///
        /// Scoring model (0–100 points, pass threshold adaptive by regime):
        ///
        ///   1. TREND ALIGNMENT (35 pts)
        ///      The most recent 1M bar must be aligned with the signal direction.
        ///      BUY  → last1m is bullish (close > open).
        ///      SELL → last1m is bearish (close < open).
        ///      Partial credit (17 pts) when the prior bar is also aligned.
        ///
        ///   2. MOMENTUM (25 pts)
        ///      EMA9(1m) slope over the last 5 bars.
        ///      Positive slope for BUY, negative for SELL.
        ///      Proportional credit (0-25) based on slope magnitude vs ATR.
        ///
        ///   3. BODY QUALITY (20 pts)
        ///      Last 1M candle body ≥ 40% of its range (not a doji/wick mess).
        ///      Confirms conviction, not noise.
        ///
        ///   4. PRESSURE (20 pts)
        ///      Taker-buy/sell pressure over last 5 bars.
        ///      BUY wants buy pressure > sell pressure (pressure > 0).
        ///      SELL wants sell pressure > buy pressure (pressure < 0).
        ///      Proportional credit (0-20) based on |pressure|.
        ///
        /// Adaptive pass threshold:
        ///   - StrongTrend / Impulse: 30 pts  — allow slightly looser timing,
        ///     price won't wait for perfect alignment in a fast move.
        ///   - Trend (normal):        45 pts  — standard quality bar.
        ///   - Range / Squeeze:       55 pts  — stricter, mean-rev entries need
        ///     clear local turn before committing.
        ///   - Unknown / Chop:        65 pts  — near-perfect timing required,
        ///     very high noise in undefined regime.
        ///
        /// Also checks one hard-block regardless of score:
        ///   STALE SIGNAL — if the last 1M bar opened more than 3 bars after
        ///   the signal was generated, the market has moved on; skip this entry.
        /// </summary>
        public async Task<bool> ConfirmEntryOn1m(
            string symbol,
            SignalSide side,
            CancellationToken ct,
            TradeSignal? signal = null,
            SmartRegimeInfo? regime = null)
        {
            const int BARS = 20;
            var k1m = await _marketDataFacade.GetKlinesAsync(symbol, KlineInterval.OneMinute, BARS, ct);
            if (k1m == null || k1m.Count < 6)
                return true; // not enough data — don't block

            var last = k1m[^1];
            var prev = k1m[^2];
            bool isBuy = side == SignalSide.Buy;

            // ── STALE SIGNAL GUARD ────────────────────────────────────────────
            // If signal timestamp provided, ensure we're not entering an
            // entry that the market has already moved past.
            if (signal?.Time is DateTime sigTime && sigTime != default)
            {
                var staleBars = (int)Math.Round((last.OpenTime - sigTime).TotalMinutes);
                if (staleBars > 3)
                {
                    _logger.LogInformation(
                        "[1M][{symbol}] STALE: signal {sigTime:HH:mm} is {n} 1M bars old",
                        symbol, sigTime, staleBars);
                    return false;
                }
            }

            // ── ADAPTIVE PASS THRESHOLD ───────────────────────────────────────
            bool isStrongTrend =
                regime?.SmartType is SmartRegimeType.SmartStrongTrend ||
                regime?.BaseRegime is MarketRegime.StrongUpTrend or MarketRegime.StrongDownTrend;
            bool isRange =
                regime?.SmartType is SmartRegimeType.SmartRange or SmartRegimeType.SmartSqueeze ||
                regime?.BaseRegime is MarketRegime.Range;
            bool isChop =
                regime?.SmartType is SmartRegimeType.SmartChop or SmartRegimeType.SmartExhaustion;
            bool hasImpulseFlag = signal?.Reason is
                "IMPULSE_CONTINUATION" or "EARLY_TREND_JOIN" or
                "VOLATILITY_EXPANSION_BREAKOUT_LONG_V2" or "VOLATILITY_EXPANSION_BREAKOUT_SHORT_V2";

            int passThreshold =
                (isStrongTrend || hasImpulseFlag) ? 30 :
                isChop   ? 65 :
                isRange  ? 55 :
                           45;  // normal Trend

            int score = 0;

            // ── 1. TREND ALIGNMENT (35 pts) ────────────────────────────────────
            bool lastAligned = isBuy
                ? last.ClosePrice > last.OpenPrice
                : last.ClosePrice < last.OpenPrice;
            bool prevAligned = isBuy
                ? prev.ClosePrice > prev.OpenPrice
                : prev.ClosePrice < prev.OpenPrice;

            if (lastAligned && prevAligned)
                score += 35; // both bars aligned = strong
            else if (lastAligned)
                score += 25; // last bar aligned = good enough
            else if (prevAligned)
                score += 10; // only prior aligned = weak, partial credit

            // ── 2. EMA9 MOMENTUM (25 pts) ─────────────────────────────────────
            // Compute EMA9 over 1M closes and check slope direction + magnitude
            if (k1m.Count >= 10)
            {
                var closes1m = k1m.Select(c => c.ClosePrice).ToArray();
                var ema9     = ComputeEma(closes1m, 9);
                decimal slopeRaw = ema9.Length >= 6 ? ema9[^1] - ema9[^6] : 0m;
                decimal atrRef   = signal?.Atr ?? (last.HighPrice - last.LowPrice);
                if (atrRef <= 0) atrRef = last.ClosePrice * 0.001m;
                // Normalise slope by ATR so it's comparable across price levels
                decimal slopeNorm = atrRef > 0 ? slopeRaw / atrRef : 0m;

                bool slopeOk = isBuy ? slopeRaw > 0 : slopeRaw < 0;
                if (slopeOk)
                {
                    // Credit proportional to slope strength: capped at 1.0×ATR = full 25pts
                    decimal strength = Math.Clamp(Math.Abs(slopeNorm), 0m, 1m);
                    score += (int)(25 * strength);
                    if (score < (int)(25 * 0.2m) && slopeOk) // at least partial credit
                        score += 5;
                }
            }

            // ── 3. BODY QUALITY (20 pts) ──────────────────────────────────────
            decimal lastRange = last.HighPrice - last.LowPrice;
            decimal lastBody  = Math.Abs(last.ClosePrice - last.OpenPrice);
            bool solidBody    = lastRange > 0 && (lastBody / lastRange) >= 0.40m;
            if (solidBody) score += 20;
            else if (lastRange > 0 && (lastBody / lastRange) >= 0.25m)
                score += 10; // partial body

            // ── 4. TAKER PRESSURE (20 pts) ────────────────────────────────────
            int pressureBars = Math.Min(5, k1m.Count);
            decimal buyVol   = 0m, totalVol = 0m;
            for (int i = k1m.Count - pressureBars; i < k1m.Count; i++)
            {
                buyVol   += k1m[i].TakerBuyBaseVolume;
                totalVol += k1m[i].Volume;
            }
            decimal pressure = totalVol > 0 ? (buyVol - (totalVol - buyVol)) / totalVol : 0m;
            bool pressureOk  = isBuy ? pressure > 0.05m : pressure < -0.05m;
            if (pressureOk)
            {
                decimal pStrength = Math.Clamp(Math.Abs(pressure) * 3m, 0m, 1m); // scale 0.05→1 maps to 0→20
                score += (int)(20 * pStrength);
            }

            _logger.LogInformation(
                "[1M][{symbol}] {side} score={score}/{threshold} | aligned={a} ema={e} body={b} pressure={p:F2}",
                symbol, side, score, passThreshold,
                lastAligned, score >= 25, solidBody, pressure);

            return score >= passThreshold;
        }

        /// <summary>Exponential moving average helper for 1M timing.</summary>
        private static decimal[] ComputeEma(decimal[] values, int period)
        {
            if (values.Length == 0 || period <= 0)
                return Array.Empty<decimal>();
            var result = new decimal[values.Length];
            decimal k = 2m / (period + 1m);
            result[0] = values[0];
            for (int i = 1; i < values.Length; i++)
                result[i] = values[i] * k + result[i - 1] * (1 - k);
            return result;
        }

        private async Task<bool> IsHedgeModeAsync(BinanceRestClient client, CancellationToken ct)
        {
            if (_isHedgeMode.HasValue)
                return _isHedgeMode.Value;

            try
            {
                var res = await client.UsdFuturesApi.Account.GetPositionModeAsync(ct: ct);
                if (res.Success)
                {
                    _isHedgeMode = res.Data.IsHedgeMode; // true = Hedge (dual-side)
                    return res.Data.IsHedgeMode;
                }
            }
            catch { }

            // FAIL-SAFE: assume One-Way (safer)
            _isHedgeMode = false;
            return false;
        }
        private static int DecimalsFromStep(decimal step)
        {
            var s = step.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var i = s.IndexOf('.');
            return i < 0 ? 0 : s.Length - i - 1;
        }
        private static decimal Quantize(decimal value, decimal step)
        {
            if (step <= 0) return value;

            var decimals = DecimalsFromStep(step);
            var rounded = Math.Floor(value / step) * step;

            // 🔒 КЛЮЧ: финальное округление ПО КОЛ-ВУ ЗНАКОВ
            return Math.Round(rounded, decimals, MidpointRounding.ToZero);
        }
        // =====================================================================
        // MAIN ENTRY
        // =====================================================================
        public async Task<OrderResult> ExecuteAsync(
       TradeSignal signal,
       decimal quantity,
       CancellationToken ct = default, decimal leverage = 1m)
        {
            using var client = _factory.CreateRestClient();

            // =============================================================
            // v9: SYNC LEVERAGE WITH THE EXCHANGE BEFORE ANY MARGIN MATH
            // =============================================================
            // Previously, `leverage` here came straight from appsettings
            // (Trading.Leverage / Trading:BTC.Leverage etc.) and was used
            // for all the notional/margin calculations below, but the
            // exchange's ACTUAL per-symbol max leverage was never queried
            // and ChangeInitialLeverageAsync was never called — so the
            // account's leverage for that symbol stayed whatever it was
            // last set to manually (or Binance's default), regardless of
            // what the config assumed. For symbols with a low exchange cap
            // (e.g. NVDAUSDT tops out at 10x while Trading.Leverage=19),
            // this could silently desync the bot's margin math from what
            // the exchange would actually do with the order.
            //
            // Fix: query the real bracket-based max leverage for this
            // symbol, clamp our configured leverage to it, then explicitly
            // push that leverage to the exchange via ChangeInitialLeverageAsync
            // so the account state matches the math below exactly.
            try
            {
                var brackets = await client.UsdFuturesApi.Account.GetBracketsAsync(signal.Symbol, ct: ct);
                if (brackets.Success && brackets.Data != null)
                {
                    var symBracket = brackets.Data.FirstOrDefault(b => b.Symbol == signal.Symbol);
                    var exchangeMaxLeverage = symBracket?.Brackets?.Length > 0
                        ? symBracket.Brackets.Max(b => b.InitialLeverage)
                        : (int?)null;

                    if (exchangeMaxLeverage.HasValue && leverage > exchangeMaxLeverage.Value)
                    {
                        _logger.LogWarning(
                            "[LEVERAGE][{symbol}] configured leverage {configured}x exceeds exchange max {max}x — clamping",
                            signal.Symbol, leverage, exchangeMaxLeverage.Value);
                        leverage = exchangeMaxLeverage.Value;
                    }
                }

                // Read the CURRENT leverage the user has set on the exchange.
                // If the user manually set a HIGHER leverage than what we
                // calculated — do NOT downgrade it. We only enforce our
                // value if it is HIGHER than what the exchange currently has
                // (i.e. we need to bring it DOWN for safety, or UP to match config).
                // This prevents the bot from overriding a user-set 40x with 10x.
                int leverageToSet = (int)leverage;
                try
                {
                    var posInfoForLev = await client.UsdFuturesApi.Account
                        .GetPositionInformationAsync(symbol: signal.Symbol, ct: ct);
                    if (posInfoForLev.Success && posInfoForLev.Data != null)
                    {
                        var posEntry = posInfoForLev.Data.FirstOrDefault();
                        int currentExchangeLev = posEntry?.Leverage ?? 0;
                        if (currentExchangeLev > leverageToSet)
                        {
                            _logger.LogInformation(
                                "[LEVERAGE][{symbol}] Exchange leverage {cur}x > our calc {calc}x — preserving user setting",
                                signal.Symbol, currentExchangeLev, leverageToSet);
                            leverageToSet = currentExchangeLev; // keep user's value
                        }
                    }
                }
                catch { /* non-critical — proceed with our calculated leverage */ }

                var setLevResult = await client.UsdFuturesApi.Account.ChangeInitialLeverageAsync(
                    signal.Symbol, leverageToSet, ct: ct);
                leverage = leverageToSet; // update for downstream margin math

                if (!setLevResult.Success)
                {
                    _logger.LogWarning(
                        "[LEVERAGE][{symbol}] ChangeInitialLeverageAsync failed: {err} — proceeding with account's current leverage setting",
                        signal.Symbol, setLevResult.Error?.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[LEVERAGE][{symbol}] leverage sync failed — proceeding with config value {leverage}x, account setting may be stale",
                    signal.Symbol, leverage);
            }

            bool isToxicSymbol =
                _tradingSettings.CurrentValue.ToxicSymbols
                .Contains(signal.Symbol);

            var isHedge = await IsHedgeModeAsync(client, ct);

            _logger.LogInformation("[ACCOUNT] Futures position mode: {mode}", isHedge ? "HEDGE" : "ONE-WAY");

            var side = signal.Side == SignalSide.Buy ? OrderSide.Buy : OrderSide.Sell;
            var posSide = signal.Side == SignalSide.Buy ? PositionSide.Long : PositionSide.Short;

            var key = $"{signal.Symbol}_{posSide}";

            // =============================================================
            // ENTRY CONTROL (NEW — STRICT)
            // =============================================================

            if (_cooldown.IsActive(key))
            {
                _logger.LogWarning(
                    "[ENTRY BLOCKED][{symbol}] cooldown active",
                    signal.Symbol);

                return OrderResult.Fail("COOLDOWN_ACTIVE");
            }

            // =============================================================
            // GLOBAL POSITION CAP — не более MAX_GLOBAL_POSITIONS открытых
            // позиций одновременно по всем символам
            // =============================================================
            int openPositionsCount = _accountState.GetPositions().Count;
            if (openPositionsCount >= MAX_GLOBAL_POSITIONS)
            {
                _logger.LogWarning(
                    "[ENTRY BLOCKED][{symbol}] global position cap reached ({count}/{max})",
                    signal.Symbol,
                    openPositionsCount,
                    MAX_GLOBAL_POSITIONS);

                return OrderResult.Fail("GLOBAL_POSITION_CAP");
            }

            // =====================================================
            // ENTRY LIMITS:
            // Active: max 2 входа пока позиция открыта (initial + 1 DCA)
            // Session: max 4 входа за сессию на эту сторону
            // =====================================================
            if (!_entryTracker.CanEnter(signal.Symbol, posSide, out var entryBlockReason))
            {
                _logger.LogWarning(
                    "[ENTRY BLOCKED][{symbol}] {reason}",
                    signal.Symbol,
                    entryBlockReason);

                return OrderResult.Fail(entryBlockReason);
            }

            // =============================================================
            // POSITION SNAPSHOT (только для averaging, НЕ для entry count)
            // =============================================================

            BinancePositionDetailsUsdt? sameSidePosition = null;

            var posCheck = await client.UsdFuturesApi.Account
                .GetPositionInformationAsync(signal.Symbol, ct: ct);

            if (posCheck.Success && posCheck.Data != null)
            {
                sameSidePosition = posCheck.Data.FirstOrDefault(p =>
                    Math.Abs(p.Quantity) > 0.00000001m &&
                    (
                        (signal.Side == SignalSide.Buy &&
                            (p.PositionSide == PositionSide.Long || p.PositionSide == PositionSide.Both)) ||

                        (signal.Side == SignalSide.Sell &&
                            (p.PositionSide == PositionSide.Short || p.PositionSide == PositionSide.Both))
                    ));
            }

            // =============================================================
            // TRAILING DCA (Binance Grid Bot логика)
            // Разрешаем DCA только если цена откатила >= ATR×0.8 от avgEntry
            // Блокируем если цена ушла > ATR×2.0 (ловля ножа)
            // =============================================================
            decimal lastPrice = _marketDataFacade.GetLastPrice(signal.Symbol);
            var filters = await _symbolInfo.GetFuturesFiltersAsync(signal.Symbol);
            var tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            int existingEntries = _entryTracker.GetActiveEntries(signal.Symbol, posSide);

            if (existingEntries == 1 && sameSidePosition != null)
            {
                decimal avgEntry = sameSidePosition.EntryPrice;
                decimal atr      = signal.Atr ?? lastPrice * 0.001m;
                bool    isLong   = signal.Side == SignalSide.Buy;

                decimal minRetrace = atr * 0.8m;
                decimal maxRetrace = atr * 2.0m;

                bool retracedEnough =
                    isLong  ? lastPrice <= avgEntry - minRetrace
                            : lastPrice >= avgEntry + minRetrace;

                bool catchingKnife =
                    isLong  ? lastPrice <= avgEntry - maxRetrace
                            : lastPrice >= avgEntry + maxRetrace;

                if (catchingKnife)
                {
                    _logger.LogWarning(
                        "[DCA BLOCKED][{symbol}] catching knife — avg={avg:F4} last={last:F4} max={max:F4}",
                        signal.Symbol, avgEntry, lastPrice,
                        isLong ? avgEntry - maxRetrace : avgEntry + maxRetrace);
                    return OrderResult.Fail("DCA_CATCHING_KNIFE");
                }

                if (!retracedEnough)
                {
                    _logger.LogInformation(
                        "[DCA BLOCKED][{symbol}] insufficient retrace — avg={avg:F4} last={last:F4} need={need:F4}",
                        signal.Symbol, avgEntry, lastPrice,
                        isLong ? avgEntry - minRetrace : avgEntry + minRetrace);
                    return OrderResult.Fail("DCA_INSUFFICIENT_RETRACE");
                }

                _logger.LogInformation(
                    "[DCA OK][{symbol}] retrace={ret:F2}×ATR avg={avg:F4} last={last:F4}",
                    signal.Symbol, Math.Abs(lastPrice - avgEntry) / atr, avgEntry, lastPrice);
            }

            // =============================================================
            // SYMBOL FILTERS
            // =============================================================

            var step = filters.step <= 0 ? 0.0001m : filters.step;
            var minQty = filters.minQty;
            var minNotional = filters.minNotional;

            quantity = Math.Floor(quantity / step) * step;
            quantity = Math.Max(quantity, minQty);

            decimal notional = quantity * signal.EntryPrice;

            // =============================================================
            // VALIDATIONS
            // =============================================================

            if (quantity < minQty || notional < minNotional)
            {
                decimal requiredNotional = Math.Max(minNotional, minQty * signal.EntryPrice);

                if (_risk.LastBalanceUsdt * leverage >= requiredNotional)
                {
                    quantity = Math.Max(quantity, minQty);

                    // 🔥 ДОБАВЬ ЭТО
                    if (quantity * signal.EntryPrice < minNotional)
                    {
                        quantity = Math.Ceiling(minNotional / signal.EntryPrice / step) * step;
                    }

                    notional = quantity * signal.EntryPrice;

                    if (notional < minNotional)
                    {
                        var reason = $"QTY_TOO_SMALL_AFTER_BUMP | qty={quantity} notional={notional} minNotional={minNotional}";
                        await _simulator.AppendLifecycleEventAsync(
                            signal,
                            stage: "PREFILTER_REJECT",
                            reason: reason,
                            attemptNotional: notional,
                            requiredMinNotional: minNotional);

                        return OrderResult.Fail(reason);
                    }

                    _logger.LogInformation(
                        "[EXEC][{symbol}] Quantity bumped up to minQty",
                        signal.Symbol);
                }
                else
                {
                    var reason = $"QTY_TOO_SMALL | qty={quantity} minQty={minQty} notional={notional} minNotional={minNotional}";
                    await _simulator.AppendLifecycleEventAsync(
                        signal,
                        stage: "PREFILTER_REJECT",
                        reason: reason,
                        attemptNotional: notional,
                        requiredMinNotional: minNotional);

                    return OrderResult.Fail(reason);
                }
            }

            if (notional < minNotional)
            {
                var reason = $"MIN_NOTIONAL | notional={notional} minNotional={minNotional}";

                await _simulator.AppendLifecycleEventAsync(
                    signal,
                    stage: "PREFILTER_REJECT",
                    reason: reason,
                    attemptNotional: notional,
                    requiredMinNotional: minNotional);

                return OrderResult.Fail(reason);
            }

            decimal entryPrice = Quantize(signal.EntryPrice, tick);


            // =============================================================
            // MULTI TF MARKET STRUCTURE
            // =============================================================

            // signal TF (1m)
            var signalKlines = await _marketDataFacade.GetKlinesAsync(
                signal.Symbol,
                KlineInterval.OneMinute,
                200);

            // trend TF (5m)
            var trendKlines = await _marketDataFacade.GetKlinesAsync(
                signal.Symbol,
                KlineInterval.FiveMinutes,
                200);

            // regime считается по 5m
            var baseReg = _marketRegimeService.DetectRegime(
                signal.Symbol,
                KlineInterval.FiveMinutes,
                trendKlines);

            // smart regime тоже по 5m
            var smart = _smartRegime.Evaluate(
                signal.Symbol,
                KlineInterval.FiveMinutes,
                trendKlines);

            // =============================================================
            // TOXIC SYMBOL GUARD (RIVER / similar manipulation assets)
            // =============================================================

            if (isToxicSymbol)
            {
                var last = signalKlines.Last();
                var prev = signalKlines[^2];

                decimal move =
     prev.ClosePrice > 0
         ? (last.ClosePrice - signalKlines[^3].ClosePrice) / signalKlines[^3].ClosePrice
         : 0m;

                bool bigDump = move <= -0.030m; // было -5%
                bool bigPump = move >= 0.035m;
                // 🚫 BLOCK SHORT IF 2 RED CANDLES IN A ROW (classic trap)
                if (signal.Side == SignalSide.Sell && signalKlines.Count >= 3)
                {
                    var c1 = signalKlines[^1];
                    var c2 = signalKlines[^2];

                    if (c1.ClosePrice < c1.OpenPrice &&
                        c2.ClosePrice < c2.OpenPrice)
                    {
                        _logger.LogWarning(
                            "[TOXIC BLOCK][{symbol}] double red short trap",
                            signal.Symbol);

                        return OrderResult.Fail("DOUBLE_RED_TRAP");
                    }
                }

                if (signal.Side == SignalSide.Buy && signalKlines.Count >= 3)
                {
                    var c1 = signalKlines[^1];
                    var c2 = signalKlines[^2];

                    if (c1.ClosePrice > c1.OpenPrice &&
                        c2.ClosePrice > c2.OpenPrice)
                    {
                        _logger.LogWarning(
                            "[TOXIC BLOCK][{symbol}] double green long trap",
                            signal.Symbol);

                        return OrderResult.Fail("DOUBLE_GREEN_TRAP");
                    }
                }
                // ------------------------------------------
                // SHORT TRAP PROTECTION
                // ------------------------------------------

                if (signal.Side == SignalSide.Sell && bigDump)
                {
                    _logger.LogWarning(
                        "[TOXIC BLOCK][{symbol}] short after dump trap",
                        signal.Symbol);

                    await _simulator.SimulateMissedTradeAsync(
                        signal,
                        "ToxicShortTrap",
                        note: $"move={move:P2}",
                        attemptNotional: quantity * signal.EntryPrice,
                        requiredMinNotional: 0);

                    return OrderResult.Fail("TOXIC_SHORT_TRAP");
                }

                // ------------------------------------------
                // LONG FOMO PROTECTION
                // ------------------------------------------

                if (signal.Side == SignalSide.Buy && bigPump)
                {
                    _logger.LogWarning(
                        "[TOXIC BLOCK][{symbol}] long after pump trap",
                        signal.Symbol);

                    await _simulator.SimulateMissedTradeAsync(
                        signal,
                        "ToxicLongTrap",
                        note: $"move={move:P2}",
                        attemptNotional: quantity * signal.EntryPrice,
                        requiredMinNotional: 0);

                    return OrderResult.Fail("TOXIC_LONG_TRAP");
                }

                // ------------------------------------------
                // leverage reduction
                // ------------------------------------------

                leverage = Math.Min(leverage, 2m);

                _logger.LogInformation(
                    "[TOXIC MODE][{symbol}] leverage limited to {lev}",
                    signal.Symbol,
                    leverage);
            }

            bool isSmartStrongTrend =
                smart.BaseRegime == MarketRegime.StrongUpTrend ||
                smart.BaseRegime == MarketRegime.StrongDownTrend;

            bool hasImpulse = IsImpulse(signalKlines, signal.Atr ?? 0m);

            // =====================================================
            // POST IMPULSE TRAPS (CRITICAL FIX)
            // =====================================================

            // SHORT TRAP
            bool postImpulseTrap =
                hasImpulse &&
                signal.Side == SignalSide.Sell &&
                signalKlines[^1].ClosePrice < signalKlines[^1].OpenPrice;

            if (postImpulseTrap)
            {
                _logger.LogWarning(
                    "[TRAP BLOCK][{symbol}] short after impulse sweep",
                    signal.Symbol);

                return OrderResult.Fail("POST_IMPULSE_TRAP");
            }

            var lastCandle = signalKlines[^1];

            decimal body = Math.Abs(lastCandle.ClosePrice - lastCandle.OpenPrice);
            decimal range = lastCandle.HighPrice - lastCandle.LowPrice;

            bool isStrongGreen = range > 0 && (body / range) > 0.7m;

            bool postImpulseLongTrap =
                hasImpulse &&
                signal.Side == SignalSide.Buy &&
                isStrongGreen;

            if (postImpulseLongTrap)
            {
                _logger.LogWarning(
                    "[TRAP BLOCK][{symbol}] long after impulse pump",
                    signal.Symbol);

                return OrderResult.Fail("POST_IMPULSE_LONG_TRAP");
            }

            // =====================================================
            // ADAPTIVE MICRO PULLBACK ENTRY (PRO VERSION)
            // =====================================================

            if ((signal.Atr ?? 0m) > 0 && entryPrice > 0)
            {
                // =====================================================
                // ADAPTIVE MICRO PULLBACK ENTRY (CALIBRATED)
                // =====================================================

                decimal atr = signal.Atr.Value;

                decimal microPullback;

                if (smart.EntryProfile == "CT")
                    microPullback = atr * 0.40m;
                else if (hasImpulse)
                    microPullback = atr * 0.23m;
                else if (isSmartStrongTrend)
                    microPullback = atr * 0.27m;
                else
                    microPullback = atr * 0.31m;

                // safety clamp
                decimal maxPullback = entryPrice * 0.0045m; // 0.45%
                microPullback = Math.Min(microPullback, maxPullback);

                decimal finalEntryPrice = entryPrice;

                if (signal.Side == SignalSide.Buy)
                    finalEntryPrice -= microPullback;
                else
                    finalEntryPrice += microPullback;

                finalEntryPrice = Quantize(finalEntryPrice, tick); // Quantize только один раз

                entryPrice = finalEntryPrice;

                // защита от отрицательных цен
                if (entryPrice <= 0)
                    entryPrice = signal.EntryPrice;

                // quantize к tick
                entryPrice = Quantize(entryPrice, tick);

                _logger.LogDebug(
                    "[PULLBACK][{symbol}] side={side} pullback={pb} ATR={atr}",
                    signal.Symbol,
                    signal.Side,
                    microPullback,
                    atr);
            }
            // =============================================================
            // LAST PRICE (SAFE, RATE-LIMIT AWARE)
            // =============================================================
          
            if (lastPrice <= 0)
            {
                lastPrice = entryPrice; // fallback safety
            }
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(800));

                var px = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(signal.Symbol, ct: cts.Token);
                if (px.Success && px.Data != null && px.Data.Price > 0)
                    lastPrice = px.Data.Price;
            }
            catch { /* fallback */ } 

          
            // =============================================================
            // LIQUIDITY GUARD (FAIL-SAFE)
            // =============================================================
            LiquidityGuardResult liquidityResult;
            // =============================================================
            // LIQUIDITY CLUSTER (EXECUTION-TIME REFINEMENT)
            // =============================================================
            try
            {
                var adjusted = await _liquidityClusterService
                    .FilterAndAdjustAsync(signal, ct);

                if (adjusted == null)
                {
                    _logger.LogWarning("[EXEC][{symbol}] cluster rejected at execution", signal.Symbol);
                    return OrderResult.Fail("LIQ_CLUSTER_EXEC_BLOCK");
                }

                signal.CopyFrom(adjusted);

            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[EXEC][{symbol}] cluster fail-safe pass", signal.Symbol);
            }

            try
            {
                liquidityResult =  _liquidityGuard.Analyze(
                    signal.Symbol,
                    KlineInterval.OneMinute,
                    signalKlines,
                    signal.Side,
                    superSignal: signal.IsSuperSignal);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LiquidityGuard] Analyze failed → FAIL-SAFE ALLOW ({symbol})", signal.Symbol);

                liquidityResult = new LiquidityGuardResult(
                    Block: false,
                    Reason: LiquidityGuardReason.None,
                    IsExtreme: false,
                    Details: "AnalyzeFailed",
                    UtcTime: DateTime.UtcNow);
            }

            bool liquiditySafe = !liquidityResult.Block;

            // =============================================================
            // RR CHECK (MAX TP)
            // =============================================================
            decimal rr = 0m;
            if (signal.StopLoss > 0 && signal.TakeProfits?.Any() == true)
            {
                var risk = Math.Abs(signal.EntryPrice - signal.StopLoss);
                var reward = signal.TakeProfits.Max(tp => Math.Abs(tp - signal.EntryPrice));
                if (risk > 0) rr = reward / risk;
            }

            decimal minRr =
                smart.EntryProfile == "CT" ? 1.2m :
                smart.EntryProfile == "EXP" ? 1.8m :
                1.5m;

            bool rrOk = rr >= minRr;


            // =============================================================
            // MARKET ENTRY PERMISSION (STRUCTURE-BASED, NO CONFIDENCE GATE)
            // =============================================================
            bool allowMarketEntry = false;

            if (smart.EntryProfile == "EXP")
            {
                allowMarketEntry =
                    isSmartStrongTrend &&
                    rrOk &&
                    liquiditySafe;
            }
            else if (smart.EntryProfile == "CT")
            {
                allowMarketEntry =
                    rrOk &&
                    liquiditySafe &&
                    !liquidityResult.IsExtreme;
            }
            else
            {
                allowMarketEntry =
                    isSmartStrongTrend &&
                    rrOk &&
                    liquiditySafe;
            }

           
            if (hasImpulse && !signal.IsSuperSignal)
            {
                _logger.LogWarning(
                  "[IMPULSE BLOCK][{symbol}] waiting pullback instead of chasing",
                  signal.Symbol);
                allowMarketEntry = false;
            }

            // =============================================================
            // APPLY SOFT LIQUIDITY SIZE MULTIPLIER (EARLY EXPANSION FIX)
            // =============================================================
            var sizeMul = signal.SizeMultiplier <= 0 ? 1.0m : signal.SizeMultiplier;

            // hard clamp — защита от глупостей
            sizeMul = Math.Clamp(sizeMul, 0.25m, 1.0m);

            // сначала multiply
            quantity *= sizeMul;

            // затем округление
            quantity = Math.Floor(quantity / step) * step;
            // затем проверка minQty / minNotional один раз
            quantity = Math.Max(quantity, minQty);
              notional = quantity * entryPrice;

            if (notional < minNotional)
            {
                quantity = Math.Max(quantity, minNotional / entryPrice);
                notional = quantity * entryPrice;
            }
            // =============================================================
            // EXECUTED SIGNAL CREATED
            // =============================================================
            decimal executionBias = Math.Clamp(smart.RiskBias * liquidityResult.Score, 0.4m, 1.0m);
            decimal adjustedQty = Math.Floor((quantity * executionBias) / step) * step;

            if (adjustedQty < filters.minQty)
            {
                decimal potentialNotional = filters.minQty * entryPrice;

                // Если qty чуть меньше minQty (>=85%), поднимаем до minQty
                if (adjustedQty >= filters.minQty * 0.85m)
                {
                    adjustedQty = filters.minQty;
                }
                // Иначе — если баланс позволяет, поднимаем для мелких альтов
                else if (_risk.LastBalanceUsdt * leverage >= potentialNotional)
                {
                    adjustedQty = filters.minQty;
                    _logger.LogInformation(
                        "[EXEC][{symbol}] AdjustedQty bumped up to minQty={minQty} due to small alt",
                        signal.Symbol, filters.minQty);
                }
                else
                {
                    // Всё равно не хватает → отказ
                    await _simulator.SimulateMissedTradeAsync(
                        signal,
                        "ADJUSTED_QTY_TOO_SMALL",
                        note: $"baseQty={quantity}; bias={executionBias:F2}; riskBias={smart.RiskBias:F2}; liqScore={liquidityResult.Score:F2}",
                        freeBalance: _risk.LastBalanceUsdt,
                        attemptNotional: quantity * entryPrice,
                        requiredMinNotional: filters.minQty * entryPrice);

                    return OrderResult.Fail("ADJUSTED_QTY_TOO_SMALL");
                }
            }

            quantity = adjustedQty;
            notional = quantity * entryPrice;

            var execRec = _executedSignalService.AddSignalCreated(
                signal,
                opportunityScore: (int)(smart.Confidence * 100), // оставил для UI/аналитики, но НЕ как гейт
                atr: signal.Atr ?? 0m,
                volatility: baseReg.VolatilityPercent,
                slope: baseReg.TrendSlopePercent,
                qty: quantity,
                notional: notional,
                tags: $"Regime={smart.BaseRegime}");

            var execTime = execRec.Time;
             
            // =============================================================
            // HARD BLOCK: LIQUIDITY
            // =============================================================
            if (liquidityResult.Block)
            {
                await _simulator.SimulateMissedTradeAsync(
                    signal,
                    $"LiquidityGuard:{liquidityResult.Reason}",
                    note: $"details={liquidityResult.Details}; extreme={liquidityResult.IsExtreme}; profile={smart.EntryProfile}; liqScore={liquidityResult.Score:F2}",
                    attemptNotional: notional,
                    requiredMinNotional: filters.minQty * entryPrice);

                return OrderResult.Fail($"LiquidityGuard:{liquidityResult.Reason}");
            }
            // =====================================================
            // FINAL MICRO TIMING (1M CONFIRMATION)
            // =====================================================
            if (!await ConfirmEntryOn1m(signal.Symbol, signal.Side, ct, signal, smart))
            {
                _logger.LogInformation(
                    "[1M BLOCK][{symbol}] bad micro timing",
                    signal.Symbol);

                return OrderResult.Fail("BAD_1M_TIMING");
            }

            // =============================================================
            // AI SELF-LEARNING GUARD (PnL-based kill-switch)
            // =============================================================
            var recentPnl = _ai.GetRecentPnL(signal.Symbol, signal.Side, 10);

            if (recentPnl < -0.03m)
            {
                _logger.LogWarning(
                    "[AI BLOCK][{symbol}] Negative recent PnL {pnl}",
                    signal.Symbol,
                    recentPnl);

                await _simulator.SimulateMissedTradeAsync(
                    signal,
                    "AI_PNL_BLOCK",
                    note: $"recentPnL={recentPnl:P2}",
                    attemptNotional: quantity * entryPrice,
                    requiredMinNotional: 0m);

                return OrderResult.Fail("AI_PNL_BLOCK");
            }
            // =============================================================
            // ENTRY ORDER — BINANCE 2026 (STRUCTURE + NO-CHASE LIMIT + CONTROLLED JOIN)
            // =============================================================
            decimal priceDriftPct =
                entryPrice > 0 ? Math.Abs(lastPrice - entryPrice) / entryPrice : 0m;

            // A) MARKET only for: breakout / tight reclaim / emergency super-signal
            bool breakoutMarket =
                allowMarketEntry &&
                hasImpulse &&
                isSmartStrongTrend &&
                !liquidityResult.IsExtreme;

            bool reclaimMarket =
                allowMarketEntry &&
                !hasImpulse &&
                smart.EntryProfile == "CT" &&
                !liquidityResult.IsExtreme &&
                priceDriftPct <= 0.0010m; // tight reclaim only

            bool emergencyMarket =
                allowMarketEntry &&
                signal.IsSuperSignal &&
                !liquidityResult.IsExtreme &&
                priceDriftPct <= 0.0025m; // emergency only when not runaway

            bool useMarket = breakoutMarket || reclaimMarket || emergencyMarket;

            // =====================================================
            // СКАЛЬПИНГ: 1M/5M — всегда Market
            // Limit на быстрых таймфреймах не успевает заполниться
            // =====================================================
            var signalTf = signal.Timeframe ?? "";
            if (signalTf is "OneMinute" or "FiveMinutes" or "1m" or "5m")
                useMarket = true;

            // Momentum паттерны → Market (уже на движении)
            if (signal.Reason is "IMPULSE_CONTINUATION" or "EARLY_TREND_JOIN")
                useMarket = true;

            // Range паттерны → Limit (цена у границы канала)
            if (signal.Reason is "RANGE_BOUND_LONG" or "RANGE_BOUND_SHORT")
                useMarket = false;

            // ── PHASE 2: Pullback signals → Limit Order ──────────────
            // Market orders at c0.Close buy the TOP of the rejection
            // candle. Limit order at EntryPrice (= c0.Close) with the
            // aggressive limit logic below gives us fill at or BELOW
            // close — a better entry that improves RR by 0.3-0.5 points.
            // The existing aggrLimitPrice calc already handles bid/ask
            // offset for Limit, and there's a Market fallback at L1700
            // if the Limit doesn't fill within 10s.
            if (signal.Reason != null && signal.Reason.Contains("PULLBACK_EMA21"))
                useMarket = false;

            // MeanReversion → also Limit (entering at extremes, no chase)
            if (signal.Reason != null && signal.Reason.Contains("MEANREV"))
                useMarket = false;

            FuturesOrderType entryType = useMarket ? FuturesOrderType.Market : FuturesOrderType.Limit;

            // =====================================================
            // DYNAMIC SPREAD GUARD (pro bots protection)
            // =====================================================

            decimal spreadPct = 0m;
            bool imbalanceBlock = false;
            try
            {
                var book = await client.UsdFuturesApi.ExchangeData.GetOrderBookAsync(signal.Symbol, 10, ct);

                if (book.Success &&
                    book.Data != null &&
                    book.Data.Bids.Any() &&
                    book.Data.Asks.Any())
                {
                    var bestBid = book.Data.Bids.First().Price;
                    var bestAsk = book.Data.Asks.First().Price;

                    //if (bestBid > 0 && bestAsk > 0)
                    //    spreadPct = (bestAsk - bestBid) / bestBid;
                    if (bestBid > 0 && bestAsk > 0)
                    {
                        var mid = (bestBid + bestAsk) / 2m;
                        spreadPct = mid > 0 ? (bestAsk - bestBid) / mid : 0m;
                    }
                    // =====================================================
                    // ORDER BOOK IMBALANCE FILTER
                    // =====================================================

                    decimal bidVol = book.Data.Bids.Sum(b => b.Quantity);
                    decimal askVol = book.Data.Asks.Sum(a => a.Quantity);

                    decimal imbalance = 0.5m;

                    if (bidVol + askVol > 0)
                        imbalance = bidVol / (bidVol + askVol);

                    // detect fake imbalance (walls flipping)
                    bool fakeImbalance =
                        signal.Side == SignalSide.Sell && imbalance > 0.58m && hasImpulse ||
                        signal.Side == SignalSide.Buy && imbalance < 0.42m && hasImpulse;

                    if (fakeImbalance)
                    {
                        _logger.LogWarning(
                            "[FAKE IMBALANCE][{symbol}] possible trap",
                            signal.Symbol);

                        return OrderResult.Fail("FAKE_IMBALANCE_TRAP");
                    }



                    decimal buyThreshold  = isToxicSymbol ? 0.43m : 0.35m;  // было 0.40 — слишком жёстко
                    decimal sellThreshold = isToxicSymbol ? 0.57m : 0.65m;  // было 0.60

                    if (signal.Side == SignalSide.Buy  && imbalance < buyThreshold)
                        imbalanceBlock = true;

                    if (signal.Side == SignalSide.Sell && imbalance > sellThreshold)
                        imbalanceBlock = true;

                    // ── Снимаем блок если тренд сильный и imbalance не экстремальный ──
                    // imbalance 0.20-0.35 может быть нормальным для быстрого движения
                    bool trendOverride =
                        (signal.IsSuperSignal || signal.Confidence >= 0.70m) &&
                        ((signal.Side == SignalSide.Buy  && imbalance >= 0.22m) ||
                         (signal.Side == SignalSide.Sell && imbalance <= 0.78m));

                    if (imbalanceBlock && trendOverride)
                    {
                        imbalanceBlock = false;
                        _logger.LogInformation(
                            "[IMBALANCE OVERRIDE][{symbol}] conf={conf:F2} imbalance={imb:F2} — entering on strong signal",
                            signal.Symbol, signal.Confidence, imbalance);
                    }

                    if (signal.Side == SignalSide.Sell && imbalance > 0.68m)
                        imbalanceBlock = true; // экстремальный перекос — не входим в шорт

                    if (!signal.IsSuperSignal)
                    {
                        if (imbalanceBlock)
                        {
                            _logger.LogWarning(
                           "[IMBALANCE BLOCK][{symbol}] side={side} imbalance={imbalance:F2}",
                           signal.Symbol,
                           signal.Side,
                           imbalance);

                            await _simulator.SimulateMissedTradeAsync(
                                signal,
                                "OrderBookImbalance",
                                note: $"imbalance={imbalance:F2}",
                                attemptNotional: notional,
                                requiredMinNotional: 0m);

                            return OrderResult.Fail("ORDERBOOK_IMBALANCE");
                        }
                           
                    }
                }
            }
            catch { }

            string failReason = null;
            // адаптивный лимит spread
            decimal maxSpread =
                (signal.Atr ?? 0m) > 0 && lastPrice > 0
                    ? Math.Clamp((signal.Atr.Value / lastPrice) * 0.20m, 0.0004m, 0.0020m)
                    : 0.0010m;

            if (imbalanceBlock && !signal.IsSuperSignal)
                failReason = "ORDERBOOK_IMBALANCE";
            else if (!signal.IsSuperSignal && spreadPct > maxSpread)
                failReason = "SPREAD_TOO_WIDE";
            else if (liquidityResult.Block)
                failReason = $"LiquidityGuard:{liquidityResult.Reason}";

            if (failReason != null)
            {
                await _simulator.SimulateMissedTradeAsync(
                    signal,
                    failReason,
                    attemptNotional: notional,
                    requiredMinNotional: filters.minQty * entryPrice
                );
                return OrderResult.Fail(failReason);
            }

          

            // блокируем вход
            if (!signal.IsSuperSignal && spreadPct > maxSpread)
            {
                _logger.LogWarning(
                    "[SPREAD BLOCK][{symbol}] spread={spread:P3} > max={max:P3}",
                    signal.Symbol,
                    spreadPct,
                    maxSpread);

                await _simulator.SimulateMissedTradeAsync(
                    signal,
                    "SpreadTooWide",
                    note: $"spread={spreadPct:P3}; max={maxSpread:P3}",
                    attemptNotional: notional,
                    requiredMinNotional: 0m);

                return OrderResult.Fail("SPREAD_TOO_WIDE");
            }

            // =====================================================
            // B) LIMIT PRICING
            // =====================================================

            // PASSIVE price must stay outside market
            decimal passiveLimit;

            if (side == OrderSide.Buy)
            {
                passiveLimit = lastPrice - tick;
            }
            else
            {
                passiveLimit = lastPrice + tick;
            }

            // =====================================================
            // CONTROLLED JOIN: catch “silent trend” safely
            // =====================================================

            // spread proxy через tick
           
            bool spreadOk = spreadPct <= maxSpread;

            bool silentTrendJoin =
     entryType == FuturesOrderType.Limit &&
     allowMarketEntry &&
     !hasImpulse &&
     isSmartStrongTrend &&
     rrOk &&
     liquiditySafe &&
     !liquidityResult.IsExtreme &&
     spreadPct <= maxSpread &&
     baseReg.TrendSlopePercent >= 0.25m &&
     priceDriftPct >= 0.0025m &&
    priceDriftPct <= 0.008m;

            // =====================================================
            // ADAPTIVE SAFE DRIFT
            // =====================================================

            decimal baseDriftPct = AGGR_LIMIT_OFFSET_PCT; // ~0.06%
            decimal superDriftPct = 0.0015m;               // 0.15%
            decimal hardCapPct = 0.0018m;                  // 0.18%

            decimal atrPct = 0m;

            if ((signal.Atr ?? 0m) > 0 && lastPrice > 0)
                atrPct = signal.Atr.Value / lastPrice;

            decimal liquidityFactor = Math.Clamp(liquidityResult.Score, 0.4m, 1.2m);

            decimal adaptiveDrift =
                Math.Clamp(
                    atrPct * 0.35m * liquidityFactor,
                    baseDriftPct,
                    hardCapPct);

            decimal safeDriftPct =
                signal.IsSuperSignal
                    ? Math.Min(adaptiveDrift * 1.3m, hardCapPct)
                    : adaptiveDrift;

            // =====================================================
            // JOIN LIMIT CALCULATION
            // =====================================================

            decimal joinLimitRaw =
                side == OrderSide.Buy
                    ? lastPrice * (1m + safeDriftPct)
                    : lastPrice * (1m - safeDriftPct);

            // anti-chase guard
            decimal maxJoinDrift;

            if (isToxicSymbol)
                maxJoinDrift = signal.IsSuperSignal ? 0.0025m : 0.0012m;
            else
                maxJoinDrift = signal.IsSuperSignal ? 0.004m : 0.0025m;

            if (entryPrice > 0)
            {
                if (side == OrderSide.Buy)
                    joinLimitRaw = Math.Min(joinLimitRaw, entryPrice * (1m + maxJoinDrift));
                else
                    joinLimitRaw = Math.Max(joinLimitRaw, entryPrice * (1m - maxJoinDrift));
            }

            // =====================================================
            // FINAL LIMIT SELECTION
            // =====================================================

            decimal finalLimitRaw =
                (silentTrendJoin || signal.IsSuperSignal)
                    ? joinLimitRaw
                    : passiveLimit;

            // safety guard vs zero / negative price
            if (finalLimitRaw <= 0)
                finalLimitRaw = lastPrice;

            // =====================================================
            // TICK QUANTIZATION (ONLY ONCE)
            // =====================================================

            decimal aggrLimitPrice = Quantize(finalLimitRaw, tick);

            // =====================================================
            // FINAL ORDER PARAMS
            // =====================================================
            decimal? orderPrice = entryType == FuturesOrderType.Market ? null : aggrLimitPrice;
            TimeInForce? tif = entryType == FuturesOrderType.Market ? null : TimeInForce.GoodTillCanceled;

      

            // =====================================================
            // LOG
            // =====================================================

            _logger.LogInformation(
                "[ORDER][{symbol}] ENTRY={type} mode={mode} orderPrice={orderPrice} last={last} " +
                "driftPct={driftPct:P3} priceDrift={priceDrift:P2} slope={slope:F3}% " +
                "impulse={imp} silentJoin={silent} super={super} liqExt={ext} liqScore={score:F2}",
                signal.Symbol,
                entryType,
                entryType == FuturesOrderType.Market
                    ? "MARKET"
                    : (silentTrendJoin ? "JOIN_LIMIT" : "PASSIVE_LIMIT"),
                orderPrice,
                lastPrice,
                safeDriftPct,
                priceDriftPct,
                baseReg.TrendSlopePercent,
                hasImpulse,
                silentTrendJoin,
                signal.IsSuperSignal,
                liquidityResult.IsExtreme,
                liquidityResult.Score
            );


            // =============================================================
            // APPLY CORRECT QTY RULE PER ORDER TYPE (MARKET vs LIMIT)
            // =============================================================

            var qtyRule = entryType == FuturesOrderType.Market
                ? SymbolInfoService.QtyRule.Market
                : SymbolInfoService.QtyRule.Limit;

            var fQty = await _symbolInfo.GetFuturesFiltersAsync(signal.Symbol, qtyRule);

            // safety step
            var stepRule = fQty.step > 0 ? fQty.step : step;

            // price used for notional calculation
            decimal notionalPrice =
                orderPrice ??
                (lastPrice > 0 ? lastPrice : entryPrice);

            if (notionalPrice <= 0)
                return OrderResult.Fail("INVALID_PRICE_FOR_NOTIONAL");

            // normalize quantity
            quantity = Math.Floor(quantity / stepRule) * stepRule;
            quantity = Math.Max(quantity, fQty.minQty);

            decimal finalNotional = quantity * notionalPrice;

            // =============================================================
            // MIN NOTIONAL GUARD
            // =============================================================

            if (finalNotional < fQty.minNotional)
            {
                if (_risk.LastBalanceUsdt * leverage >= fQty.minNotional)
                {
                    quantity = fQty.minNotional / notionalPrice;

                    // округление вверх до шага
                    quantity = Math.Ceiling(quantity / stepRule) * stepRule;

                    quantity = Math.Max(quantity, fQty.minQty);

                    finalNotional = quantity * notionalPrice;
                }
                else
                {
                    await _simulator.SimulateMissedTradeAsync(
                        signal,
                        "QTY_TOO_SMALL_AFTER_RULE",
                        note: $"entryType={entryType}; qtyRule={qtyRule}; qty={quantity}; minQty={fQty.minQty}; step={stepRule}",
                        attemptNotional: finalNotional,
                        requiredMinNotional: fQty.minNotional);

                    _executedSignalService.UpdateStatus(
                        signal.Symbol,
                        execTime,
                        TradeExecutionStatus.Blocked,
                        0,
                        0);

                    return OrderResult.Fail("QTY_TOO_SMALL_AFTER_RULE");
                }
            }

            // =============================================================
            // FINAL NOTIONAL AT ORDER CREATE
            // =============================================================

            decimal markForNotional =
                lastPrice > 0 ? lastPrice : entryPrice;

            decimal notionalAtCreate =
                orderPrice.HasValue
                    ? quantity * orderPrice.Value
                    : quantity * markForNotional;

            // =============================================================
            // FINAL ORDER PARAMS
            // =============================================================

            orderPrice = entryType == FuturesOrderType.Market
                ? null
                : aggrLimitPrice;

            tif = entryType == FuturesOrderType.Market
                ? null
                : TimeInForce.GoodTillCanceled;

            // =============================================================
            // HARD GUARDS
            // =============================================================

            if (entryType == FuturesOrderType.Market && orderPrice != null)
                return OrderResult.Fail("InvalidMarketOrderWithPrice");

            if (entryType == FuturesOrderType.Limit && orderPrice == null)
                return OrderResult.Fail("InvalidLimitOrderWithoutPrice");

            // =============================================================
            // PLACE ENTRY
            // =============================================================
            if (!_tradingOptions.CurrentValue.EnableExecution)
            {
                _logger.LogWarning(
                    "[DRY-RUN][{symbol}] Execution disabled → signal processed but NO order sent",
                    signal.Symbol);

                await _simulator.SimulateMissedTradeAsync(
                    signal,
                    "DRY_RUN_EXECUTION_DISABLED",
                    note: $"entryType={entryType}; qty={quantity}; price={(orderPrice?.ToString() ?? "MKT")}",
                    attemptNotional: notionalAtCreate,
                    requiredMinNotional: 0m);

                _executedSignalService.UpdateStatus(
                    signal.Symbol,
                    execTime,
                    TradeExecutionStatus.Blocked,
                    0,
                    0);

                return OrderResult.Fail("EXECUTION_DISABLED");
            }
            var entryRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol:                  signal.Symbol,
                side:                    side,
                type:                    entryType,
                quantity:                quantity,
                price:                   orderPrice,
                positionSide:            isHedge ? posSide : null,
                timeInForce:             tif,
                reduceOnly:              null,
                selfTradePreventionMode: SelfTradePreventionMode.ExpireMaker,
                ct:                      ct);

            _logger.LogInformation(
                "[ENTRY][{symbol}] PlaceOrder type={type} side={side} qty={qty} price={price} hedge={h} → {ok}",
                signal.Symbol, entryType, side, quantity, orderPrice, isHedge,
                entryRes.Success ? "OK" : $"FAIL code={entryRes.Error?.Code} msg={entryRes.Error?.Message}");

            // =============================================================
            // SAFE RETRY (LIMIT ONLY)
            // =============================================================

            if (!entryRes.Success && entryType == FuturesOrderType.Limit)
            {
                var errCode = entryRes.Error?.Code;

                bool retryable =
                    errCode == -2010 ||   // order rejected
                    errCode == -2021 ||   // immediate trigger
                    errCode == -4164;     // price filter

                if (retryable)
                {
                    try
                    {
                        var priceResp = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(signal.Symbol, ct: ct);

                        if (priceResp.Success && priceResp.Data?.Price > 0)
                        {
                            var last = priceResp.Data.Price;

                            var retryPrice = side == OrderSide.Buy
                                ? last - tick
                                : last + tick;

                            if (retryPrice <= 0)
                                retryPrice = last;

                            retryPrice = Quantize(retryPrice, tick);

                            // anti-chase protection
                            decimal maxDrift = 0.002m; // 0.2%

                            if (entryPrice > 0)
                            {
                                if (side == OrderSide.Buy)
                                    retryPrice = Math.Min(retryPrice, entryPrice * (1m + maxDrift));
                                else
                                    retryPrice = Math.Max(retryPrice, entryPrice * (1m - maxDrift));
                            }

                            _logger.LogWarning(
                                "[ORDER][{symbol}] Retry LIMIT after reject. newPrice={price}",
                                signal.Symbol,
                                retryPrice);

                            entryRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                                symbol: signal.Symbol,
                                side: side,
                                type: FuturesOrderType.Limit,
                                quantity: quantity,
                                price: retryPrice,
                                positionSide: isHedge ? posSide : null,
                                timeInForce: TimeInForce.GoodTillCanceled,
                                ct: ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[ORDER][{symbol}] Retry attempt failed", signal.Symbol);
                    }
                }
            }
            if (!entryRes.Success || entryRes.Data == null)
            {
                var errCode = entryRes.Error?.Code;
                var errMsg  = entryRes.Error?.Message ?? "no_error_object";
                var note    = $"code={errCode}; msg={errMsg}";

                // Переводим коды Binance в читаемые причины
                failReason = errCode switch
                {
                    -2010 => "INSUFFICIENT_MARGIN",
                    -1013 => "QTY_BELOW_MIN_NOTIONAL",
                    -1111 => "PRICE_PRECISION_ERROR",
                    -1100 => "ILLEGAL_CHARS_IN_PARAM",
                    -4003 => "QTY_BELOW_MIN",
                    -4164 => "PRICE_FILTER_ERROR",
                    -4015 => "REDUCE_ONLY_NO_POSITION",
                    -1021 => "TIMESTAMP_OUT_OF_SYNC",
                    -2011 => "UNKNOWN_ORDER",
                    -3041 => "INSUFFICIENT_BALANCE",
                    _ => $"ENTRY_FAILED:{errCode}"
                };

                _logger.LogError(
                    "[ENTRY_FAIL][{symbol}] {reason} | {note}",
                    signal.Symbol, failReason, note);

                await _simulator.SimulateMissedTradeAsync(
                    signal,
                    failReason,
                    note: note,
                    attemptNotional: Math.Max(notionalAtCreate, notional),
                    requiredMinNotional: 0m);

                _executedSignalService.UpdateStatus(
                    signal.Symbol,
                    execTime,
                    TradeExecutionStatus.OrderPartiallyFilled,
                    0,
                    0);

                return OrderResult.Fail(failReason, errCode, errMsg);
            }

            long entryOrderId = entryRes.Data.Id;

            _executedSignalService.UpdateStatus(
                signal.Symbol,
                execTime,
                TradeExecutionStatus.OrderCreated,
                quantity,
                notionalAtCreate);

            await _simulator.AppendLifecycleEventAsync(signal, "ORDER_CREATED", freeBalance: _risk.LastBalanceUsdt);

            // =============================================================
            // WAIT FILL
            // =============================================================
            bool marketFallbackUsed = false;

            var wait = await WaitForPositionOrOrderAsync(
                client,
                signal,
                posSide,
                entryOrderId,
                entryPrice,
                quantity,
                ct);

            //============new guard for limit order 
            // =====================================================
            // STALE LIMIT ORDER GUARD
            // =====================================================

            if (!wait.HasPosition && entryType == FuturesOrderType.Limit)
            { // проверяем, не пришла ли частичная позиция
                if (wait.Qty > 0)
                {
                    _logger.LogInformation("[LIMIT_PARTIAL_FILL] qty={0}", wait.Qty);
                    _entryTracker.RegisterEntry(signal.Symbol, posSide);
                    // TP на частичное заполнение
                    await PlaceFullProtectionAsync(client, signal, posSide, isHedge,
                        wait.EntryPrice, wait.Qty, filters, ct);
                    // Instant UI notification — Web refreshes positions immediately
                    _pushClient?.NotifyPositionChanged(
                        signal.Symbol,
                        signal.Side == SignalSide.Buy ? "LONG" : "SHORT",
                        "OPENED");
                    return OrderResult.Successs(wait.EntryPrice, wait.Qty, entryOrderId);
                }

                try
                {
                    var px = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(signal.Symbol, ct: ct);

                    if (px.Success && px.Data?.Price > 0)
                    {
                        var now = px.Data.Price;

                        decimal drift =
                            entryPrice > 0
                            ? Math.Abs(now - entryPrice) / entryPrice
                            : 0m;

                        if (drift > MAX_LIMIT_STALE_DRIFT)
                        {
                            _logger.LogWarning(
                                "[STALE LIMIT][{symbol}] cancel stale order drift={drift:P2}",
                                signal.Symbol,
                                drift);

                            try
                            {
                                await client.UsdFuturesApi.Trading.CancelOrderAsync(
                                    signal.Symbol,
                                    entryOrderId,
                                    ct: ct);
                            }
                            catch { }

                            await _simulator.SimulateMissedTradeAsync(
                                signal,
                                "StaleLimitOrder",
                                note: $"drift={drift:P2}",
                                attemptNotional: notionalAtCreate,
                                requiredMinNotional: 0m);

                            return OrderResult.Fail("STALE_LIMIT_ORDER");
                        }
                    }
                }
                catch { }
            }
            //====================




            // ============================================================
            // LIMIT TIMEOUT → MARKET FALLBACK (SAFE EXECUTION BLOCK)
            // ============================================================
            if (isToxicSymbol)
            {
                _logger.LogWarning(
                    "[TOXIC BLOCK][{symbol}] market fallback disabled",
                    signal.Symbol);

                return OrderResult.Fail("TOXIC_NO_FALLBACK_MARKET");
            }
            if (!wait.HasPosition &&
                entryType == FuturesOrderType.Limit &&
                wait.Reason == "TimeoutNoFill" &&
                !marketFallbackUsed)
            {
                marketFallbackUsed = true;

                // ========================================================
                // RACE CONDITION GUARD
                // position might appear just after timeout
                // ========================================================

                var posRecheck = await client.UsdFuturesApi.Account.GetPositionInformationAsync(signal.Symbol, ct:ct);

                if (posRecheck.Success && posRecheck.Data != null)
                {
                    var existing = posRecheck.Data.FirstOrDefault(p =>
                        Math.Abs(p.Quantity) > 0.00000001m &&
                        (
                            (signal.Side == SignalSide.Buy &&
                                (p.PositionSide == PositionSide.Long || p.PositionSide == PositionSide.Both)) ||

                            (signal.Side == SignalSide.Sell &&
                                (p.PositionSide == PositionSide.Short || p.PositionSide == PositionSide.Both))
                        ));

                    if (existing != null)
                    {
                        _logger.LogWarning(
                            "[RACE-GUARD][{symbol}] position appeared during timeout → abort fallback",
                            signal.Symbol);

                        _entryTracker.RegisterEntry(signal.Symbol, posSide);
                        await PlaceFullProtectionAsync(client, signal, posSide, isHedge,
                            existing.EntryPrice, Math.Abs(existing.Quantity), filters, ct);
                        return OrderResult.Successs(existing.EntryPrice, Math.Abs(existing.Quantity), entryOrderId);
                    }
                }

                // ========================================================
                // cancel old limit
                // ========================================================

                try
                {
                    await client.UsdFuturesApi.Trading.CancelOrderAsync(signal.Symbol, entryOrderId, ct: ct);
                }
                catch { }

                // ========================================================
                // read fresh price
                // ========================================================

                decimal markNow = lastPrice;

                try
                {
                    var pxNow = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(signal.Symbol, ct: ct);

                    if (pxNow.Success && pxNow.Data?.Price > 0)
                        markNow = pxNow.Data.Price;
                }
                catch { }

                decimal lastNow = markNow;

                // =====================================================
                // SMART LIMIT RETRY BEFORE MARKET
                // =====================================================

                decimal retryPrice;

                if (signal.Side == SignalSide.Buy)
                    retryPrice = lastNow - tick;
                else
                    retryPrice = lastNow + tick;

                retryPrice = Quantize(retryPrice, tick);

                _logger.LogInformation(
                    "[LIMIT RETRY][{symbol}] retryPrice={price}",
                    signal.Symbol,
                    retryPrice);

                var retryOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: signal.Symbol,
                    side: side,
                    type: FuturesOrderType.Limit,
                    quantity: quantity,
                    price: retryPrice,
                    positionSide: isHedge ? posSide : null,
                    timeInForce: TimeInForce.GoodTillCanceled,
                    ct: ct);

                if (retryOrder.Success && retryOrder.Data != null)
                {
                    var retryWait = await WaitForPositionOrOrderAsync(
                        client,
                        signal,
                        posSide,
                        retryOrder.Data.Id,
                        lastNow,
                        quantity,
                        ct);

                    if (retryWait.HasPosition)
                    {
                        wait = retryWait;
                        entryOrderId = retryOrder.Data.Id;
                        _entryTracker.RegisterEntry(signal.Symbol, posSide);
                        await PlaceFullProtectionAsync(client, signal, posSide, isHedge,
                            wait.EntryPrice, wait.Qty, filters, ct);
                        return OrderResult.Successs(wait.EntryPrice, wait.Qty, entryOrderId);
                    }
                }
                // ========================================================
                // ATR SLIPPAGE CAP
                // ========================================================
                bool structureBroken = false;
                decimal atr = signal.Atr ?? 0m;


                decimal atrSlipCap =
                    (signal.Atr ?? 0m) > 0 && entryPrice > 0
                        ? Math.Min(
                            (atr / entryPrice) * 1.4m,
                            MARKET_FALLBACK_MAX_SLIP_PCT * (signal.IsSuperSignal ? 1.4m : 1m))
                        : MARKET_FALLBACK_MAX_SLIP_PCT;

                decimal driftNowPct =
                    entryPrice > 0
                        ? Math.Abs(lastNow - entryPrice) / entryPrice
                        : 0m;

                // ========================================================
                // STRUCTURE BREAK GUARD
                // ========================================================

             

                decimal structureThreshold =
                    atr > 0
                    ? Math.Max(atr * 0.35m, tick * 2)
                    : tick * 4;
                if (signal.Side == SignalSide.Buy)
                    structureBroken = lastNow < entryPrice - structureThreshold;
                else
                    structureBroken = lastNow > entryPrice + structureThreshold;

                

                if (structureBroken)
                {
                    _logger.LogWarning(
                        "[LATE-CANCEL][{symbol}] structure broken → cancel entry",
                        signal.Symbol);

                    await _simulator.SimulateMissedTradeAsync(
                        signal,
                        "LateCancel:StructureBroken",
                        note: $"last={lastNow}; entry={entryPrice}; atr={(signal.Atr ?? 0m)}",
                        attemptNotional: notionalAtCreate,
                        requiredMinNotional: 0m);

                    _executedSignalService.UpdateStatus(
                        signal.Symbol,
                        execTime,
                        TradeExecutionStatus.Blocked,
                        0,
                        0);

                    return OrderResult.Fail("LATE_CANCEL_STRUCTURE_BROKEN");
                }

                // ========================================================
                // LIQUIDITY / EXTREME MOVE GUARD
                // ========================================================

                bool allowFallbackMarket =
                    !liquidityResult.IsExtreme ||
                    (signal.IsSuperSignal && rrOk);

                if (!allowFallbackMarket || driftNowPct > atrSlipCap)
                {
                    await _simulator.SimulateMissedTradeAsync(
                        signal,
                        "FallbackMarketBlocked",
                        note: $"drift={driftNowPct:P2}; max={atrSlipCap:P2}; extreme={liquidityResult.IsExtreme}",
                        attemptNotional: notionalAtCreate,
                        requiredMinNotional: 0m);

                    _executedSignalService.UpdateStatus(
                        signal.Symbol,
                        execTime,
                        TradeExecutionStatus.Blocked,
                        0,
                        0);

                    return OrderResult.Fail("FALLBACK_MKT_BLOCKED");
                }

                // ========================================================
                // PREPARE MARKET QTY
                // ========================================================

                var fMkt = await _symbolInfo.GetFuturesFiltersAsync(
                    signal.Symbol,
                    SymbolInfoService.QtyRule.Market);

                var mktQty = Math.Floor(quantity / fMkt.step) * fMkt.step;

                if (mktQty < fMkt.minQty)
                {
                    mktQty = fMkt.minQty;

                    _logger.LogInformation(
                        "[FALLBACK MARKET][{symbol}] qty bumped to minQty={minQty}",
                        signal.Symbol,
                        fMkt.minQty);
                }

                if (mktQty < fMkt.minQty)
                    return OrderResult.Fail("FALLBACK_MKT_QTY_TOO_SMALL");

                // ========================================================
                // MARKET ENTRY
                // ========================================================
                if (!await ConfirmEntryOn1m(signal.Symbol, signal.Side, ct, signal, smart))
                {
                    _logger.LogInformation("Entry rejected by 1m timing");
                    return OrderResult.Fail("Entry rejected by 1m timing");
                }
                var mktRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: signal.Symbol,
                    side: side,
                    type: FuturesOrderType.Market,
                    quantity: mktQty,
                    positionSide: isHedge ? posSide : null,
                    ct: ct);

                if (mktRes.Success && mktRes.Data != null)
                {
                    entryOrderId = mktRes.Data.Id;

                    wait = await WaitForPositionOrOrderAsync(
                        client,
                        signal,
                        posSide,
                        entryOrderId,
                        lastPrice,
                        mktQty,
                        ct);
                }
            }

            // ============================================================
            // FINAL CHECK
            // ============================================================

            if (!wait.HasPosition)
            {
                var reason = marketFallbackUsed
                    ? $"FallbackMarketFailed:{wait.Reason}"
                    : wait.Reason ?? "NotFilled";

                await _simulator.SimulateMissedTradeAsync(
                    signal,
                    reason,
                    note:
                    $"marketFallbackUsed={marketFallbackUsed}; " +
                    $"entryType={entryType}; " +
                    $"orderPrice={(orderPrice?.ToString() ?? "null")}; " +
                    $"lastPrice={lastPrice}; " +
                    $"entryPrice={entryPrice}; " +
                    $"waitReason={wait.Reason}",
                    attemptNotional: orderPrice.HasValue
                        ? quantity * orderPrice.Value
                        : notional,
                    requiredMinNotional: 0m);

                _executedSignalService.UpdateStatus(
                    signal.Symbol,
                    execTime,
                    TradeExecutionStatus.Blocked,
                    0,
                    0);

                return OrderResult.Fail(reason);
            }

            // =============================================================
            // FINAL SUCCESS
            // =============================================================
            entryPrice = wait.EntryPrice;
            quantity = wait.Qty;

            if (entryPrice <= 0 || quantity <= 0)
                return OrderResult.Fail("InvalidFinalState");

            _executedSignalService.UpdateStatus(
                signal.Symbol,
                execTime,
                TradeExecutionStatus.PositionOpened,
                quantity,
                quantity * entryPrice,
                filledEntry: entryPrice);

            await _simulator.AppendLifecycleEventAsync(signal, "POSITION_OPENED");

            filters = await _symbolInfo.GetFuturesFiltersAsync(signal.Symbol);
            tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;
            // =============================================================
            // CALCULATE TAKE PROFITS (IMMEDIATE)
            // =============================================================
            List<decimal> tps = new List<decimal>();

            if (signal.TakeProfits?.Any() == true)
            {
                // Сигнал уже имеет TP — квантуем к tick
                tps = signal.TakeProfits
                    .Select(tp => Quantize(tp, tick))
                    .ToList();
            }
            else
            {
                // TP нет — вычисляем из SL или из ATR
                decimal risk = signal.StopLoss > 0
                    ? Math.Abs(entryPrice - signal.StopLoss)
                    : (signal.Atr ?? 0m) * 1.5m;    // fallback: ATR×1.5

                if (risk <= 0)
                    risk = entryPrice * 0.005m;       // абсолютный fallback: 0.5%

                decimal rrTarget = 1.5m;
                decimal tpPrice = signal.Side == SignalSide.Buy
                    ? entryPrice + risk * rrTarget
                    : entryPrice - risk * rrTarget;

                tpPrice = Quantize(tpPrice, tick);
                tps.Add(tpPrice);
            }

            signal.TakeProfits = tps;

            _logger.LogInformation(
                "[TP_CALC][{symbol}] entry={entry} SL={sl} ATR={atr} TP1={tp1} TP2={tp2}",
                signal.Symbol, entryPrice, signal.StopLoss, signal.Atr,
                tps.Count > 0 ? tps[0] : 0,
                tps.Count > 1 ? tps[1] : 0);

            // =============================================================
            // =============================================================
            // PLACE FULL PROTECTION (ALL TPs + SL) IMMEDIATELY AFTER POSITION OPEN
            // =============================================================
            if (wait.HasPosition)
            {
                await PlaceFullProtectionAsync(client, signal, posSide, isHedge,
                    entryPrice, quantity, filters, ct);
            }

            _entryTracker.RegisterEntry(signal.Symbol, posSide);
            _pushClient?.NotifyPositionChanged(
                signal.Symbol,
                signal.Side == SignalSide.Buy ? "LONG" : "SHORT",
                "OPENED");
            return OrderResult.Successs(entryPrice, quantity, entryOrderId);
        }

        private static bool IsImpulse(
         IReadOnlyList<BinanceFuturesUsdtKline> klines,
         decimal atr,
         decimal minBodyAtr = 0.8m)  
        {
            if (klines == null || klines.Count < 2 || atr <= 0)
                return false;

            var last = klines[^1];

            var body = Math.Abs(last.ClosePrice - last.OpenPrice);
            var range = last.HighPrice - last.LowPrice;

            return body >= atr * minBodyAtr || range >= atr * 1.2m;
        }

        // =====================================================================
        // WAIT FOR POSITION or ORDER FILL (dual-track)
        // =====================================================================
        // =====================================================================
        // PLACE TP ORDERS — вызывается из ВСЕХ путей после успешного входа
        // Гарантирует что TP ставится независимо от пути (Market/Limit/Partial)
        // =====================================================================
        // Computes the per-level quantity for an institutional-style
        // scale-out allocation: heavier weight on the nearer (more
        // probable) targets, tapering off toward farther ones. 50/30/20
        // for 3 levels is a standard, widely-used professional starting
        // point (per direct confirmation to use this approach rather
        // than an equal split); for other counts, a similar tapering
        // shape is derived rather than falling back to equal weights.
        private static List<decimal> TpAllocationWeights(int count) => count switch
        {
            1 => new() { 1.0m },
            2 => new() { 0.6m, 0.4m },
            3 => new() { 0.5m, 0.3m, 0.2m },
            4 => new() { 0.4m, 0.3m, 0.2m, 0.1m },
            _ => Enumerable.Range(0, count)
                    .Select(i => (decimal)(count - i))
                    .Select(w => w / Enumerable.Range(1, count).Sum())
                    .ToList(),
        };

        private async Task PlaceFullProtectionAsync(
            BinanceRestClient client,
            TradeSignal signal,
            PositionSide posSide,
            bool isHedge,
            decimal entryPrice,
            decimal quantity,
            (decimal step, decimal minQty, decimal maxQty, decimal minNotional, decimal tickSize) filters,
            CancellationToken ct)
        {
            if (entryPrice <= 0 || quantity <= 0) return;

            var step = filters.step > 0 ? filters.step : 0.001m;
            var tick = filters.tickSize > 0 ? filters.tickSize : 0.0001m;
            var tpSide = signal.Side == SignalSide.Buy ? OrderSide.Sell : OrderSide.Buy;
            bool isLong = signal.Side == SignalSide.Buy;

            // Mark price для валидации
            decimal markPrice = 0m;
            try
            {
                var mk = await client.UsdFuturesApi.ExchangeData.GetMarkPriceAsync(signal.Symbol, ct);
                if (mk.Success) markPrice = mk.Data.MarkPrice;
            }
            catch { }

            // =================================================================
            // =================================================================
            // SMART DEDUP — check existing TP/SL orders before placing.
            // On position add-ons (second entry same symbol+side), calling
            // PlaceFullProtectionAsync blindly created 6 TPs + 2 SLs.
            // Now we fetch open orders first and decide: skip, replace, or keep.
            // =================================================================
            List<BinanceUsdFuturesOrder> existingOrders = new();
            try
            {
                var openRes = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(signal.Symbol, ct: ct);
                if (openRes.Success && openRes.Data != null)
                    existingOrders = openRes.Data
                        .Where(o => !isHedge || o.PositionSide == posSide)
                        .ToList();
            }
            catch { }

            var existingTps = existingOrders
                .Where(o => o.Type == FuturesOrderType.TakeProfitMarket)
                .OrderBy(o => isLong ? (o.StopPrice ?? 0m) : -(o.StopPrice ?? 0m))
                .ToList();
            var existingSlList = existingOrders
                .Where(o => o.Type == FuturesOrderType.StopMarket)
                .ToList();

            _logger.LogInformation(
                "[DEDUP][{sym}] existing TP={tp} SL={sl} | new TPs={newTp} newSL={hasSl}",
                signal.Symbol, existingTps.Count, existingSlList.Count,
                signal.TakeProfits?.Count ?? 0, signal.StopLoss > 0);

            // ── TP dedup ───────────────────────────────────────────────────
            bool skipTpPlacement = false;
            if (existingTps.Count > 0 && signal.TakeProfits != null && signal.TakeProfits.Count > 0)
            {
                var newTps = signal.TakeProfits
                    .Select(tp => tick > 0 ? Math.Round(tp / tick) * tick : tp).ToList();
                bool countMatch  = existingTps.Count == newTps.Count;
                bool levelsMatch = countMatch && existingTps.Zip(newTps, (ex, nw) =>
                    nw == 0 || Math.Abs(((ex.StopPrice ?? 0m) - nw) / nw) < 0.003m).All(x => x);

                if (levelsMatch)
                {
                    _logger.LogInformation("[DEDUP][{sym}] TPs match existing — skip re-placement", signal.Symbol);
                    skipTpPlacement = true;
                }
                else
                {
                    foreach (var tp in existingTps)
                        try { await client.UsdFuturesApi.Trading.CancelOrderAsync(signal.Symbol, tp.Id, ct: ct); } catch { }
                    _logger.LogInformation("[DEDUP][{sym}] Cancelled {n} old TPs — placing updated levels", signal.Symbol, existingTps.Count);
                }
            }

            // ── SL dedup ───────────────────────────────────────────────────
            bool skipSlPlacement = false;
            if (existingSlList.Count > 0 && signal.StopLoss > 0)
            {
                var bestSl = isLong
                    ? existingSlList.OrderByDescending(o => o.StopPrice ?? 0m).First()
                    : existingSlList.OrderBy(o => o.StopPrice ?? 0m).First();
                decimal slNew = tick > 0 ? Math.Round(signal.StopLoss / tick) * tick : signal.StopLoss;
                bool nearlyIdentical = Math.Abs(((bestSl.StopPrice ?? 0m) - slNew) / Math.Max(slNew, 0.0001m)) < 0.003m;
                bool existingBetter  = isLong ? (bestSl.StopPrice ?? 0m) > slNew : (bestSl.StopPrice ?? 0m) < slNew;

                if (nearlyIdentical || existingBetter)
                {
                    _logger.LogInformation("[DEDUP][{sym}] SL keep existing={ep} (new={np})", signal.Symbol, bestSl.StopPrice ?? 0m, slNew);
                    skipSlPlacement = true;
                    // Clean up any duplicate SLs beyond the best one
                    foreach (var dupe in existingSlList.Where(o => o.Id != bestSl.Id))
                        try { await client.UsdFuturesApi.Trading.CancelOrderAsync(signal.Symbol, dupe.Id, ct: ct); } catch { }
                }
                else
                {
                    // New SL is tighter — cancel old and place better one
                    foreach (var sl in existingSlList)
                        try { await client.UsdFuturesApi.Trading.CancelOrderAsync(signal.Symbol, sl.Id, ct: ct); } catch { }
                    _logger.LogInformation("[DEDUP][{sym}] SL replace: old={ep} → new={np}", signal.Symbol, bestSl.StopPrice ?? 0m, slNew);
                }
            }
            else if (existingSlList.Count > 1)
            {
                // Multiple SLs, no new signal SL — keep best, cancel duplicates
                var keepSl = isLong ? existingSlList.OrderByDescending(o => o.StopPrice ?? 0m).First()
                                    : existingSlList.OrderBy(o => o.StopPrice ?? 0m).First();
                foreach (var dupe in existingSlList.Where(o => o.Id != keepSl.Id))
                    try { await client.UsdFuturesApi.Trading.CancelOrderAsync(signal.Symbol, dupe.Id, ct: ct); } catch { }
                _logger.LogWarning("[DEDUP][{sym}] Cleaned {n} duplicate SLs, kept best", signal.Symbol, existingSlList.Count - 1);
            }

            // TAKE PROFITS — every level the signal actually computed, not
            // just TP1. Quantity split per the institutional weighting
            // above; the LAST level absorbs any rounding remainder so the
            // sum of all TP quantities never exceeds the real position size.
            // =================================================================
            if (!skipTpPlacement && signal.TakeProfits != null && signal.TakeProfits.Count > 0)
            {
                var tps = signal.TakeProfits
                    .Select(tp => tick > 0 ? Math.Round(tp / tick) * tick : tp)
                    .ToList();

                var weights = TpAllocationWeights(tps.Count);
                decimal totalQuantized = Math.Floor(quantity / step) * step;
                totalQuantized = Math.Max(totalQuantized, filters.minQty);

                decimal allocatedSoFar = 0m;
                for (int i = 0; i < tps.Count; i++)
                {
                    bool isLastLevel = i == tps.Count - 1;
                    decimal tpPrice = tps[i];

                    // Валидация: TP должен быть ВЫШЕ mark для лонга, НИЖЕ для шорта
                    bool valid = markPrice <= 0 ||
                        (isLong && tpPrice > markPrice) ||
                        (!isLong && tpPrice < markPrice);

                    if (!valid)
                    {
                        // Корректируем — ставим минимальный разумный TP,
                        // staggered slightly per level so multiple invalid
                        // levels don't all collapse onto the exact same price.
                        tpPrice = isLong
                            ? markPrice + tick * 10 * (i + 1)
                            : markPrice - tick * 10 * (i + 1);
                        _logger.LogWarning(
                            "[TP_ADJUSTED][{symbol}] TP{level} invalid vs mark={mark} → adjusted to {tp}",
                            signal.Symbol, i + 1, markPrice, tpPrice);
                    }

                    decimal tpQty = isLastLevel
                        ? Math.Max(0m, totalQuantized - allocatedSoFar) // remainder absorbs rounding, never exceeds real position size
                        : Math.Floor(totalQuantized * weights[i] / step) * step;
                    tpQty = Math.Max(tpQty, filters.minQty);
                    if (!isLastLevel) allocatedSoFar += tpQty;

                    if (tpQty <= 0) continue;

                    _logger.LogInformation(
                        "[TP_PLACE][{symbol}] level={level} price={tp} qty={qty} mark={mark} entry={entry}",
                        signal.Symbol, i + 1, tpPrice, tpQty, markPrice, entryPrice);

                    // Попытка 1: WorkingType.Mark
                    var res = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol:       signal.Symbol,
                        side:         tpSide,
                        type:         FuturesOrderType.TakeProfitMarket,
                        stopPrice:    tpPrice,
                        quantity:     tpQty,
                        reduceOnly:   true,
                        positionSide: isHedge ? posSide : null,
                        workingType:  WorkingType.Mark,
                        selfTradePreventionMode: SelfTradePreventionMode.ExpireMaker,
                        ct: ct);

                    if (res.Success)
                    {
                        _logger.LogInformation(
                            "[TP_PLACED][{symbol}] level={level} orderId={id} price={tp} qty={qty} (Mark)",
                            signal.Symbol, i + 1, res.Data?.Id, tpPrice, tpQty);
                        continue;
                    }

                    _logger.LogWarning(
                        "[TP_FAIL_MARK][{symbol}] level={level} code={code} msg={msg} → retry Contract",
                        signal.Symbol, i + 1, res.Error?.Code, res.Error?.Message);

                    // Попытка 2: WorkingType.Contract
                    var res2 = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol:       signal.Symbol,
                        side:         tpSide,
                        type:         FuturesOrderType.TakeProfitMarket,
                        stopPrice:    tpPrice,
                        quantity:     tpQty,
                        reduceOnly:   true,
                        positionSide: isHedge ? posSide : null,
                        workingType:  WorkingType.Contract,
                        ct: ct);

                    if (res2.Success)
                    {
                        _logger.LogInformation(
                            "[TP_PLACED_CONTRACT][{symbol}] level={level} orderId={id} price={tp} qty={qty}",
                            signal.Symbol, i + 1, res2.Data?.Id, tpPrice, tpQty);
                        continue;
                    }

                    _logger.LogError(
                        "[TP_FAIL_FINAL][{symbol}] level={level} code={code} msg={msg} → trying ALGO endpoint",
                        signal.Symbol, i + 1, res2.Error?.Code, res2.Error?.Message);

                    // CRITICAL FIX: both attempts above use the regular
                    // PlaceOrderAsync endpoint, which Binance's mandatory
                    // Dec 9 2025 migration moved ALL conditional orders
                    // (STOP_MARKET/TAKE_PROFIT_MARKET) away from — this
                    // endpoint now rejects them outright (-4120), meaning both
                    // attempts above were failing every single time before
                    // this fix, with no path to actually succeed. This is the
                    // exact reason the at-entry TP was never actually visible
                    // on the exchange ("decided this, but never saw it work").
                    // Falls back to the dedicated Algo Order endpoint, same
                    // mechanism PositionSupervisorService already uses
                    // successfully for its own emergency SL/TP and BE-move
                    // placement.
                    var algoOk = await _algoOrders.PlaceConditionalAsync(
                        symbol: signal.Symbol,
                        side: tpSide,
                        positionSide: posSide,
                        type: "TAKE_PROFIT_MARKET",
                        quantity: tpQty,
                        triggerPrice: tpPrice,
                        workingType: "MARK_PRICE",
                        reduceOnly: isHedge ? null : true,
                        ct: ct);

                    if (algoOk)
                    {
                        _logger.LogInformation(
                            "[TP_PLACED_ALGO][{symbol}] level={level} price={tp} qty={qty} (via Algo Order endpoint)",
                            signal.Symbol, i + 1, tpPrice, tpQty);
                    }
                    else
                    {
                        _logger.LogError(
                            "[TP_FAIL_ALGO][{symbol}] level={level} Algo endpoint also failed — Supervisor will place emergency TP",
                            signal.Symbol, i + 1);
                    }
                }
            }

            // =================================================================
            // STOP LOSS — per direct, explicit request: this was NEVER placed
            // at all before this fix. The FULL position quantity (not split
            // like the TPs above) — a stop loss protects the entire remaining
            // position regardless of which TP levels have or haven't filled
            // yet, so it always covers the originally-requested quantity.
            // Same proven triple-fallback pattern as the TP placement above.
            // =================================================================
            if (!skipSlPlacement && signal.StopLoss > 0)
            {
                decimal slPrice = tick > 0 ? Math.Round(signal.StopLoss / tick) * tick : signal.StopLoss;
                var slSide = signal.Side == SignalSide.Buy ? OrderSide.Sell : OrderSide.Buy;

                // Валидация: SL должен быть НИЖЕ mark для лонга, ВЫШЕ для шорта
                // (opposite direction from the TP validation above)
                bool slValid = markPrice <= 0 ||
                    (isLong && slPrice < markPrice) ||
                    (!isLong && slPrice > markPrice);

                if (!slValid)
                {
                    slPrice = isLong
                        ? markPrice - tick * 10
                        : markPrice + tick * 10;
                    _logger.LogWarning(
                        "[SL_ADJUSTED][{symbol}] SL invalid vs mark={mark} → adjusted to {sl}",
                        signal.Symbol, markPrice, slPrice);
                }

                decimal slQty = Math.Floor(quantity / step) * step;
                slQty = Math.Max(slQty, filters.minQty);

                _logger.LogInformation(
                    "[SL_PLACE][{symbol}] price={sl} qty={qty} mark={mark} entry={entry}",
                    signal.Symbol, slPrice, slQty, markPrice, entryPrice);

                // Попытка 1: WorkingType.Mark
                var slRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol:       signal.Symbol,
                    side:         slSide,
                    type:         FuturesOrderType.StopMarket,
                    stopPrice:    slPrice,
                    quantity:     slQty,
                    reduceOnly:   true,
                    positionSide: isHedge ? posSide : null,
                    workingType:  WorkingType.Mark,
                    selfTradePreventionMode: SelfTradePreventionMode.ExpireMaker,
                    ct: ct);

                if (slRes.Success)
                {
                    _logger.LogInformation(
                        "[SL_PLACED][{symbol}] orderId={id} price={sl} qty={qty} (Mark)",
                        signal.Symbol, slRes.Data?.Id, slPrice, slQty);
                }
                else
                {
                    _logger.LogWarning(
                        "[SL_FAIL_MARK][{symbol}] code={code} msg={msg} → retry Contract",
                        signal.Symbol, slRes.Error?.Code, slRes.Error?.Message);

                    // Попытка 2: WorkingType.Contract
                    var slRes2 = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol:       signal.Symbol,
                        side:         slSide,
                        type:         FuturesOrderType.StopMarket,
                        stopPrice:    slPrice,
                        quantity:     slQty,
                        reduceOnly:   true,
                        positionSide: isHedge ? posSide : null,
                        workingType:  WorkingType.Contract,
                        ct: ct);

                    if (slRes2.Success)
                    {
                        _logger.LogInformation(
                            "[SL_PLACED_CONTRACT][{symbol}] orderId={id} price={sl} qty={qty}",
                            signal.Symbol, slRes2.Data?.Id, slPrice, slQty);
                    }
                    else
                    {
                        _logger.LogError(
                            "[SL_FAIL_FINAL][{symbol}] code={code} msg={msg} → trying ALGO endpoint",
                            signal.Symbol, slRes2.Error?.Code, slRes2.Error?.Message);

                        // Same Binance Dec 2025 migration reasoning as the TP
                        // fallback above — STOP_MARKET also requires the Algo
                        // Order endpoint now, not the regular order endpoint.
                        var slAlgoOk = await _algoOrders.PlaceConditionalAsync(
                            symbol: signal.Symbol,
                            side: slSide,
                            positionSide: posSide,
                            type: "STOP_MARKET",
                            quantity: slQty,
                            triggerPrice: slPrice,
                            workingType: "MARK_PRICE",
                            reduceOnly: isHedge ? null : true,
                            ct: ct);

                        if (slAlgoOk)
                        {
                            _logger.LogInformation(
                                "[SL_PLACED_ALGO][{symbol}] price={sl} qty={qty} (via Algo Order endpoint)",
                                signal.Symbol, slPrice, slQty);
                        }
                        else
                        {
                            _logger.LogError(
                                "[SL_FAIL_ALGO][{symbol}] Algo endpoint also failed — Supervisor will place emergency SL",
                                signal.Symbol);
                        }
                    }
                }
            }
            else
            {
                _logger.LogWarning(
                    "[SL_MISSING][{symbol}] signal.StopLoss is 0/unset — no Stop Loss placed at entry; Supervisor will place an emergency one",
                    signal.Symbol);
            }
        }

        private async Task<(bool HasPosition, decimal EntryPrice, decimal Qty, string Reason)> WaitForPositionOrOrderAsync(
            BinanceRestClient client,
            TradeSignal signal,
            PositionSide posSide,
            long entryOrderId,
            decimal fallbackEntry,
            decimal requestedQty,
            CancellationToken ct)
        {
            const int maxLoops = 60;            // 60 * 500ms ~ 30s
            const int delayMs = 740;
            const decimal maxSlipPct = 0.004m;  // 0.4% runaway log threshold

            decimal lastExecuted = 0m;
            bool runawayLogged = false;

            for (int i = 0; i < maxLoops; i++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    // ---- 1) Read order ----
                    OrderStatus? status = null;
                    decimal executedQty = 0m;
                    decimal avgPrice = fallbackEntry;

                    var ordRes = await client.UsdFuturesApi.Trading.GetOrderAsync(signal.Symbol, entryOrderId, ct: ct);
                    if (ordRes.Success && ordRes.Data != null)
                    {
                        status = ordRes.Data.Status;
                        executedQty = ordRes.Data.QuantityFilled;

                        avgPrice = ordRes.Data.AveragePrice > 0
                            ? ordRes.Data.AveragePrice
                            : fallbackEntry;

                        // progress log
                        if (executedQty > 0 && executedQty != lastExecuted)
                        {
                            lastExecuted = executedQty;
                            _logger.LogInformation(
                                "[ORDER][{symbol}] Partial fill: {exec}/{total} status={st}",
                                signal.Symbol, executedQty, ordRes.Data.Quantity, status);
                        }

                        // ✅ PRO FIX: if FILLED — accept immediately, position can lag
                        if (status == OrderStatus.Filled)
                        {
                            var qty =
     executedQty > 0
         ? executedQty
         : (requestedQty > 0
             ? requestedQty
             : 0m);

                            var entry =
                                avgPrice > 0
                                    ? avgPrice
                                    : fallbackEntry;

                            _logger.LogInformation(
                                "[ORDER][{symbol}] Order FILLED before position visible → accept fill. qty={qty} entry={entry}",
                                signal.Symbol, qty, entry);

                            return (true, entry, qty, "OrderFilled");
                        }

                        // If order is dead and nothing filled -> fail early
                        if (status is OrderStatus.Canceled or OrderStatus.Rejected or OrderStatus.Expired)
                        {
                            if (executedQty <= 0)
                                return (false, 0m, 0m, status switch
                                {
                                    OrderStatus.Canceled => "OrderCanceled",
                                    OrderStatus.Rejected => "OrderRejected",
                                    OrderStatus.Expired => "OrderExpired",
                                    _ => "OrderDead"
                                });

                            // executedQty > 0: do NOT fail here, keep checking position for a bit
                            _logger.LogWarning(
                                "[ORDER][{symbol}] Order {st} with exec={exec} → waiting position lag",
                                signal.Symbol, status, executedQty);
                        }
                    }

                    // ---- 2) Read position (Binance bug-safe: NO symbol filter) ----
                    var posRes = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                    if (posRes.Success && posRes.Data != null)
                    {
                        var pos = posRes.Data.FirstOrDefault(p =>
                            p.Symbol == signal.Symbol &&
                            p.PositionSide == posSide &&
                            p.Quantity != 0m);

                        if (pos != null)
                        {
                            var qty = Math.Abs(pos.Quantity);
                            var entry = pos.EntryPrice > 0 ? pos.EntryPrice : avgPrice;

                            _logger.LogInformation(
                                "[ORDER][{symbol}] Position detected: side={side}, qty={qty}, entry={entry}",
                                signal.Symbol, posSide, qty, entry);

                            return (true, entry, qty, "PositionOpened");
                        }
                    }

                    // ---- 3) Runaway log (non-fatal) ----
                    if (lastExecuted <= 0 && (i % 10 == 0))
                    {
                        try
                        {
                            var priceRes = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(signal.Symbol, ct: ct);
                            if (priceRes.Success && priceRes.Data != null && priceRes.Data.Price > 0 && fallbackEntry > 0)
                            {
                                var mark = priceRes.Data.Price;
                                decimal diffPct;
                                decimal runawayThreshold = signal.IsSuperSignal ? maxSlipPct * 2m : maxSlipPct;
                                if (posSide == PositionSide.Long)
                                {
                                    diffPct = (mark - fallbackEntry) / fallbackEntry;
                                    if (diffPct >= runawayThreshold && !runawayLogged)
                                    {
                                        runawayLogged = true;
                                        _logger.LogWarning(
                                            "[ORDER][{symbol}] PRICE RUN AWAY (LONG) → keep LIMIT alive: entry={e}, mark={m}, diff={d:P2}",
                                            signal.Symbol, fallbackEntry, mark, diffPct);
                                    }
                                }
                                else
                                {
                                    diffPct = (fallbackEntry - mark) / fallbackEntry;
                                    if (diffPct >= runawayThreshold && !runawayLogged)
                                    {
                                        runawayLogged = true;
                                        _logger.LogWarning(
                                            "[ORDER][{symbol}] PRICE RUN AWAY (SHORT) → keep LIMIT alive: entry={e}, mark={m}, diff={d:P2}",
                                            signal.Symbol, fallbackEntry, mark, diffPct);
                                    }
                                }
                            }
                        }
                        catch (Exception exPrice)
                        {
                            _logger.LogWarning(exPrice, "[ORDER][{symbol}] Error reading price", signal.Symbol);
                        }
                    }

                    await Task.Delay(delayMs, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ORDER][{symbol}] Error in WaitForPositionOrOrder loop", signal.Symbol);
                    await Task.Delay(delayMs, ct);
                }
            }

            // ---- 4) Final check: order + position ----
            try
            {
                OrderStatus? status = null;
                decimal executedQty = 0m;
                decimal avgPrice = fallbackEntry;

                var ordRes = await client.UsdFuturesApi.Trading.GetOrderAsync(signal.Symbol, entryOrderId, ct: ct);
                if (ordRes.Success && ordRes.Data != null)
                {
                    status = ordRes.Data.Status;
                    executedQty = ordRes.Data.QuantityFilled;

                    avgPrice = ordRes.Data.AveragePrice > 0
                        ? ordRes.Data.AveragePrice
                        : fallbackEntry;

                    _logger.LogWarning(
                        "[ORDER][{symbol}] After wait: status={st}, exec={exec}, avg={avg}",
                        signal.Symbol, status, executedQty, avgPrice);
                }

                var posRes = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
                if (posRes.Success && posRes.Data != null)
                {
                    var pos = posRes.Data.FirstOrDefault(p =>
                        p.Symbol == signal.Symbol &&
                        p.PositionSide == posSide &&
                        p.Quantity != 0m);

                    if (pos != null)
                    {
                        var qty = Math.Abs(pos.Quantity);
                        var entry = pos.EntryPrice > 0 ? pos.EntryPrice : avgPrice;

                        _logger.LogInformation(
                            "[ORDER][{symbol}] Position detected AFTER wait: side={side}, qty={qty}, entry={entry}",
                            signal.Symbol, posSide, qty, entry);

                        return (true, entry, qty, "PositionOpenedAfterWait");
                    }
                }

                // Try cancel order (best-effort)
                try
                {
                    await client.UsdFuturesApi.Trading.CancelOrderAsync(signal.Symbol, entryOrderId, ct: ct);
                }
                catch { }

                // ✅ PRO FIX: if any execution happened -> accept fill (position may lag)
                if (executedQty > 0m)
                {
                    var entry = avgPrice > 0 ? avgPrice : fallbackEntry;

                    _logger.LogWarning(
                        "[ORDER][{symbol}] ExecQty>0 but position not visible yet → accept fill (exchange lag). exec={exec} entry={entry}",
                        signal.Symbol, executedQty, entry);

                    return (true, entry, executedQty, "OrderExecutedAwaitPosLag");

                }

                // Real no-fill case
                return (false, 0m, 0m, "TimeoutNoFill");
            }
            catch (Exception exFinal)
            {
                _logger.LogError(exFinal, "[ORDER][{symbol}] Fatal in WaitForPositionOrOrderAsync", signal.Symbol);
                return (false, 0m, 0m, "WaitFatalError");
            }
        }

     
    }
}


