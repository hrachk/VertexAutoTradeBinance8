using Binance.Net.Clients;
using Binance.Net.Enums;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Text.Json;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class RiskManager
    {
        private readonly ILogger<RiskManager> _logger;
        private readonly SymbolInfoService _symbolInfo;
        private readonly TradingOptions _options;
        private readonly BinanceClientFactory _factory;
        private readonly MarketDataService _marketData;
        private readonly AiLeverageService _aiLeverage;

        private static readonly string MissedTradesPath =
            Path.Combine(AppContext.BaseDirectory, "missed_trades.json");

        public RiskManager(
            ILogger<RiskManager> logger,
            SymbolInfoService symbolInfo,
            TradingOptions options,
            BinanceClientFactory factory,
            MarketDataService marketData,
            AiLeverageService aiLeverage)
        {
            _logger = logger;
            _symbolInfo = symbolInfo;
            _options = options;
            _factory = factory;
            _marketData = marketData;
            _aiLeverage = aiLeverage;
        }

        // ====================================================================
        // SAFE QTY v7.4 (QUANT-REALTIME FINAL)
        // ====================================================================
        public async Task<decimal> CalculateSafeQty(
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
            if (entryPrice <= 0 || stopLoss <= 0)
                return 0;

            decimal slDist = Math.Abs(entryPrice - stopLoss);
            if (slDist <= 0)
                return 0;

            // LOAD FILTERS
            var f = await _symbolInfo.GetFuturesFiltersAsync(symbol);

            decimal step = f.step > 0 ? f.step : 0.001m;
            decimal minQty = f.minQty > 0 ? f.minQty : step;

            decimal minNotional = f.minNotional > 0
                ? f.minNotional
                : (_options.MinNotionalGuard > 0 ? _options.MinNotionalGuard : 5m);

            decimal binanceMinNotional = minNotional;

            // ACCOUNT BALANCE
            using var client = _factory.CreateRestClient();
            var acc = await client.UsdFuturesApi.Account.GetBalancesAsync(null, ct);
            decimal free = acc?.Data?.FirstOrDefault(x => x.Asset == "USDT")?.AvailableBalance ?? 0;

            if (free <= 0)
            {
                LogMissedTrade(symbol, entryPrice, stopLoss, "NoBalance",
                    free, 0, binanceMinNotional, side, takeProfits);
                return 0;
            }

            // BASE RISK
            decimal baseRiskPercent = _options.BaseRiskPercent > 0
                ? _options.BaseRiskPercent
                : 0.03m;

            // AI LEVERAGE FACTOR
            decimal aiLevMult = await GetAiLeverageMultiplierAsync(symbol, ct);

            // FINAL MULTIPLIER
            decimal finalRisk = riskMultiplier * safetyRiskMultiplier * aiLevMult;
            finalRisk = Math.Clamp(finalRisk, 0.3m, 2.7m);

            decimal maxRisk = free * baseRiskPercent * finalRisk;
            if (maxRisk < 1m) maxRisk = 1m;
            if (maxRisk > free * 0.20m) maxRisk = free * 0.20m;

            decimal qty = maxRisk / slDist;
            if (leverage > 0) qty *= leverage;

            qty = Math.Floor(qty / step) * step;
            if (qty < minQty) qty = minQty;

            decimal notional = qty * entryPrice;

            // =====================
            // SIGNAL STRENGTH LOGIC
            // =====================
            decimal score = riskMultiplier * safetyRiskMultiplier;
            bool strong = score >= 1.30m;
            bool weak = score < 0.80m;

            if (weak)
            {
                LogMissedTrade(symbol, entryPrice, stopLoss, "WeakSignalRejected",
                    free, notional, binanceMinNotional, side, takeProfits);
                return 0;
            }

            // BELOW MIN NOTIONAL → TRY BOOST
            if (notional < binanceMinNotional)
            {
                decimal targetNotional = strong
                    ? binanceMinNotional * 1.5m
                    : binanceMinNotional;

                decimal maxAllowed = free * leverage;
                if (targetNotional > maxAllowed)
                    targetNotional = maxAllowed;

                if (targetNotional < binanceMinNotional)
                {
                    LogMissedTrade(symbol, entryPrice, stopLoss,
                        "InsufficientBalanceForMinNotional",
                        free, targetNotional, binanceMinNotional, side, takeProfits);

                    return 0;
                }

                qty = Math.Floor((targetNotional / entryPrice) / step) * step;
                if (qty < minQty) qty = minQty;

                notional = qty * entryPrice;
            }

            if (qty <= 0 || notional <= 0)
            {
                LogMissedTrade(symbol, entryPrice, stopLoss, "QtyZeroAfterAdjust",
                    free, notional, binanceMinNotional, side, takeProfits);
                return 0;
            }

            return qty;
        }

        // ====================================================================
        // AI LEVERAGE MULTIPLIER
        // ====================================================================
        private async Task<decimal> GetAiLeverageMultiplierAsync(string symbol, CancellationToken ct)
        {
            try
            {
                var klines = await _marketData.GetKlines(symbol, KlineInterval.FifteenMinutes, 200);
                if (klines == null || klines.Count < 30)
                    return 1.0m;

                decimal m = _aiLeverage.Calculate(symbol, KlineInterval.FifteenMinutes, klines);
                return m > 0 ? m : 1.0m;
            }
            catch
            {
                return 1.0m;
            }
        }

        // ====================================================================
        // LOG MISSED TRADES FINAL (side + TP's)
        // ====================================================================
        private void LogMissedTrade(
            string symbol,
            decimal entry,
            decimal sl,
            string reason,
            decimal freeBalance,
            decimal attemptNotional,
            decimal requiredMinNotional,
            SignalSide side,
            List<decimal> takeProfits)
        {
            try
            {
                var record = new
                {
                    symbol,
                    time = DateTime.UtcNow,
                    entry,
                    stopLoss = sl,
                    side = side.ToString(),
                    takeProfits = takeProfits,
                    reason,
                    freeBalance,
                    attemptNotional,
                    requiredMinNotional
                };

                List<object> list;
                if (File.Exists(MissedTradesPath))
                {
                    var json = File.ReadAllText(MissedTradesPath);
                    list = JsonSerializer.Deserialize<List<object>>(json) ?? new List<object>();
                }
                else list = new List<object>();

                list.Add(record);

                File.WriteAllText(
                    MissedTradesPath,
                    JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true })
                );
            }
            catch { }
        }
    }
}
