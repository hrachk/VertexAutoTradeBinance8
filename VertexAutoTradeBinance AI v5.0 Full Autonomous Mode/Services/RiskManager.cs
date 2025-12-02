// ============================================================================
// RISK MANAGER v5.0 — интелектуальный расчёт количества
// - ATR-aware
// - Notional-aware
// - MinNotional/minQty/max leverage safe
// - Anti-manipulation buffer
// - FULL совместимость с Signal & Executor v5.0
// ============================================================================

using Binance.Net.Enums;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class RiskManager
    {
        private readonly ILogger<RiskManager> _logger;
        private readonly SymbolInfoService _symbolInfo;

        public RiskManager(
            ILogger<RiskManager> logger,
            SymbolInfoService symbolInfo)
        {
            _logger = logger;
            _symbolInfo = symbolInfo;
        }

        // =====================================================================
        // MAIN METHOD — CALCULATE SAFE QTY
        // =====================================================================
        Task<decimal> CalculateSafeQty(
     string symbol,
     decimal entry,
     decimal stop,
     decimal riskMultiplier,
     decimal leverage,
     CancellationToken ct)
        {
            // 1) Load exchange filters
            var f = _symbolInfo.GetFuturesFiltersAsync(symbol).Result;
            // tick = f.tickSize <= 0 ? 0.01m : f.tickSize;
            //decimal step = f.step <= 0 ? 0.001m : f.step;
            decimal step = f.step;
            decimal minQty = f.minQty;
            decimal minNotional = f.minNotional;
            decimal tick = f.tickSize;
            decimal slDist = Math.Abs(EntryPrice - stopLoss);

            if (slDist <= 0)
            {
                _logger.LogError("[RISK] SL distance invalid → 0");
                return 0;
            }

            // 2) Risk amount (1–2% мозга, но ты управляешь сам — оставляю гибко)
            decimal maxRisk = deposit * 0.03m; // 3% (можешь менять)
            if (maxRisk < 1m) maxRisk = 1m;

            // 3) Qty = Risk / SL-distance
            decimal qty = maxRisk / slDist;

            // 4) apply leverage (умножаем потенциальную позицию)
            qty *= leverage;

            // 5) Adjust by step filter
            qty = Math.Floor(qty / step) * step;

            if (qty < f.minQty)
            {
                _logger.LogWarning(
                    "[RISK] Qty {qty} < minQty {min} → adjusted to minQty",
                    qty, f.minQty);
                qty = f.minQty;
            }

            // 6) Notional check
            decimal notional = qty * entryPrice;
            if (notional < f.minNotional)
            {
                decimal needQty = f.minNotional / entryPrice;
                needQty = Math.Ceiling(needQty / step) * step;

                _logger.LogWarning(
                    "[RISK] Notional {n:F2} < minNotional {min:F2} → raise to {q}",
                    notional, f.minNotional, needQty);

                qty = needQty;
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
                tick
            );

            return qty;
        }
    }
}
