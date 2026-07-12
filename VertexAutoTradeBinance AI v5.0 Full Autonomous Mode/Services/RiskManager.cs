using Binance.Net.Enums;
using CryptoExchange.Net;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class RiskManager
    {
        private readonly ILogger<RiskManager> _logger;
        private readonly SymbolInfoService _symbolInfo;
        private readonly BinanceClientFactory _factory;
        private readonly MarketDataService _marketData;
        private readonly AiLeverageService _aiLeverage;
        private readonly AiMarketRegimeService _marketRegimeService;
        private readonly SmartRegimeService _smartRegime;
        private readonly TradingOptionsResolver _tradingResolver;
        private readonly AiSelfLearningService _ai;
        private readonly LiquidationRiskEngine _liqRisk;

        public string? LastRejectReason { get; private set; }
        public decimal LastBalanceUsdt { get; private set; }

        public RiskManager(
            ILogger<RiskManager> logger,
            SymbolInfoService symbolInfo,
            BinanceClientFactory factory,
            MarketDataService marketData,
            AiLeverageService aiLeverage,
            AiMarketRegimeService marketRegimeService,
            SmartRegimeService smartRegime,
            TradingOptionsResolver tradingResolver,
            AiSelfLearningService ai,
            LiquidationRiskEngine liqRisk
        )
        {
            _logger = logger;
            _symbolInfo = symbolInfo;
            _factory = factory;
            _marketData = marketData;
            _aiLeverage = aiLeverage;
            _marketRegimeService = marketRegimeService;
            _smartRegime = smartRegime;
            _tradingResolver = tradingResolver;
            _ai = ai;
            _liqRisk = liqRisk;
        }

        public int GetPrecision(decimal step)
        {
            step = step.Normalize();
            int[] bits = decimal.GetBits(step);
            int scale = (bits[3] >> 16) & 0x7F;
            return scale;
        }

        // effectiveLeverage: already computed by TradingWorker as
        //   configLeverage × aiLevMult (clamped to [70%, 100%] of config).
        // Passing it as a parameter avoids re-reading trading.Leverage here
        // and ensures RiskManager uses the SAME value that was set on exchange.
        // If not provided (0), falls back to trading.Leverage.
        /// <summary>
        /// Computes the effective leverage to use for a given symbol/timeframe.
        /// Result = configLeverage × AiLeverageService.Calculate(),
        /// clamped so AI can only REDUCE leverage (never increase above config).
        /// Call this from TradingWorker BEFORE GetPropDeskQtyFinal so both
        /// qty calculation and ExecuteAsync use the same leverage value.
        /// </summary>
        public decimal GetEffectiveLeverage(
            string symbol,
            Binance.Net.Enums.KlineInterval tf,
            IReadOnlyList<Binance.Net.Interfaces.IBinanceKline> klines,
            TradingOptions trading)
        {
            decimal configLev = trading.Leverage > 0 ? (decimal)trading.Leverage : 10m;

            decimal aiMult = 1.0m;
            try
            {
                if (klines?.Count >= 30)
                {
                    var castKlines = klines
                        .OfType<Binance.Net.Objects.Models.Futures.BinanceFuturesUsdtKline>()
                        .ToList();
                    if (castKlines.Count >= 30)
                        aiMult = _aiLeverage.Calculate(symbol, tf, castKlines);
                }
            }
            catch { /* non-critical — use 1.0x */ }

            // AI can only reduce leverage, never increase above config ceiling
            decimal effective = Math.Clamp(
                configLev * aiMult,
                configLev * 0.70m,   // floor: 70% of config (raised from 40% — 19x×0.4=7.6x was too low)
                configLev * 1.00m);  // ceil:  never above config value

            _logger.LogInformation(
                "[LEV][{sym}] config={cfg}x aiMult={mult:F2} → effective={eff:F1}x",
                symbol, configLev, aiMult, effective);

            return effective;
        }

        public decimal GetPropDeskQtyFinal(
            TradeSignal signal,
            decimal balance,
            decimal step,
            decimal minQty,
            decimal riskMult,
            TradingOptions trading,
            decimal effectiveLeverage = 0m)
        {
            LastRejectReason = null;

            // -----------------------------
            // VALIDATION
            // -----------------------------
            if (signal == null || balance <= 0 || step <= 0)
            {
                LastRejectReason = "Invalid input params";
                return 0;
            }

            decimal entry = signal.EntryPrice;
            decimal stop = signal.StopLoss;

            if (entry <= 0 || stop <= 0)
            {
                LastRejectReason = "Invalid entry/stop";
                return 0;
            }

            // Use passed effectiveLeverage (config × AI mult) if provided,
            // otherwise fall back to config value directly.
            decimal leverage = effectiveLeverage > 0
                ? effectiveLeverage
                : (trading.Leverage > 0 ? trading.Leverage : (signal.Leverage ?? 1m));
            if (leverage <= 0)
            {
                LastRejectReason = "Invalid leverage";
                return 0;
            }

            // -----------------------------
            // STOP % и базовый риск
            // -----------------------------
            decimal slDistance = Math.Abs(entry - stop);

            // Minimum SL distance scales with leverage.
            // At 19-25x, a 0.2% SL sits only 3-4% away from liquidation price —
            // dangerously close. Formula: minSL = 0.5% base + 0.02% per leverage unit.
            // Examples: 10x→0.7%, 19x→0.88%, 25x→1.0%
            decimal minSlPct = 0.005m + leverage * 0.0002m;  // 0.5% + 0.02% per lev
            minSlPct = Math.Clamp(minSlPct, 0.005m, 0.015m); // floor 0.5%, ceil 1.5%

            decimal rawSlPct = entry > 0 ? slDistance / entry : 0m;
            decimal slPercent = Math.Max(rawSlPct, minSlPct);

            if (rawSlPct < minSlPct)
                _logger.LogWarning(
                    "[RISK] SL too close for {symbol} (lev={lev}x): {raw:P2} < min {min:P2} — forcing floor",
                    signal.Symbol, leverage, rawSlPct, minSlPct);

            // -----------------------------
            // BASE RISK + Safety Multiplier
            // -----------------------------
            decimal baseRisk = trading.RiskPerTrade > 0
                ? (decimal)trading.RiskPerTrade
                : GetDynamicBaseRisk(balance);

            decimal safetyMult = signal.SafetyRiskMultiplier > 0 ? signal.SafetyRiskMultiplier : 1m;
            decimal winRate = _ai.GetWinRate(signal.Side);
            decimal finalRisk = CalculateAdaptiveRisk(signal, baseRisk, riskMult, winRate);

            // WinRate adjustment is now applied INSIDE CalculateAdaptiveRisk
            // (before the clamp) — removed from here to avoid bypassing the
            // [0.2%, 5%] safety ceiling.

            if (finalRisk <= 0)
            {
                LastRejectReason = "Final risk <= 0";
                return 0;
            }

            decimal riskBudget = balance * finalRisk;

            // =============================================================
            // SMALL BALANCE MODE (balance < 50$)
            // При малом депозите не можем считать от riskBudget/slPercent —
            // результат слишком мал. Вместо этого:
            // - Входим на minNotional (минимальный ордер Binance = 5$)
            // - Используем весь доступный баланс × leverage
            // - Это позволяет торговать при депозите $10-20
            // =============================================================
            decimal minNotional = trading.MinNotional > 0 ? trading.MinNotional : 5m;

            if (balance < 50m)
            {
                // ═══════════════════════════════════════════════════════════
                // INTELLIGENT MICRO-ACCOUNT MODE ($10-50)
                // Цель: наращивать баланс без риска слива
                //
                // Принципы:
                // 1. Фиксированный риск 2% баланса на сделку (Kelly 1/4)
                // 2. Минимальный notional 5$ (Binance limit)
                // 3. Приоритет: сохранить капитал → потом наращивать
                // 4. Лимит потерь подряд: после 2 лоссов → уменьшаем
                // ═══════════════════════════════════════════════════════════

                // Базовый риск 2% баланса
                decimal microRisk = 0.02m;

                // Корректируем на win rate AI
                var wr = _ai.GetWinRate(signal.Side);
                if (wr < 0.45m)       microRisk = 0.015m; // плохой WR → осторожнее
                else if (wr >= 0.60m) microRisk = 0.025m; // хороший WR → чуть больше

                // Корректируем на confidence сигнала
                if (signal.Confidence >= 0.70m) microRisk *= 1.2m;
                if (signal.Confidence < 0.45m)  microRisk *= 0.8m;

                // Корректируем на SizeMultiplier из стратегии
                if (signal.SizeMultiplier > 0m)
                    microRisk *= Math.Clamp(signal.SizeMultiplier, 0.5m, 1.5m);

                microRisk = Math.Clamp(microRisk, 0.010m, 0.030m); // 1%-3% жёсткий лимит

                // Рискуем X% баланса
                decimal riskBudgetMicro = balance * microRisk;

                // Размер позиции = riskBudget / slPercent / entry
                decimal rawNotional = riskBudgetMicro / slPercent;

                // Не больше 60% баланса × leverage (маржинальный лимит)
                // Cap at 35% of max leverage capacity (was 60%).
                // Prevents micro-account from over-concentrating on a single trade.
                decimal maxNotionalMicro = balance * leverage * 0.35m;
                decimal targetNotional   = Math.Min(rawNotional, maxNotionalMicro);

                // Минимум 5$ notional (иначе Binance отклонит)
                if (targetNotional < minNotional)
                    targetNotional = minNotional;

                // Рассчитываем qty
                decimal minQtyForMin = Math.Ceiling(minNotional / entry / step) * step;
                decimal effectiveMinQtyMicro = Math.Max(minQty, minQtyForMin);

                decimal rawQtyMicro = targetNotional / entry;
                decimal qtyMicro    = Math.Floor(rawQtyMicro / step) * step;
                if (qtyMicro < effectiveMinQtyMicro) qtyMicro = effectiveMinQtyMicro;

                decimal actualNotional = qtyMicro * entry;
                decimal actualRiskPct  = actualNotional > 0 && leverage > 0
                    ? (actualNotional / leverage) / balance * slPercent
                    : 0m;

                _logger.LogInformation(
                    "[RISK][MICRO] balance={bal:F2}$ risk={rsk:P1} slPct={sl:P2} " +
                    "notional={ntn:F2}$ qty={qty} lev={lev}x → actualRisk={ar:P2}",
                    balance, microRisk, slPercent,
                    actualNotional, qtyMicro, leverage, actualRiskPct);

                // Liquidation risk check
                if (_liqRisk != null)
                {
                    var liqCheck = _liqRisk.CheckPreTrade(signal, qtyMicro, balance, leverage);
                    if (!liqCheck.IsAllowed)
                    {
                        LastRejectReason = $"LIQ_RISK_BLOCKED: {liqCheck.BlockReason}";
                        return 0;
                    }
                    if (liqCheck.SafeQty < qtyMicro && liqCheck.SafeQty > 0)
                        qtyMicro = Math.Floor(liqCheck.SafeQty / step) * step;
                }

                return qtyMicro;
            }

            // -----------------------------
            // Notional calculation (normal balance >= 50$)
            // -----------------------------
            decimal riskNotional = riskBudget / slPercent;
            // 95% of max leverage capacity — leaves a 5% buffer for
            // funding rates, price gaps, and exchange rounding.
            // Was 90% which unnecessarily left 10% unused capacity.
            decimal leverageCapNotional = balance * leverage * 0.95m;

            // balance < 50 path already returned early above via the
            // MICRO-ACCOUNT block — this code is only reachable when
            // balance >= 50, so the ternary was dead code. Use
            // trading.MaxMarginPercent unconditionally here.
            decimal marginCapNotional = balance * trading.MaxMarginPercent * leverage;

            decimal finalNotional = Math.Min(riskNotional, Math.Min(leverageCapNotional, marginCapNotional));
            
            if (finalNotional <= 0)
            {
                LastRejectReason = "Final notional <= 0";
                return 0;
            }

            // =============================================================
            // ADAPTIVE MINNOTIONAL
            // =============================================================
            // minNotional already declared above - reuse it

            // Минимальный qty чтобы покрыть minNotional
            decimal minQtyForNotional = Math.Ceiling(minNotional / entry / step) * step;
            decimal effectiveMinQty = Math.Max(minQty, minQtyForNotional);

            // Поднимаем finalNotional до минимума если нужно
            decimal effectiveNotional = Math.Max(finalNotional, minNotional);

            // -----------------------------
            // Convert to qty
            // -----------------------------
            decimal rawQty = effectiveNotional / entry;

            if (step > 1 && entry < minNotional)
                step = Math.Max(0.00001m, entry / 10m);

            decimal qty = Math.Floor(rawQty / step) * step;

            // -----------------------------
            // Check minQty (включая minQty для покрытия minNotional)
            // -----------------------------
            if (qty < effectiveMinQty) qty = effectiveMinQty;

            // -----------------------------
            // Check minNotional — финальная проверка
            // -----------------------------
            decimal finalNotionalCheck = qty * entry;
            if (finalNotionalCheck < minNotional)
            {
                // Ещё раз поднимаем qty
                qty = Math.Ceiling(minNotional / entry / step) * step;
                finalNotionalCheck = qty * entry;

                if (finalNotionalCheck < minNotional)
                {
                    LastRejectReason = $"QTY_TOO_SMALL | qty={qty} minQty={effectiveMinQty} notional={finalNotionalCheck} minNotional={minNotional}";
                    _logger.LogWarning(
                        "[RISK] {symbol} {reason}",
                        signal.Symbol, LastRejectReason);
                    return 0;
                }
            }

            // Проверяем что не превышаем маржинальный cap
            decimal finalMargin = qty * entry / leverage;
            if (finalMargin > balance * trading.MaxMarginPercent && balance >= 50m)
            {
                // Обрезаем qty до cap
                decimal cappedNotional = balance * trading.MaxMarginPercent * leverage;
                qty = Math.Floor(cappedNotional / entry / step) * step;

                // Но не ниже минимума
                if (qty < effectiveMinQty)
                {
                    LastRejectReason = $"MARGIN_CAP_BELOW_MIN: cappedQty={qty} < effectiveMinQty={effectiveMinQty}";
                    return 0;
                }
            }

            // =============================================================
            // LIQUIDATION RISK PRE-TRADE CHECK
            // Проверяем: при данном qty безопасно ли открывать позицию?
            // Если SL слишком близко к цене ликвидации — корректируем qty.
            // =============================================================
            if (_liqRisk != null)
            {
                var liqCheck = _liqRisk.CheckPreTrade(signal, qty, balance, leverage);

                if (!liqCheck.IsAllowed)
                {
                    LastRejectReason = $"LIQ_RISK_BLOCKED: {liqCheck.BlockReason}";
                    _logger.LogWarning(
                        "[RISK] {symbol} BLOCKED by liquidation risk: {reason}",
                        signal.Symbol, liqCheck.BlockReason);
                    return 0;
                }

                // Если qty был скорректирован — используем безопасный
                if (liqCheck.SafeQty < qty && liqCheck.SafeQty > 0)
                {
                    _logger.LogInformation(
                        "[RISK] {symbol} qty adjusted by liq risk: {orig:F4}→{safe:F4} liqPrice={liq:F4} buffer={buf:P2} mmr={mmr}",
                        signal.Symbol, qty, liqCheck.SafeQty,
                        liqCheck.LiquidationPrice, liqCheck.LiqBufferPct, liqCheck.MmrTier);

                    qty = Math.Floor(liqCheck.SafeQty / step) * step;
                    if (qty < minQty) qty = minQty;
                }
                else
                {
                    _logger.LogDebug(
                        "[RISK] {symbol} liqPrice={liq:F4} buffer={buf:P2} tier={mmr} — OK",
                        signal.Symbol,
                        liqCheck.LiquidationPrice,
                        liqCheck.LiqBufferPct,
                        liqCheck.MmrTier);
                }
            }

            return qty;
        }

        private decimal GetDynamicBaseRisk(decimal balance)
        {
            // Raised from 2.5%/2.0% — at 19x leverage these were producing
            // notionals barely above MinNotional for typical balance ranges.
            if (balance <= 100m) return 0.035m;  // was 2.5%
            if (balance <= 500m) return 0.030m;  // was 2.0%
            if (balance <= 1000m) return 0.015m;
            if (balance <= 5000m) return 0.012m;
            if (balance <= 10000m) return 0.01m;
            return 0.0075m;
        }

        public async Task<decimal> GetRealtimeBalanceAsync(CancellationToken ct)
        {
            // ── Попытка 1: GetAccountInfoV3 (точный realtime) ────────────
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var client = _factory.CreateRestClient();
                    var account = await client.UsdFuturesApi.Account
                        .GetAccountInfoV3Async(ct: ct)
                        .ConfigureAwait(false);

                    if (account.Success && account.Data != null)
                    {
                        var free = account.Data.Assets
                            .FirstOrDefault(a => a.Asset == "USDT")?.AvailableBalance ?? 0m;
                        free = Math.Max(free, 0m);
                        LastBalanceUsdt = free;
                        _logger.LogInformation(
                            "[BALANCE] Fetched {bal:F4}$ USDT (attempt {a})",
                            free, attempt);
                        return free;
                    }

                    _logger.LogWarning(
                        "[BALANCE] GetAccountInfoV3 failed attempt {a}/3: {code} {msg}",
                        attempt,
                        account.Error?.Code,
                        account.Error?.Message);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[BALANCE] Exception on attempt {a}/3", attempt);
                }

                if (attempt < 3)
                    await Task.Delay(500 * attempt, ct);
            }

            // ── Попытка 2: GetFuturesAccountBalancesAsync (легче) ─────────
            try
            {
                var client2 = _factory.CreateRestClient();
                var balances = await client2.UsdFuturesApi.Account
                    .GetBalancesAsync(ct: ct)
                    .ConfigureAwait(false);

                if (balances.Success && balances.Data != null)
                {
                    var usdt = balances.Data.FirstOrDefault(b => b.Asset == "USDT");
                    if (usdt != null)
                    {
                        var free = Math.Max(usdt.AvailableBalance, 0m);
                        LastBalanceUsdt = free;
                        _logger.LogInformation(
                            "[BALANCE] Fallback GetBalances → {bal:F4}$", free);
                        return free;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[BALANCE] Fallback GetBalances also failed");
            }

            // ── Попытка 3: используем кэшированное значение ───────────────
            if (LastBalanceUsdt > 0)
            {
                _logger.LogWarning(
                    "[BALANCE] All API calls failed — using cached {bal:F4}$",
                    LastBalanceUsdt);
                return LastBalanceUsdt;
            }

            _logger.LogError("[BALANCE] Cannot determine balance — returning 0");
            return 0m;
        }

        /// <summary>
        /// Professional Kelly-fraction adaptive risk sizing.
        ///
        /// Formula: baseRisk × riskMult × confMult × regimeMult × liqMult
        ///          × volMult × aiMult × safetyMult × winRateMult
        ///
        /// Key changes from prior version:
        /// - confMult extended to 1.35× at ≥0.80 confidence (high conviction = size up)
        /// - IsSuperSignal: +25% boost on top of confMult
        /// - regimeMult: StrongTrend +15%, VolatileChop -30%
        /// - winRateMult range widened: 0.70×-1.20× (was 0.75×-1.15×)
        /// - clamp floor 0.5% (was 0.3%), ceil 6% (was 4%)
        ///   At 19× leverage, 6% risk = 6%×19=114% notional — hard-capped
        ///   by MaxMarginPercent downstream so this never over-exposes.
        /// </summary>
        private decimal CalculateAdaptiveRisk(
            TradeSignal signal,
            decimal baseRisk,
            decimal riskMult,
            decimal winRate = 0.5m)
        {
            decimal confidence = signal.Confidence ?? 0.60m;
            decimal liquidity  = signal.LiquidityScore ?? 0.80m;
            decimal aiQuality  = signal.AiQuality ?? 0.60m;
            decimal atr        = signal.Atr ?? 0m;
            decimal price      = signal.EntryPrice;

            decimal volatility = price > 0 && atr > 0 ? atr / price : 0.01m;

            // ── CONFIDENCE MULTIPLIER ────────────────────────────────────
            // Higher confidence = higher edge = scale up position.
            // ≥0.80 is the "high conviction" zone — meaningfully size up.
            decimal confMult =
                confidence < 0.40m ? 0.60m :   // very low — likely borderline signal
                confidence < 0.52m ? 0.80m :    // below MinEntry threshold zone
                confidence < 0.65m ? 1.00m :    // normal confidence
                confidence < 0.80m ? 1.18m :    // good confidence
                1.35m;                           // high conviction — full size up

            // Super-signal bonus: verified confluence of multiple timeframes
            if (signal.IsSuperSignal)
                confMult = Math.Min(confMult * 1.25m, 1.60m); // cap at 1.60×

            // ── REGIME MULTIPLIER ────────────────────────────────────────
            // Strong trend = higher momentum expectancy → size up.
            // Chop/range = noisy signals → size down.
            // TradeSignal.Reason encodes the regime context set by StrategyEngine
            // e.g. "PULLBACK_EMA21_STRONG_UP", "MEANREV_RANGE", "LIQ_GRAB_CHOP"
            var reason = (signal.Reason ?? string.Empty).ToUpperInvariant();
            decimal regimeMult =
                reason.Contains("STRONG_UP") || reason.Contains("STRONG_DOWN") ? 1.15m :
                reason.Contains("_UP")       || reason.Contains("_DOWN")       ? 1.05m :
                reason.Contains("CHOP")      || reason.Contains("VOLATILE")    ? 0.70m :
                reason.Contains("RANGE")     || reason.Contains("MEANREV")     ? 0.95m :
                1.00m;

            // ── LIQUIDITY MULTIPLIER ─────────────────────────────────────
            decimal liqMult =
                liquidity < 0.40m ? 0.60m :
                liquidity < 0.65m ? 0.82m :
                1.00m;

            // ── VOLATILITY MULTIPLIER ────────────────────────────────────
            // Very high ATR/price → position can get wiped on normal moves.
            // Very low ATR/price → quiet market, safe to size normally.
            decimal volMult =
                volatility > 0.040m ? 0.55m :   // extreme vol → very small
                volatility > 0.025m ? 0.78m :   // high vol → reduce
                volatility < 0.004m ? 1.12m :   // ultra-quiet → slight boost
                1.00m;

            // ── AI QUALITY MULTIPLIER ────────────────────────────────────
            decimal aiMult =
                aiQuality < 0.40m ? 0.80m :
                aiQuality > 0.72m ? 1.12m :
                1.00m;

            // ── SAFETY / OVERRIDE ────────────────────────────────────────
            decimal safety = signal.SafetyRiskMultiplier > 0
                ? signal.SafetyRiskMultiplier
                : 1.00m;

            if (signal.HighTfSafetyMode)     safety *= 0.70m;
            if (signal.LiquiditySoftWarning) safety *= 0.75m;

            // ── WIN RATE MULTIPLIER ──────────────────────────────────────
            // Widen range vs prior version — strong track record justifies
            // larger size; consistently poor win-rate signals degraded edge.
            decimal winRateMult =
                winRate < 0.38m ? 0.70m :   // very poor → -30%
                winRate < 0.44m ? 0.85m :   // below avg → -15%
                winRate > 0.68m ? 1.20m :   // strong → +20%
                winRate > 0.58m ? 1.08m :   // decent → +8%
                1.00m;

            decimal risk =
                baseRisk
                * riskMult
                * confMult
                * regimeMult
                * liqMult
                * volMult
                * aiMult
                * safety
                * winRateMult;

            // Floor 0.5%: never risk less than this — at $300 balance
            // and 19× leverage, 0.5% = $28.5 notional, still tradeable.
            // Ceil 6%: never exceed this — MaxMarginPercent will cap
            // actual exposure downstream. At 19× leverage, 6% risk
            // = 6/1% × SL%  which is bounded by the margin cap.
            return Math.Clamp(risk, 0.005m, 0.06m);
        }
    }
}


