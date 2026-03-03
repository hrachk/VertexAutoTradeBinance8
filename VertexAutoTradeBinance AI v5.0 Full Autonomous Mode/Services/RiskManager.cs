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

        public string? LastRejectReason { get; private set; }
        public decimal LastBalanceUsdt { get; private set; }
        private const decimal MaxMarginPercent = 0.12m; // 12% жёсткий лимит маржи
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

   
        public int GetPrecision(decimal step)
        {
            step = step.Normalize(); // убираем хвосты типа 0.0100000

            int[] bits = decimal.GetBits(step);
            int scale = (bits[3] >> 16) & 0x7F;
            return scale;
        }

        // 🔹  метод расчёта qty
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
            // BASIC VALIDATION
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

            decimal leverage = trading.Leverage > 0
                ? trading.Leverage
                : (signal.Leverage ?? 1m);

            if (leverage <= 0)
            {
                LastRejectReason = "Invalid leverage";
                return 0;
            }

            // -----------------------------
            // STOP %
            // -----------------------------
            decimal slPercent = Math.Abs(entry - stop) / entry;
            if (slPercent <= 0)
            {
                LastRejectReason = "Invalid SL distance";
                return 0;
            }

            // -----------------------------
            // BASE RISK (CONFIG FIRST)
            // -----------------------------
            decimal baseRisk = trading.RiskPerTrade > 0
                ? (decimal)trading.RiskPerTrade
                : GetDynamicBaseRisk(balance);

            decimal safetyMult = signal.SafetyRiskMultiplier > 0
                ? signal.SafetyRiskMultiplier
                : 1m;

            decimal finalRisk = baseRisk * riskMult * safetyMult;

            // 🔒 HARD CLAMP (never allow >5% risk)
            finalRisk = Math.Min(finalRisk, 0.05m);

            if (finalRisk <= 0)
            {
                LastRejectReason = "Final risk <= 0";
                return 0;
            }

            // -----------------------------
            // RISK NOTIONAL
            // -----------------------------
            decimal riskBudget = balance * finalRisk;
            decimal riskNotional = riskBudget / slPercent;

            // -----------------------------
            // LEVERAGE CAP
            // -----------------------------
            decimal leverageCapNotional = balance * leverage * 0.98m;

            // -----------------------------
            // MARGIN CAP (12% HARD LIMIT)
            // -----------------------------
            decimal marginCapNotional = balance * MaxMarginPercent * leverage;

            // -----------------------------
            // FINAL NOTIONAL
            // -----------------------------
            decimal finalNotional = Math.Min(riskNotional, leverageCapNotional);
            finalNotional = Math.Min(finalNotional, marginCapNotional);

            // -----------------------------
            // MIN NOTIONAL CHECK
            // -----------------------------
            decimal minNotional = trading.MinNotional > 0
                ? trading.MinNotional
                : 10m;

            // -----------------------------
            // ENSURE MIN NOTIONAL (ADAPTIVE)
            // -----------------------------
            decimal effectiveNotional = finalNotional;

            if (effectiveNotional < minNotional)
            {
                // Разрешаем минимальный вход,
                // если реальный риск не превысит 5% баланса

                decimal minRiskNotional = minNotional;
                decimal requiredRisk = (minRiskNotional * slPercent) / balance;

                if (requiredRisk <= 0.05m) // 5% hard cap
                {
                    effectiveNotional = minNotional;
                }
                else
                {
                    LastRejectReason =
                        $"MinNotional requires too much risk: requiredRisk={requiredRisk:P2}";
                    return 0;
                }
            }

            // -----------------------------
            // CONVERT TO QTY (STRICT FLOOR + DYNAMIC MIN NOTIONAL ADJUSTMENT)
            // -----------------------------
            decimal rawQty = effectiveNotional / entry;
            decimal qty = Math.Floor(rawQty / step) * step;

            // Проверка минимального шага биржи
            if (qty < minQty)
            {
                LastRejectReason = $"Qty below min step: qty={qty} minQty={minQty}";
                return 0;
            }

            // Финальная проверка minNotional
            if (qty * entry < minNotional)
            {
                // Попробуем поднять qty до минимального шага, чтобы пройти minNotional
                decimal minStepQty = Math.Ceiling(minNotional / entry / step) * step;

                // Используем effectiveNotional как верхнюю границу
                if (minStepQty * entry <= effectiveNotional)
                    qty = minStepQty;
                else
                {
                    LastRejectReason = $"Final qty too small even after bump: qty={qty} notional={qty * entry:F8} minNotional={minNotional}";
                    return 0;
                }
            }

            // Дополнительно проверка минимального шага биржи
            if (qty < minQty)
            {
                LastRejectReason = $"Qty below min step: qty={qty} minQty={minQty}";
                return 0;
            }

            return qty;
        }



        // 🔹 Динамический базовый риск (адаптивно под баланс)
        private decimal GetDynamicBaseRisk(decimal balance)
        {
            if (balance <= 100m) return 0.025m;   // 2.5% для мелких депозитов
            if (balance <= 500m) return 0.02m;    // 2%
            if (balance <= 1000m) return 0.015m;  // 1.5%
            if (balance <= 5000m) return 0.012m;  // 1.2%
            if (balance <= 10000m) return 0.01m;  // 1%
            return 0.0075m;                        // 0.75% для больших депозитов
        }

        // 🔹 Получение актуального баланса
        public async Task<decimal> GetRealtimeBalanceAsync(CancellationToken ct)
        {
            try
            {
                var client = _factory.CreateRestClient();
                var account = await client.UsdFuturesApi.Account.GetAccountInfoV3Async(ct: ct)
                    .ConfigureAwait(false);

                if (!account.Success || account.Data == null)
                {
                    _logger.LogWarning("GetRealtimeBalanceAsync: Failed to fetch account info. Success={Success}", account.Success);
                    return 0m;
                }

                var free = account.Data.Assets
                    .FirstOrDefault(a => a.Asset == "USDT")?
                    .AvailableBalance ?? 0m;

                // Safety clamp
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
       
    }
}