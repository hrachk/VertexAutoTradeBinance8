using Binance.Net.Enums;
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
            TradingOptionsResolver tradingResolver
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
        }

        // 🔹 Динамический базовый риск по балансу
        private decimal GetDynamicBaseRisk(decimal balance)
        {
            if (balance < 100m) return 0.025m;
            if (balance < 300m) return 0.018m;
            if (balance < 500m) return 0.014m;
            if (balance < 2000m) return 0.010m;
            return 0.007m;
        }

        // 🔹 Универсальный расчет qty
        private decimal CalculateUniversalQty(
            decimal balance,
            decimal entry,
            decimal stop,
            decimal leverage,
            decimal riskPercent,
            decimal minNotional,
            decimal step,
            decimal minQty)
        {
            if (balance <= 0 || entry <= 0 || stop <= 0 || leverage <= 0)
                return 0;

            decimal slPercent = Math.Abs(entry - stop) / entry;
            if (slPercent <= 0)
                return 0;

            decimal riskBudget = balance * riskPercent;
            riskBudget = Math.Clamp(riskBudget, balance * 0.005m, balance * 0.20m);

            decimal rawNotional = riskBudget / slPercent;
            decimal maxNotional = balance * leverage * 0.98m;

            decimal finalNotional = Math.Max(rawNotional, minNotional);
            finalNotional = Math.Min(finalNotional, maxNotional);

            if (finalNotional <= 0)
                return 0;

            decimal qty = Math.Floor((finalNotional / entry) / step) * step;

            if (qty * entry < minNotional)
            {
                qty = Math.Ceiling((minNotional / entry) / step) * step;
            }
            if (qty < minQty) return 0;

            decimal requiredMargin = (qty * entry) / leverage;
            if (requiredMargin > balance) return 0;

            return qty;
        }

        // 🔹 Основной метод расчета безопасного qty
        public async Task<decimal> CalculateSafeQty(
            TradeSignal signal,
            string symbol,
            decimal entryPrice,
            decimal stopLoss,
            decimal riskMultiplier,
            decimal safetyRiskMultiplier,
            decimal leverage,
            SignalSide side,
            List<decimal> takeProfits,
            CancellationToken ct)
        {
            var trading = _tradingResolver.Resolve(symbol);
            leverage = trading.Leverage > 0 ? trading.Leverage : (signal.Leverage ?? 1m);
            LastRejectReason = null;

            if (entryPrice <= 0 || stopLoss <= 0)
            {
                LastRejectReason = "InvalidEntryOrStop";
                signal.RejectReason = LastRejectReason;
                _logger.LogWarning("[RISK][{Symbol}] Entry or Stop invalid: entry={Entry} stop={Stop}", symbol, entryPrice, stopLoss);
                return 0;
            }

            var f = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            decimal step = f.step > 0 ? f.step : 0.001m;
            decimal minQty = f.minQty > 0 ? f.minQty : step;
            decimal minNotional = f.minNotional > 0 ? f.minNotional : (trading.MinNotionalGuard > 0 ? trading.MinNotionalGuard : 5m);

            decimal free = await GetRealtimeBalanceAsync(ct);
            if (free <= 0)
            {
                LastRejectReason = "NoBalance";
                signal.RejectReason = LastRejectReason;
                _logger.LogWarning("[RISK][{Symbol}] Free balance 0", symbol);
                return 0;
            }

            decimal binanceMinNotional = minNotional;
            if (trading.MinNotional > 0)
                binanceMinNotional = Math.Max(binanceMinNotional, trading.MinNotional);

            if (trading.MinNotionalGuardPercent > 0)
            {
                decimal guardValue = (decimal?)trading.MinNotionalGuard ?? 0m;
                decimal dynMin = Math.Max(guardValue, free * trading.MinNotionalGuardPercent);
                binanceMinNotional = Math.Max(binanceMinNotional, dynMin);
            }

            // 🔹 1️⃣ Базовый риск
            decimal baseRiskPercent =
                trading.RiskPerTrade > 0
                    ? (decimal)trading.RiskPerTrade
                    : trading.BaseRiskPercent > 0
                        ? trading.BaseRiskPercent / 100m
                        : 0.01m;

            // 🔹 2️⃣ Сигнальная сила
            decimal score = riskMultiplier * safetyRiskMultiplier;
            bool strong = score >= 1.30m;
            bool weak = score < 0.80m;

            // 🔹 3️⃣ AI leverage multiplier
            decimal aiLevMult = await GetAiLeverageMultiplierAsync(symbol, ct);

            // 🔹 4️⃣ Финальный риск с учетом AI
            decimal finalRisk = riskMultiplier * safetyRiskMultiplier * aiLevMult;
            finalRisk = Math.Clamp(finalRisk, 0.3m, 2.7m);

            // 🔹 5️⃣ Максимальный риск позиции
            decimal maxRisk = free * baseRiskPercent * finalRisk;

            if (weak)
            {
                _logger.LogInformation($"[RISK][{symbol}] Weak signal detected — lowering position size.");
                maxRisk *= 0.35m;
            }

            maxRisk = Math.Clamp(maxRisk, free * 0.005m, free * 0.20m);

            // 🔹 6️⃣ Расчет qty через универсальный метод
            decimal trueRiskPercent = baseRiskPercent * finalRisk;

            decimal qty = CalculateUniversalQty(
                free,
                entryPrice,
                stopLoss,
                leverage,
                trueRiskPercent,
                binanceMinNotional,
                step,
                minQty
            );

            decimal notional = qty * entryPrice;
            decimal requiredMargin = notional / leverage;

            // 🔹 7️⃣ Adaptive adjustment
            if (notional < binanceMinNotional || requiredMargin > free)
            {
                decimal maxAllowedNotional = free * leverage * 0.97m;
                decimal targetNotional = Math.Max(binanceMinNotional, Math.Min(notional * 1.4m, maxAllowedNotional));

                for (int i = 0; i < 12; i++)
                {
                    qty = Math.Floor((targetNotional / entryPrice) / step) * step;

                    if (qty * entryPrice < binanceMinNotional)
                    {
                        qty = Math.Ceiling((binanceMinNotional / entryPrice) / step) * step;
                    }

                    notional = qty * entryPrice;
                    requiredMargin = notional / leverage;

                    if (notional >= binanceMinNotional && requiredMargin <= free) break;

                    targetNotional *= 0.85m;
                }

                if (notional < binanceMinNotional || requiredMargin > free || qty < minQty)
                {
                    LastRejectReason = "MinNotionalAfterAdaptiveReduce";
                    signal.RejectReason = LastRejectReason;
                    return 0;
                }
            }

            // 🔹 8️⃣ Финальная safety проверка
            decimal maxPossibleQty = Math.Floor((free * leverage / entryPrice) / step) * step;
            if (maxPossibleQty < minQty)
            {
                LastRejectReason = "FinalSafetyQtyTooLow";
                signal.RejectReason = LastRejectReason;
                return 0;
            }

            _logger.LogInformation(
                "[RISK][FINAL] {Symbol} {Side} | risk={Risk:P2} lev={Lev} | qty={Qty} notional={Notional:F2} margin={Margin:F2} | minNotional={MinNotional:F2}",
                symbol,
                side,
                finalRisk,
                leverage,
                qty,
                notional,
                requiredMargin,
                binanceMinNotional
            );

            signal.RejectReason = "RISK_OK";
            return qty;
        }

        // 🔹 AI Leverage Multiplier
        private async Task<decimal> GetAiLeverageMultiplierAsync(string symbol, CancellationToken ct)
        {
            try
            {
                var klines = await _marketData.GetKlines(symbol, KlineInterval.FifteenMinutes, 200);
                if (klines == null || klines.Count < 30) return 1.0m;
                decimal m = _aiLeverage.Calculate(symbol, KlineInterval.FifteenMinutes, klines);
                return m > 0 ? m : 1.0m;
            }
            catch
            {
                _logger.LogWarning("[RISK][AI] Failed to get AI leverage for {Symbol}", symbol);
                return 1.0m;
            }
        }

        // 🔹 Получение актуального баланса
        public async Task<decimal> GetRealtimeBalanceAsync(CancellationToken ct)
        {
            var account = await _factory
                .CreateRestClient()
                .UsdFuturesApi.Account
                .GetAccountInfoV3Async(ct: ct);

            if (!account.Success || account.Data == null) return 0m;

            var free = account.Data.Assets
                .FirstOrDefault(a => a.Asset == "USDT")?
                .AvailableBalance ?? 0m;

            LastBalanceUsdt = free;
            return free;
        }

        // 🔹 Получение qty через propDesk (обертка над universal)
        public decimal GetPropDeskQty(
            TradeSignal signal,
            decimal balance,
            decimal minNotional,
            decimal step,
            decimal minQty,
            decimal riskMult,
            TradingOptions trading)
        {
            decimal leverage = trading.Leverage > 0 ? trading.Leverage : (signal.Leverage ?? 1m);
            decimal dynamicRisk = GetDynamicBaseRisk(balance);
            decimal finalRisk = dynamicRisk * riskMult * (signal.SafetyRiskMultiplier > 0 ? signal.SafetyRiskMultiplier : 1m);

            return CalculateUniversalQty(
                balance,
                signal.EntryPrice,
                signal.StopLoss,
                leverage,
                finalRisk,
                minNotional,
                step,
                minQty
            );
        }
    }
}