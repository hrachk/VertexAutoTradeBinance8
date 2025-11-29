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
            if (entryPrice <= 0 || stopLoss <= 0 || entryPrice == stopLoss)
                return 0m;

            var (deposit, available) = await GetCurrentDepositAsync();
            if (available <= 0) return 0m;

            decimal aiWeight = _aiLearning.GetDynamicRiskWeight(
                symbol,
                MarketRegime.Unknown); // StrategyEngine даст реальный режим

            decimal riskMultiplier = baseRiskMultiplier * aiWeight;

            var lev = _options.Leverage > 0 ? (decimal)_options.Leverage : 1m;
            var baseRiskUsd = available * (decimal)_options.RiskPerTrade;
            var riskUsd = baseRiskUsd * riskMultiplier;

            var slDist = Math.Abs(entryPrice - stopLoss);
            if (slDist <= 0) return 0m;

            var qtyByRisk = riskUsd / slDist;

            var maxNotionalTotal = available * lev * 0.90m;
            var maxQtyByBalance = maxNotionalTotal / entryPrice;

            var rawQty = Math.Min(qtyByRisk, maxQtyByBalance);

            var filters = await _symbolInfoService.GetFuturesFiltersAsync(symbol);
            decimal step = filters.step <= 0 ? 0.001m : filters.step;
            decimal minQty = filters.minQty <= 0 ? (decimal)_options.MinQuantity : filters.minQty;
            decimal minNotional = filters.minNotional <= 0 ? 5m : filters.minNotional;

            decimal qty = Math.Max(rawQty, minQty);
            var notional = qty * entryPrice;

            if (notional < minNotional)
            {
                var needQty = minNotional / entryPrice;
                qty = Math.Ceiling(needQty / step) * step;
            }

            qty = Math.Floor(qty / step) * step;

            if (qty <= 0) return 0m;

            ConsoleReportFormatter.RiskReport(
                _logger,
                symbol,
                qty,
                qty * entryPrice,
                riskUsd,
                slDist,
                lev,
                deposit,
                maxNotionalTotal,
                step,
                minQty,
                minNotional);

            return qty;
        }
    }
}
