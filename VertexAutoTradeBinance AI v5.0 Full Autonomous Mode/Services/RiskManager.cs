// ============================================================================
//  RISK MANAGER v5.0 — чистая версия БЕЗ ошибок
// ============================================================================

using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.Services
{
    public class RiskManager
    {
        private readonly ILogger<RiskManager> _logger;
        private readonly SymbolInfoService _symbolInfo;
        private readonly TradingOptions _options;

        public RiskManager(
            ILogger<RiskManager> logger,
            SymbolInfoService symbolInfo,
            TradingOptions options)
        {
            _logger = logger;
            _symbolInfo = symbolInfo;
            _options = options;
        }

        // ====================================================================
        // SAFE QTY
        // ====================================================================
        public async Task<decimal> CalculateSafeQty(
            string symbol,
            decimal entryPrice,
            decimal stopLoss,
            decimal riskMultiplier,
            decimal leverage,
            CancellationToken ct)
        {
            // 0) Filters
            var f = await _symbolInfo.GetFuturesFiltersAsync(symbol);

            decimal step = f.step <= 0 ? 0.001m : f.step;
            decimal minQty = f.minQty <= 0 ? step : f.minQty;
            decimal minNotional = f.minNotional <= 0 ? 5m : f.minNotional;

            // 1) Distance
            decimal slDist = Math.Abs(entryPrice - stopLoss);
            if (slDist <= 0 || stopLoss <= 0 || entryPrice <= 0)
            {
                _logger.LogError("[RISK] Invalid SL distance: entry={entry}, sl={sl}", entryPrice, stopLoss);
                return 0;
            }

            // 2) % risk from deposit
            decimal deposit = (decimal)_options.Deposit;
            decimal maxRisk = deposit *(decimal)_options.RiskPerTrade;
            if (maxRisk <= 0) maxRisk = 1m;
            maxRisk *= riskMultiplier;

            // 3) Raw QTY
            decimal qty = maxRisk / slDist;

            // 4) apply leverage
            if (leverage > 0)
                qty *= leverage;

            // 5) step filter
            qty = Math.Floor(qty / step) * step;

            if (qty < minQty)
                qty = minQty;

            // 6) Notional
            decimal notional = qty * entryPrice;
            if (notional < minNotional)
            {
                decimal needQty = (minNotional / entryPrice);
                needQty = Math.Ceiling(needQty / step) * step;
                qty = needQty;

                _logger.LogWarning(
                    "[RISK] Adjusted QTY: minNotional={m} → qty={q}",
                    minNotional, qty);
            }

            if (qty <= 0)
            {
                _logger.LogWarning("[RISK] Final QTY <= 0 → rejected");
                return 0;
            }


            // 7) LOG safe info
            _logger.LogInformation(
                """
                ═══════════════════════════════════════════════════════════
                 💰 РИСК-МЕНЕДЖМЕНТ (v5.0 SMART)
                ═══════════════════════════════════════════════════════════
                {Symbol}

                Размер позиции: {qty}
                Нотионал: {not:F2}
                SL-дистанция: {dist:F4}
                Риск: {risk:F2} USDT
                Плечо: x{lev}
                Фильтры: step={step}, minQty={minQty}, minNotional={minNot}, tick={tick}
                """,
                symbol,
                qty,
                notional,
                slDist,
                maxRisk,
                leverage,
                step,
                f.minQty,
                f.minNotional,
                f.tickSize
            );

            return qty;
        }
    }
}
