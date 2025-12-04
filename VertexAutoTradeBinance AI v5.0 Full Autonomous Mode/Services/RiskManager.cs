using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using Binance.Net.Clients;
using System.Linq;

namespace VertexAutoTradeBinance8.Services
{
    public class RiskManager
    {
        private readonly ILogger<RiskManager> _logger;
        private readonly SymbolInfoService _symbolInfo;
        private readonly TradingOptions _options;
        private readonly BinanceClientFactory _factory;

        public RiskManager(
            ILogger<RiskManager> logger,
            SymbolInfoService symbolInfo,
            TradingOptions options,
            BinanceClientFactory factory)
        {
            _logger = logger;
            _symbolInfo = symbolInfo;
            _options = options;
            _factory = factory;
        }

        // ====================================================================
        // SAFE QTY
        // ====================================================================
        public async Task<decimal> CalculateSafeQty(
            string symbol,
            decimal entryPrice,
            decimal stopLoss,
            decimal riskMultiplier,
            decimal safetyRiskMultiplier,   // AI + сигнал
            decimal leverage,
            CancellationToken ct)
        {
            // 1) Сводим всё к одному финальному коэффициенту риска
            decimal finalRisk = riskMultiplier * safetyRiskMultiplier;

            // Базовый коридор: от 0.5x до 2.0x
            if (finalRisk < 0.5m) finalRisk = 0.5m;
            if (finalRisk > 2.0m) finalRisk = 2.0m;

            // Если риск-модель даёт совсем “ядерный” сигнал — чуть расширяем
            if (riskMultiplier > 1.5m && safetyRiskMultiplier > 1.0m)
                finalRisk = Math.Min(finalRisk * 1.1m, 2.3m);

            // 0) Filters
            var f = await _symbolInfo.GetFuturesFiltersAsync(symbol);

            decimal step = f.step <= 0 ? 0.001m : f.step;
            decimal minQty = f.minQty <= 0 ? step : f.minQty;

            // если биржа не даёт minNotional → fallback из конфига
            decimal minNotional = f.minNotional > 0
                ? f.minNotional
                : (_options.MinNotionalGuard > 0 ? _options.MinNotionalGuard : 5m);

            // LIVE BALANCE
            using var client = _factory.CreateRestClient();
            var acc = await client.UsdFuturesApi.Account.GetBalancesAsync(null, ct);

            if (!acc.Success || acc.Data == null)
            {
                _logger.LogError("[RISK] Can't load account balance");
                return 0;
            }

            var usdt = acc.Data.FirstOrDefault(x => x.Asset == "USDT");
            decimal free = usdt?.AvailableBalance ?? 0;

            if (free <= 0)
                return 0;

            // SL distance
            decimal slDist = Math.Abs(entryPrice - stopLoss);
            if (slDist <= 0)
                return 0;

            // 2) CORRECT MAX RISK
            // базовый риск с учётом настроек TradingOptions
            decimal baseRiskPercent = _options.BaseRiskPercent > 0
                ? _options.BaseRiskPercent
                : 0.03m; // 3% по умолчанию

            decimal maxRisk = free * baseRiskPercent;
            maxRisk *= finalRisk; // AI-скейлинг

            // Жёсткие границы по абсолютному риску
            if (maxRisk < 1m) maxRisk = 1m;
            if (maxRisk > free * 0.20m) // не рискуем >20% депо в одной сделке
                maxRisk = free * 0.20m;

            // 3) RAW QTY
            decimal qty = maxRisk / slDist;

            if (leverage > 0)
                qty *= leverage;

            qty = Math.Floor(qty / step) * step;
            if (qty < minQty)
                qty = minQty;

            decimal notional = qty * entryPrice;

            // защитный фильтр по notional: не меньше minNotional и не больше free * leverage
            if (notional < minNotional)
            {
                _logger.LogWarning(
                    "[RISK] Notional {notional:F2} < minNotional {minNotional:F2} → SKIP",
                    notional, minNotional);
                return 0;
            }

            if (notional > free * leverage)
            {
                qty = Math.Floor((free * leverage) / entryPrice / step) * step;
                notional = qty * entryPrice;
            }

            if (qty <= 0 || notional <= 0)
                return 0;

            _logger.LogInformation(
                "[RISK] {symbol}: free={free:F2}, maxRisk={maxRisk:F2}, finalRisk={fr:F2}, qty={qty:F4}, notional={notional:F2}",
                symbol, free, maxRisk, finalRisk, qty, notional);

            return qty;
        }
    }
}
