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

        public int GetPrecision(decimal step)
        {
            step = step.Normalize(); // убираем хвосты типа 0.0100000

            int[] bits = decimal.GetBits(step);
            int scale = (bits[3] >> 16) & 0x7F;
            return scale;
        }

        public decimal GetPropDeskQty(
           TradeSignal signal,
           decimal balance,
           decimal minNotional,
           decimal step,
           decimal minQty,
           decimal riskMult,
           TradingOptions trading)
        {
            if (signal == null || balance <= 0 || step <= 0)
                return 0;

            decimal leverage = trading.Leverage > 0 ? trading.Leverage : (signal.Leverage ?? 1m);

            // 🔹 динамический риск на баланс
            decimal dynamicRisk = GetDynamicBaseRisk(balance);
            decimal finalRisk = dynamicRisk * riskMult * (signal.SafetyRiskMultiplier > 0 ? signal.SafetyRiskMultiplier : 1m);

            // 🔹 1️⃣ Расчёт qty через улучшенный CalculateUniversalQty
            decimal qty = CalculateUniversalQty(balance, signal.EntryPrice, signal.StopLoss, leverage, finalRisk, minNotional, step);

            // 🔹 2️⃣ Гарантия minQty
            if (qty < minQty)
                qty = minQty;

            // 🔹 3️⃣ Поднимаем qty, если notional < minNotional
            decimal notional = qty * signal.EntryPrice;
            if (notional < minNotional)
                qty = minNotional / signal.EntryPrice;

            // 🔹 4️⃣ Итоговое округление по step
            qty = Math.Ceiling(qty / step) * step;

            return qty > 0 ? qty : 0;
        }

        private decimal CalculateUniversalQty(
            decimal balance,
            decimal entry,
            decimal stop,
            decimal leverage,
            decimal riskPercent,
            decimal minNotional,
            decimal step)
        {
            if (balance <= 0 || entry <= 0 || stop <= 0 || leverage <= 0 || step <= 0)
                return 0;

            decimal slPercent = Math.Abs(entry - stop) / entry;
            if (slPercent <= 0)
                return 0;

            // 🔹 Risk budget
            decimal riskBudget = balance * riskPercent;
            riskBudget = Math.Clamp(riskBudget, balance * 0.005m, balance * 0.20m);

            // 🔹 Ограничение по плечу
            decimal maxNotional = balance * leverage * 0.98m;

            // 🔹 Выбираем минимальный notional
            decimal finalNotional = Math.Min(riskBudget / slPercent, maxNotional);
            finalNotional = Math.Max(finalNotional, minNotional);

            // 🔥 Boost для дешёвых монет
            decimal priceBoost = 1m;

            if (entry < 0.05m)
                priceBoost = 3m;
            else if (entry < 0.50m)
                priceBoost = 2m;
            else if (entry < 5m)
                priceBoost = 1.5m;

            finalNotional *= priceBoost;

            decimal qty = finalNotional / entry;

            // 🔹 Расчёт qty
              qty = finalNotional / entry;

            return qty > 0 ? qty : 0;
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

       
    }
}