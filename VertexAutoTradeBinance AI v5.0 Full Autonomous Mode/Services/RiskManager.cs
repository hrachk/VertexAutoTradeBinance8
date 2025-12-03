// ============================================================================
//  RISK MANAGER v5.1 — с live-балансом + правильным maxRisk + notional guard
// ============================================================================

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
      decimal safetyRiskMultiplier,   // ← ДОБАВЛЕН
      decimal leverage,
      CancellationToken ct)
        {
            // объединяем два риска
            decimal finalRisk = riskMultiplier * safetyRiskMultiplier;
            if (finalRisk < 0.1m)
                finalRisk = 0.1m;

            // 0) Filters
            var f = await _symbolInfo.GetFuturesFiltersAsync(symbol);

            decimal step = f.step <= 0 ? 0.001m : f.step;
            decimal minQty = f.minQty <= 0 ? step : f.minQty;
            decimal minNotional = f.minNotional <= 0 ? 5m : f.minNotional;

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

            // CORRECT MAX RISK
            decimal maxRisk = free * 0.03m;   // базовый риск
            maxRisk *= finalRisk;             // учитываем safetyRiskMultiplier

            if (maxRisk < 1m) maxRisk = 1m;

            // RAW QTY
            decimal qty = maxRisk / slDist;

            if (leverage > 0)
                qty *= leverage;

            qty = Math.Floor(qty / step) * step;
            if (qty < minQty)
                qty = minQty;

            decimal notional = qty * entryPrice;

            if (notional > free * leverage)
            {
                qty = Math.Floor((free * leverage) / entryPrice / step) * step;
            }

            if (qty <= 0)
                return 0;

            return qty;
        }

    }
}
