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

        private const decimal MaxMarginPercent = 0.12m; // 12% hard cap margin

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

        public decimal GetPropDeskQtyFinal(
            TradeSignal signal,
            decimal balance,
            decimal step,
            decimal minQty,
            decimal riskMult,
            TradingOptions trading)
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

            decimal leverage = trading.Leverage > 0 ? trading.Leverage : (signal.Leverage ?? 1m);
            if (leverage <= 0)
            {
                LastRejectReason = "Invalid leverage";
                return 0;
            }

            // -----------------------------
            // STOP % и базовый риск
            // -----------------------------
            decimal slDistance = Math.Abs(entry - stop);
            const decimal MinSlPercent = 0.002m; // 0.2%
            decimal slPercent = Math.Max(slDistance / entry, MinSlPercent);

            if (slDistance / entry < MinSlPercent)
                _logger.LogWarning("[RISK] SL too close for {symbol}, forcing min {MinSlPercent:P2}", signal.Symbol, MinSlPercent);

            // -----------------------------
            // BASE RISK + Safety Multiplier
            // -----------------------------
            decimal baseRisk = trading.RiskPerTrade > 0
                ? (decimal)trading.RiskPerTrade
                : GetDynamicBaseRisk(balance);

            decimal safetyMult = signal.SafetyRiskMultiplier > 0 ? signal.SafetyRiskMultiplier : 1m;
            decimal finalRisk =
     CalculateAdaptiveRisk(signal, baseRisk, riskMult);

            // =============================================================
            // AI WINRATE ADJUSTMENT
            // =============================================================
            var winRate = _ai.GetWinRate(signal.Side);

            if (winRate < 0.45m)
                finalRisk *= 0.7m;
            else if (winRate > 0.60m)
                finalRisk *= 1.2m;


            if (finalRisk <= 0)
            {
                LastRejectReason = "Final risk <= 0";
                return 0;
            }

            decimal riskBudget = balance * finalRisk;

            // -----------------------------
            // Notional calculation
            // -----------------------------
            decimal riskNotional = riskBudget / slPercent;
            decimal leverageCapNotional = balance * leverage * 0.98m;
            
            decimal marginCapNotional =
    balance < 50
        ? balance * leverage   // 🔥 отключаем ограничение
        : balance * MaxMarginPercent * leverage;

            decimal finalNotional = Math.Min(riskNotional, Math.Min(leverageCapNotional, marginCapNotional));
            
            if (finalNotional <= 0)
            {
                LastRejectReason = "Final notional <= 0";
                return 0;
            }

            // =============================================================
            // ADAPTIVE MINNOTIONAL — правильная логика
            //
            // Binance требует minNotional (из фильтров символа, обычно 5$).
            // Старый код: minNotionalAdaptive = Math.Min(minNotional, entry*minQty)
            // Для дешёвых монет (price=0.317): Math.Min(5, 0.317) = 0.317 → НЕВЕРНО
            //
            // Правильно: minNotional из конфига = РЕАЛЬНЫЙ минимум Binance.
            // Если finalNotional < minNotional → поднимаем qty до минимума.
            // Если даже минимальный qty даёт notional > maxAllowed → reject.
            // =============================================================
            decimal minNotional = trading.MinNotional > 0 ? trading.MinNotional : 5m;

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
            if (finalMargin > balance * MaxMarginPercent && balance >= 50m)
            {
                // Обрезаем qty до cap
                decimal cappedNotional = balance * MaxMarginPercent * leverage;
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
            if (balance <= 100m) return 0.025m;
            if (balance <= 500m) return 0.02m;
            if (balance <= 1000m) return 0.015m;
            if (balance <= 5000m) return 0.012m;
            if (balance <= 10000m) return 0.01m;
            return 0.0075m;
        }

        public async Task<decimal> GetRealtimeBalanceAsync(CancellationToken ct)
        {
            try
            {
                var client = _factory.CreateRestClient();
                var account = await client.UsdFuturesApi.Account.GetAccountInfoV3Async(ct: ct).ConfigureAwait(false);

                if (!account.Success || account.Data == null)
                {
                    _logger.LogWarning("GetRealtimeBalanceAsync: Failed to fetch account info. Success={Success}", account.Success);
                    return 0m;
                }

                var free = account.Data.Assets.FirstOrDefault(a => a.Asset == "USDT")?.AvailableBalance ?? 0m;
                free = Math.Max(free, 0m);
                LastBalanceUsdt = free;
                return free;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetRealtimeBalanceAsync: Exception while fetching balance");
                return 0m;
            }
        }

        private decimal CalculateAdaptiveRisk(
    TradeSignal signal,
    decimal baseRisk,
    decimal riskMult)
        {
            decimal confidence = signal.Confidence ?? 0.6m;
            decimal liquidity = signal.LiquidityScore ?? 0.8m;
            decimal aiQuality = signal.AiQuality ?? 0.6m;

            decimal atr = signal.Atr ?? 0m;
            decimal price = signal.EntryPrice;

            decimal volatility =
                price > 0 && atr > 0
                ? atr / price
                : 0.01m;

            // -------------------------
            // CONFIDENCE
            // -------------------------

            decimal confMult =
                confidence < 0.4m ? 0.7m :
                confidence < 0.6m ? 0.9m :
                confidence < 0.8m ? 1.0m :
                1.15m;

            // -------------------------
            // LIQUIDITY
            // -------------------------

            decimal liqMult =
                liquidity < 0.4m ? 0.6m :
                liquidity < 0.7m ? 0.8m :
                1.0m;

            // -------------------------
            // VOLATILITY
            // -------------------------

            decimal volMult =
                volatility > 0.035m ? 0.6m :
                volatility > 0.02m ? 0.8m :
                volatility < 0.005m ? 1.1m :
                1m;

            // -------------------------
            // AI QUALITY
            // -------------------------

            decimal aiMult =
                aiQuality < 0.4m ? 0.8m :
                aiQuality > 0.7m ? 1.1m :
                1m;

            // -------------------------
            // SAFETY
            // -------------------------

            decimal safety =
                signal.SafetyRiskMultiplier > 0
                ? signal.SafetyRiskMultiplier
                : 1m;

            if (signal.HighTfSafetyMode)
                safety *= 0.7m;

            if (signal.LiquiditySoftWarning)
                safety *= 0.75m;

            decimal risk =
                baseRisk
                * riskMult
                * confMult
                * liqMult
                * volMult
                * aiMult
                * safety;

            return Math.Clamp(risk, 0.002m, 0.05m);
        }
    }
}