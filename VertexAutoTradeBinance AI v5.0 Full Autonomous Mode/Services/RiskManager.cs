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

        // 🔹 Динамический базовый риск по балансу (адаптивно)
        private decimal GetDynamicBaseRisk(decimal balance)
        {
            if (balance <= 100m) return 0.025m;   // 2.5% для мелких депозитов
            if (balance <= 500m) return 0.02m;    // 2%
            if (balance <= 1000m) return 0.015m;  // 1.5%
            if (balance <= 5000m) return 0.012m;  // 1.2%
            if (balance <= 10000m) return 0.01m;  // 1%
            return 0.0075m;                        // 0.75% для очень больших депозитов
        }

        // 🔹 Основной метод расчёта qty
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

            // ✅ используем плечо из config, если оно больше 0, иначе берем из сигнала
            decimal leverage = trading.Leverage > 0 ? trading.Leverage : (signal.Leverage ?? 1m);

            // 🔹 динамический риск
            decimal dynamicRisk = GetDynamicBaseRisk(balance);
            decimal finalRisk = dynamicRisk * riskMult * (signal.SafetyRiskMultiplier > 0 ? signal.SafetyRiskMultiplier : 1m);

            // 🔹 расчёт qty через универсальный метод
            decimal qty = CalculateUniversalQty(balance, signal.EntryPrice, signal.StopLoss, leverage, finalRisk, minNotional, step);

            // 🔹 гарантия минимального qty
            if (qty < minQty) qty = minQty;

            // 🔹 определяем symbol-specific минимальный notional
            decimal symbolMinNotional = minNotional;
            if (_symbolMinNotional.TryGetValue(signal.Symbol, out var specificMin))
                symbolMinNotional = Math.Max(minNotional, specificMin);

            // 🔹 гарантируем, что notional >= minNotional для конкретного символа
            decimal notional = qty * signal.EntryPrice;
            if (notional < symbolMinNotional) qty = symbolMinNotional / signal.EntryPrice;

            // 🔹 итоговое округление по step
            qty = Math.Floor(qty / step) * step;

            return qty > 0 ? qty : 0;
        }

        // 🔹 Универсальный расчёт qty
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

            // 🔹 1. Процент потери на стоп
            decimal slPercent = Math.Abs(entry - stop) / entry;
            if (slPercent <= 0) return 0;

            // 🔹 2. Динамический riskBudget: минимум 0.5%, максимум 20% от баланса
            decimal riskBudget = balance * riskPercent;
            riskBudget = Math.Clamp(riskBudget, balance * 0.005m, balance * 0.20m);

            // 🔹 3. Максимальный notional с плечом
            decimal maxNotional = balance * leverage * 0.98m;

            // 🔹 4. Основной notional через риск и стоп
            decimal finalNotional = riskBudget / slPercent;

            // 🔹 5. Не превышаем плечо
            finalNotional = Math.Min(finalNotional, maxNotional);

            // 🔹 6. Обеспечиваем минимальный notional биржи
            finalNotional = Math.Max(finalNotional, minNotional);

            // 🔹 7. Scaling для дешёвых монет, чтобы позиции были реальными
            // (чем дешевле монета, тем чуть больше позиция, но без перебора)
            if (entry < 0.05m) finalNotional *= 2m;        // очень дешёвые монеты
            else if (entry < 0.5m) finalNotional *= 1.5m;  // дешёвые монеты
            else if (entry < 5m) finalNotional *= 1.2m;    // средние монеты
                                                           // для дорогих монет (>5$) boost не нужен

            // 🔹 8. Рассчитываем qty
            decimal qty = finalNotional / entry;

            // 🔹 9. Округляем по шагу
            qty = Math.Floor(qty / step) * step;

            return qty > 0 ? qty : 0;
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
        // 🔹 Получение актуального плеча для конкретного символа
        private async Task<decimal> GetSymbolLeverageAsync(string symbol, decimal configLeverage, CancellationToken ct)
        {
            try
            {
                var client = _factory.CreateRestClient();
                var info = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, ct:ct);

                if (info.Success && info.Data != null && info.Data.Any())
                {
                    var position = info.Data.FirstOrDefault();
                    if (position != null && position.Leverage > 0)
                        return position.Leverage; // динамическое плечо с биржи
                }
            }
            catch
            {
                // fallback
            }

            return configLeverage; // плечо из конфига если API не доступен
        }


    }
}