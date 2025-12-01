using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class RiskManager
    {
        private readonly ILogger<RiskManager> _logger;
        private readonly TradingOptions _options;
        private readonly SymbolInfoService _symbolInfoService;
        private readonly BinanceClientFactory _factory;
        private readonly AiSelfLearningService _aiLearning;

        public RiskManager(
            ILogger<RiskManager> logger,
            IOptions<TradingOptions> options,
            SymbolInfoService symbolInfoService,
            BinanceClientFactory factory,
            AiSelfLearningService aiLearning)
        {
            _logger = logger;
            _options = options.Value;
            _symbolInfoService = symbolInfoService;
            _factory = factory;
            _aiLearning = aiLearning;
        }

        private async Task<(decimal deposit, decimal available)> GetCurrentDepositAsync()
        {
            try
            {
                using var client = _factory.CreateRestClient();
                var acc = await client.UsdFuturesApi.Account.GetAccountInfoV3Async();

                if (acc.Success && acc.Data != null)
                {
                    var usdt = acc.Data.Assets.FirstOrDefault(a => a.Asset == "USDT");
                    if (usdt != null)
                        return (usdt.WalletBalance, usdt.AvailableBalance);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Risk: failed to load futures balance");
            }

            // fallback на конфиг, если API не ответил
            var cfg = (decimal)_options.Deposit;
            return (cfg, cfg * 0.9m);
        }

        public Task<decimal> CalculateQuantityAsync(
            string symbol,
            decimal entryPrice,
            decimal stopLoss)
        {
            return CalculateQuantityAsync(symbol, entryPrice, stopLoss, 1m);
        }

        public async Task<decimal> CalculateQuantityAsync(
            string symbol,
            decimal entryPrice,
            decimal stopLoss,
            decimal baseRiskMultiplier)
        {
            // базовая валидация
            if (entryPrice <= 0 || stopLoss <= 0 || entryPrice == stopLoss)
                return 0m;

            var (deposit, available) = await GetCurrentDepositAsync();
            if (available <= 0)
                return 0m;

            // ===== 1) AI-вес риска =====
            decimal aiWeight = _aiLearning.GetDynamicRiskWeight(
                symbol,
                MarketRegime.Unknown); // StrategyEngine подставит реальный режим, но здесь дефолт

            decimal riskMultiplier = baseRiskMultiplier * aiWeight;

            // реальное плечо из опций, но для расчёта ставим верхний потолок,
            // чтобы не упираться в маржу на Binance
            var cfgLev = _options.Leverage > 0 ? (decimal)_options.Leverage : 1m;

            // для расчёта маржи берём ещё более консервативное плечо
            // (например, если cfgLev = 30, считаем как 15)
            var effLevForMargin = Math.Min(cfgLev, 15m);

            // ===== 2) Базовый риск в USDT =====
            var baseRiskUsd = available * (decimal)_options.RiskPerTrade;
            var riskUsd = baseRiskUsd * riskMultiplier;

            // Защита от слишком маленького/большого риска
            if (riskUsd <= 0)
                return 0m;

            // ===== 3) Дистанция до стопа =====
            var slDist = Math.Abs(entryPrice - stopLoss);
            if (slDist <= 0)
                return 0m;

            // Кол-во по риску (сколько можно потерять до SL)
            var qtyByRisk = riskUsd / slDist;

            if (qtyByRisk <= 0)
                return 0m;

            // ===== 4) Ограничение по балансу и плечу =====
            // Теоретически макс. нотионал по доступной марже и плечу
            // но с очень жёстким коэффициентом safety 0.4
            var maxNotionalTotal = available * effLevForMargin * 0.40m;
            var maxQtyByBalance = maxNotionalTotal / entryPrice;

            var rawQty = Math.Min(qtyByRisk, maxQtyByBalance);
            if (rawQty <= 0)
                return 0m;

            // ===== 5) Фильтры Binance =====
            var filters = await _symbolInfoService.GetFuturesFiltersAsync(symbol);

            decimal step = filters.step <= 0 ? 0.001m : filters.step;
            decimal minQty = filters.minQty <= 0 ? (decimal)_options.MinQuantity : filters.minQty;
            decimal minNotional = filters.minNotional <= 0 ? 5m : filters.minNotional;

            // поджимаем под step
            decimal qty = Math.Floor(rawQty / step) * step;
            if (qty < minQty)
                qty = minQty;

            // проверка minNotional
            var notional = qty * entryPrice;
            if (notional < minNotional)
            {
                var needQty = minNotional / entryPrice;
                qty = Math.Ceiling(needQty / step) * step;
                notional = qty * entryPrice;
            }

            if (qty <= 0)
                return 0m;

            // ===== 6) Дополнительная защита от Margin is insufficient =====
            // Прикидываем требуемую маржу как notional / effLevForMargin
            // + небольшой запас под комиссию/волатильность.
            decimal approxRequiredMargin = (qty * entryPrice) / effLevForMargin;
            approxRequiredMargin *= 1.03m; // 3% запас

            // мы разрешаем тратить не больше 45% доступной маржи на одну сделку
            decimal maxMarginPerTrade = available * 0.45m;

            if (approxRequiredMargin > maxMarginPerTrade)
            {
                // Урезаем qty пропорционально
                var factor = maxMarginPerTrade / approxRequiredMargin;
                if (factor <= 0)
                    return 0m;

                qty = qty * factor;
                qty = Math.Floor(qty / step) * step;

                if (qty <= 0)
                    return 0m;

                notional = qty * entryPrice;
            }

            // финальная страховка: если после всех ужиманий notional всё ещё слишком жирный,
            // повторно уменьшаем до тех пор, пока маржа не влезет в лимит
            int guard = 0;
            while (qty > 0 && guard < 20)
            {
                approxRequiredMargin = (qty * entryPrice) / effLevForMargin * 1.03m;
                if (approxRequiredMargin <= maxMarginPerTrade)
                    break;

                qty -= step;
                qty = Math.Floor(qty / step) * step;
                guard++;
            }

            if (qty <= 0)
                return 0m;

            notional = qty * entryPrice;

            // ===== 7) Консольный отчёт =====
            ConsoleReportFormatter.RiskReport(
                _logger,
                symbol,
                qty,
                notional,
                riskUsd,
                slDist,
                cfgLev,
                deposit,
                maxNotionalTotal,
                step,
                minQty,
                minNotional);

            return qty;
        }
    }
}
