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

        public string? LastRejectReason { get; private set; }
        public decimal LastBalanceUsdt { get; private set; }

        private const decimal MaxMarginPercent = 0.12m; // максимум 12% баланса в марже
        private const decimal HardRiskCap = 0.05m;      // максимум 5% риска на сделку

        public RiskManager(
            ILogger<RiskManager> logger,
            SymbolInfoService symbolInfo,
            BinanceClientFactory factory)
        {
            _logger = logger;
            _symbolInfo = symbolInfo;
            _factory = factory;
        }

        // ============================================
        // Dynamic base risk (адаптивно от баланса)
        // ============================================
        private decimal GetDynamicBaseRisk(decimal balance)
        {
            if (balance <= 100m) return 0.025m;
            if (balance <= 500m) return 0.02m;
            if (balance <= 1000m) return 0.015m;
            if (balance <= 5000m) return 0.012m;
            if (balance <= 10000m) return 0.01m;
            return 0.0075m;
        }

        // ============================================
        // Precision helper
        // ============================================
        public int GetPrecision(decimal step)
        {
            step = step.Normalize();
            int[] bits = decimal.GetBits(step);
            return (bits[3] >> 16) & 0x7F;
        }

        // ============================================
        // MAIN PROP DESK QTY CALC
        // ============================================
        public decimal GetPropDeskQtyFinal(
            TradeSignal signal,
            decimal balance,
            decimal step,
            decimal minQty,
            decimal exchangeMinNotional,
            decimal riskMult,
            TradingOptions trading)
        {
            LastRejectReason = null;

            if (signal == null || balance <= 0 || step <= 0)
            {
                LastRejectReason = "Invalid input";
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

            // ============================================
            // SL distance
            // ============================================
            decimal slPercent = Math.Abs(entry - stop) / entry;

            if (slPercent <= 0)
            {
                LastRejectReason = "SL too close";
                return 0;
            }

            // ============================================
            // Risk calculation
            // ============================================
            decimal baseRisk = trading.RiskPerTrade > 0
                ? (decimal)trading.RiskPerTrade
                : GetDynamicBaseRisk(balance);

            decimal safetyMult = signal.SafetyRiskMultiplier > 0
                ? signal.SafetyRiskMultiplier
                : 1m;

            decimal finalRisk = Math.Min(baseRisk * riskMult * safetyMult, HardRiskCap);

            if (finalRisk <= 0)
            {
                LastRejectReason = "Final risk <= 0";
                return 0;
            }

            decimal riskBudget = balance * finalRisk;

            // ============================================
            // Position notional from SL model
            // ============================================
            decimal positionNotional = riskBudget / slPercent;

            // Ограничение по плечу
            decimal leverageCap = balance * leverage * 0.98m;

            // Ограничение по марже
            decimal marginCap = balance * MaxMarginPercent * leverage;

            decimal finalNotional = Math.Min(positionNotional,
                                    Math.Min(leverageCap, marginCap));

            if (finalNotional <= 0)
            {
                LastRejectReason = "Final notional <= 0";
                return 0;
            }

            // ============================================
            // Convert to qty
            // ============================================
            decimal rawQty = finalNotional / entry;

            decimal qty = Math.Floor(rawQty / step) * step;

            if (qty < minQty)
            {
                LastRejectReason =
                    $"QTY_TOO_SMALL | step={step} minQty={minQty}";
                return 0;
            }

            // ============================================
            // Exchange minNotional check (REAL FILTER)
            // ============================================
            decimal checkNotional = qty * entry;

            if (checkNotional < exchangeMinNotional)
            {
                LastRejectReason =
                    $"MIN_NOTIONAL_TOO_HIGH_FOR_MODEL | required={exchangeMinNotional:F2} actual={checkNotional:F2}";
                return 0;
            }

            return qty;
        }

        // ============================================
        // Real balance
        // ============================================
        public async Task<decimal> GetRealtimeBalanceAsync(CancellationToken ct)
        {
            try
            {
                var client = _factory.CreateRestClient();
                var account = await client.UsdFuturesApi.Account
                    .GetAccountInfoV3Async(ct: ct)
                    .ConfigureAwait(false);

                if (!account.Success || account.Data == null)
                {
                    _logger.LogWarning("Failed to fetch balance");
                    return 0m;
                }

                var free = account.Data.Assets
                    .FirstOrDefault(a => a.Asset == "USDT")?
                    .AvailableBalance ?? 0m;

                LastBalanceUsdt = Math.Max(free, 0m);
                return LastBalanceUsdt;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Balance fetch exception");
                return 0m;
            }
        }
    }
}