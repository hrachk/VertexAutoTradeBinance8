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
        // SAFE QTY v7.3 (QUANT-REALTIME FINAL)
        // ====================================================================
        public async Task<decimal> CalculateSafeQty(
            string symbol,
            decimal entryPrice,
            decimal stopLoss,
            decimal riskMultiplier,
            decimal safetyRiskMultiplier,
            decimal leverage,
            CancellationToken ct)
        {
            // BASIC VALIDATION
            if (entryPrice <= 0 || stopLoss <= 0)
            {
                _logger.LogWarning("[RISK7.3] {symbol}: invalid prices entry={e}, sl={sl}", symbol, entryPrice, stopLoss);
                return 0;
            }

            decimal slDist = Math.Abs(entryPrice - stopLoss);
            if (slDist <= 0)
            {
                _logger.LogWarning("[RISK7.3] {symbol}: slDist <= 0", symbol);
                return 0;
            }

            // LOAD FILTERS
            var f = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            decimal step = f.step > 0 ? f.step : 0.001m;
            decimal minQty = f.minQty > 0 ? f.minQty : step;

            decimal minNotional = f.minNotional > 0
                ? f.minNotional
                : (_options.MinNotionalGuard > 0 ? _options.MinNotionalGuard : 5m);

            // SAVE ORIGINAL BINANCE REQUIREMENT
            decimal binanceMinNotional = minNotional;

            // ACCOUNT BALANCE
            using var client = _factory.CreateRestClient();
            var acc = await client.UsdFuturesApi.Account.GetBalancesAsync(null, ct);

            if (!acc.Success || acc.Data == null)
            {
                _logger.LogError("[RISK7.3] Can't load account balance: {err}", acc.Error);
                return 0;
            }

            var usdt = acc.Data.FirstOrDefault(x => x.Asset == "USDT");
            decimal free = usdt?.AvailableBalance ?? 0;

            if (free <= 0)
            {
                LogMissedTrade(symbol, entryPrice, stopLoss,
                    "NoBalance", free, 0, binanceMinNotional);

                _logger.LogWarning("[RISK7.3] {symbol}: free balance <= 0", symbol);
                return 0;
            }

            // BASE RISK %
            decimal baseRiskPercent = _options.BaseRiskPercent > 0
                ? _options.BaseRiskPercent
                : 0.03m;

            // AI LEVERAGE
            decimal aiLevMult = await GetAiLeverageMultiplierAsync(symbol, ct);

            // FINAL RISK FACTOR
            decimal finalRisk = riskMultiplier * safetyRiskMultiplier * aiLevMult;

            if (finalRisk < 0.3m) finalRisk = 0.3m;
            if (finalRisk > 2.5m) finalRisk = 2.5m;

            if (riskMultiplier > 1.5m && safetyRiskMultiplier > 1.0m && aiLevMult > 1.2m)
                finalRisk = Math.Min(finalRisk * 1.1m, 2.7m);

            // MAX ALLOWED RISK
            decimal maxRisk = free * baseRiskPercent * finalRisk;
            if (maxRisk < 1m) maxRisk = 1m;
            if (maxRisk > free * 0.20m)
                maxRisk = free * 0.20m;

            // INITIAL RAW QTY
            decimal qty = maxRisk / slDist;
            if (leverage > 0) qty *= leverage;

            qty = Math.Floor(qty / step) * step;
            if (qty < minQty) qty = minQty;

            decimal notional = qty * entryPrice;

            // ====================================================================
            // SIGNAL STRENGTH BASED ADJUSTMENT
            // ====================================================================
            decimal signalScore = riskMultiplier * safetyRiskMultiplier;
            bool strong = signalScore >= 1.30m;
            bool medium = signalScore >= 0.80m && signalScore < 1.30m;
            bool weak = signalScore < 0.80m;

            // HANDLE WEAK SIGNALS
            if (weak)
            {
                LogMissedTrade(symbol, entryPrice, stopLoss,
                    "WeakSignalRejected", free, notional, binanceMinNotional);

                return 0;
            }

            // IF BELOW BINANCE MINNOTIONAL → ADJUST
            if (notional < binanceMinNotional)
            {
                decimal targetNotional = binanceMinNotional;

                if (strong)
                    targetNotional = binanceMinNotional * 1.5m;

                decimal maxAllowedNotional = Math.Min(maxRisk * leverage, free * leverage);

                if (targetNotional > maxAllowedNotional)
                    targetNotional = maxAllowedNotional;

                if (targetNotional < binanceMinNotional)
                {
                    LogMissedTrade(symbol, entryPrice, stopLoss,
                        "InsufficientBalanceForMinNotional",
                        free,
                        attemptNotional: targetNotional,
                        requiredMinNotional: binanceMinNotional);

                    return 0;
                }

                qty = Math.Floor((targetNotional / entryPrice) / step) * step;
                if (qty < minQty) qty = minQty;

                notional = qty * entryPrice;

                _logger.LogInformation(
                    "[RISK7.3] {symbol}: Notional adjusted by signal strength → strong={strong}, medium={medium}, weak={weak}, qty={qty}, notional={notional}",
                    symbol, strong, medium, weak, qty, notional);
            }

            // NOTIONAL MUST NOT EXCEED AVAILABLE
            decimal maxLeverageNotional = free * leverage;

            if (notional > maxLeverageNotional)
            {
                LogMissedTrade(symbol, entryPrice, stopLoss,
                    "NotEnoughBalanceForCalculatedSize",
                    free,
                    attemptNotional: notional,
                    requiredMinNotional: binanceMinNotional);

                qty = Math.Floor((maxLeverageNotional / entryPrice) / step) * step;
                notional = qty * entryPrice;
            }

            if (qty <= 0 || notional <= 0)
            {
                LogMissedTrade(symbol, entryPrice, stopLoss,
                    "QtyZeroAfterAdjust", free, notional, binanceMinNotional);

                return 0;
            }

            _logger.LogInformation(
                "[RISK7.3] {symbol}: free={free:F2}, riskMult={rm:F2}, safety={sr:F2}, aiLev={alm:F2}, finalRisk={fr:F2}, maxRisk={mx:F2}, lev={lev:F1}, qty={qty:F4}, notional={notional:F2}",
                symbol,
                free,
                riskMultiplier,
                safetyRiskMultiplier,
                aiLevMult,
                finalRisk,
                maxRisk,
                leverage,
                qty,
                notional);

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

                var mult = _aiLeverage.Calculate(symbol, KlineInterval.FifteenMinutes, klines);
                if (mult <= 0) return 1.0m;

                return mult;
            }
            catch
            {
                return 1.0m;
            }
        }

        // ====================================================================
        // MISSED TRADES LOGGER FINAL
        // ====================================================================
        private void LogMissedTrade(
            string symbol,
            decimal entry,
            decimal sl,
            string reason,
            decimal freeBalance,
            decimal attemptNotional,
            decimal requiredMinNotional)
        {
            try
            {
                var record = new
                {
                    symbol,
                    time = DateTime.UtcNow,
                    entry,
                    stopLoss = sl,
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
                else
                {
                    list = new List<object>();
                }

                list.Add(record);

                File.WriteAllText(
                    MissedTradesPath,
                    JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true })
                );

                _logger.LogWarning("[MISSED TRADE] {symbol} logged: reason={reason}", symbol, reason);
            }
            catch { }
        }
    }
}
