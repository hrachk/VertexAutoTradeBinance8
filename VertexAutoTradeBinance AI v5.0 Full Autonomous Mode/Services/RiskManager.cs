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

        // 🔹 Словарь минимального notional по символам
        private readonly Dictionary<string, decimal> _symbolMinNotional = new()
{
    { "BTCUSDT", 100m },
    { "ETHUSDT", 20m },
    { "BNBUSDT", 10m },
    // добавь мелкие монеты, например:
    { "ADAUSDT", 5m },
    { "XRPUSDT", 5m },
    { "DOGEUSDT", 5m }
};
        public int GetPrecision(decimal step)
        {
            step = step.Normalize(); // убираем хвосты типа 0.0100000

            int[] bits = decimal.GetBits(step);
            int scale = (bits[3] >> 16) & 0x7F;
            return scale;
        }

        // 🔹 Финальный метод расчёта qty
        public decimal GetPropDeskQtyFinal(
        TradeSignal signal,
        decimal balance,
        decimal step,
        decimal minQty,
        decimal riskMult,
        TradingOptions trading)
        {
            if (signal == null || balance <= 0 || step <= 0)
                return 0;

            // Плечо
            decimal leverage = trading.Leverage > 0 ? trading.Leverage : (signal.Leverage ?? 1m);

            // Динамический базовый риск
            decimal dynamicRisk = GetDynamicBaseRisk(balance);
            decimal finalRisk = dynamicRisk * riskMult * (signal.SafetyRiskMultiplier > 0 ? signal.SafetyRiskMultiplier : 1m);

            // Symbol-specific minNotional (с safety минимальным $1)
            decimal minNotional = _symbolMinNotional.TryGetValue(signal.Symbol, out var sMin)
                ? Math.Max(1m, sMin)
                : 5m;

            // Max notional по плечу
            decimal maxNotional = balance * leverage * 0.98m;

            // 1️⃣ Risk-based notional
            decimal riskBudget = balance * finalRisk;
            decimal riskNotional = riskBudget / Math.Max(Math.Abs(signal.EntryPrice - signal.StopLoss), 0.00001m);

            // 2️⃣ Сразу учитываем minNotional
            decimal finalNotional = Math.Max(riskNotional, minNotional);

            // 3️⃣ Boost для дешёвых монет
            if (signal.EntryPrice < 0.05m)
                finalNotional = Math.Min(finalNotional * 2m, maxNotional);
            else if (signal.EntryPrice < 0.5m)
                finalNotional = Math.Min(finalNotional * 1.5m, maxNotional);
            else if (signal.EntryPrice < 5m)
                finalNotional = Math.Min(finalNotional * 1.2m, maxNotional);

            // 4️⃣ Ограничение плечом
            finalNotional = Math.Min(finalNotional, maxNotional);

            // 5️⃣ Переводим в qty и округляем вверх
            decimal qty = Math.Ceiling(finalNotional / signal.EntryPrice / step) * step;

            // 6️⃣ Гарантия minQty
            qty = Math.Max(qty, Math.Ceiling(minQty / step) * step);

            // 7️⃣ Финальная проверка notional
            decimal finalCheckNotional = qty * signal.EntryPrice;
            if (finalCheckNotional < minNotional)
                return 0;

            return qty;
        }

        // 🔹 Универсальный расчёт qty
        private decimal CalculateUniversalQtyFinal(
            decimal balance,
            decimal entry,
            decimal stop,
            decimal leverage,
            decimal riskPercent,
            decimal minNotional,
            decimal step,
            decimal minQty)
        {
            if (balance <= 0 || entry <= 0 || stop <= 0 || leverage <= 0 || step <= 0)
                return 0;

            // 1️⃣ Процент до стопа
            decimal slPercent = Math.Abs(entry - stop) / entry;
            if (slPercent <= 0) return 0;

            // 2️⃣ Risk-based notional
            decimal riskBudget = balance * riskPercent;
            decimal riskNotional = riskBudget / slPercent;

            // 3️⃣ Max notional по плечу
            decimal maxNotional = balance * leverage * 0.98m;

            // 4️⃣ Итоговый notional = min(riskNotional, maxNotional)
            decimal finalNotional = Math.Min(riskNotional, maxNotional);

            // 5️⃣ Boost для дешёвых монет
            if (entry < 0.05m) finalNotional = Math.Min(finalNotional * 2m, maxNotional);
            else if (entry < 0.5m) finalNotional = Math.Min(finalNotional * 1.5m, maxNotional);
            else if (entry < 5m) finalNotional = Math.Min(finalNotional * 1.2m, maxNotional);

            // 6️⃣ Проверка minNotional
            if (finalNotional < minNotional)
                return 0; // физически невозможно открыть позицию

            // 7️⃣ Перевод в qty и округление вверх
            decimal qty = Math.Ceiling(finalNotional / entry / step) * step;

            // 8️⃣ Гарантия minQty
            qty = Math.Max(qty, Math.Ceiling(minQty / step) * step);

            // 9️⃣ Финальная проверка notional
            decimal finalCheckNotional = qty * entry;
            if (finalCheckNotional < minNotional)
                return 0;

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