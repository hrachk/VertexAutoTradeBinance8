using Binance.Net.Clients;
using Binance.Net.Enums;
using Microsoft.Extensions.Logging;
using System.Linq;
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
        // SAFE QTY v6 (QUANT-REALTIME)
        // ====================================================================
        /// <summary>
        /// Основной расчёт безопасного размера позиции.
        /// Использует:
        /// - riskMultiplier (AiRiskScalerV2 / grade)
        /// - safetyRiskMultiplier (из сигнала)
        /// - AiLeverageService (QUANT-REALTIME v6)
        /// - фактическое плечо (leverage), которое уже выставлено на бирже
        /// </summary>
        public async Task<decimal> CalculateSafeQty(
            string symbol,
            decimal entryPrice,
            decimal stopLoss,
            decimal riskMultiplier,
            decimal safetyRiskMultiplier,   // AI + сигнал
            decimal leverage,               // фактическое плечо на бирже
            CancellationToken ct)
        {
            // 0) Базовые проверки
            if (entryPrice <= 0 || stopLoss <= 0)
            {
                _logger.LogWarning("[RISK6] {symbol}: invalid prices entry={e}, sl={sl}", symbol, entryPrice, stopLoss);
                return 0;
            }

            decimal slDist = Math.Abs(entryPrice - stopLoss);
            if (slDist <= 0)
            {
                _logger.LogWarning("[RISK6] {symbol}: slDist <= 0", symbol);
                return 0;
            }

            // 1) Фильтры биржи (step, minQty, minNotional)
            var f = await _symbolInfo.GetFuturesFiltersAsync(symbol);

            decimal step = f.step <= 0 ? 0.001m : f.step;
            decimal minQty = f.minQty <= 0 ? step : f.minQty;

            decimal minNotional = f.minNotional > 0
                ? f.minNotional
                : (_options.MinNotionalGuard > 0 ? _options.MinNotionalGuard : 5m);

            // 2) Баланс аккаунта
            using var client = _factory.CreateRestClient();
            var acc = await client.UsdFuturesApi.Account.GetBalancesAsync(null, ct);

            if (!acc.Success || acc.Data == null)
            {
                _logger.LogError("[RISK6] Can't load account balance: {err}", acc.Error);
                return 0;
            }

            var usdt = acc.Data.FirstOrDefault(x => x.Asset == "USDT");
            decimal free = usdt?.AvailableBalance ?? 0;

            if (free <= 0)
            {
                _logger.LogWarning("[RISK6] {symbol}: free balance <= 0", symbol);
                return 0;
            }

            // 3) Базовый риск (% от депозита)
            decimal baseRiskPercent = _options.BaseRiskPercent > 0
                ? _options.BaseRiskPercent
                : 0.03m; // 3% по умолчанию

            // 4) AI-левередж (QUANT-REALTIME) — мультипликатор риска
            decimal aiLevMult = await GetAiLeverageMultiplierAsync(symbol, ct);

            // 5) Сводим всё к финальному коэффициенту
            decimal finalRisk = riskMultiplier * safetyRiskMultiplier * aiLevMult;

            // Базовый коридор: от 0.3x до 2.5x
            if (finalRisk < 0.3m) finalRisk = 0.3m;
            if (finalRisk > 2.5m) finalRisk = 2.5m;

            // Если риск-модель даёт совсем “ядерный” сигнал — чуть расширяем
            if (riskMultiplier > 1.5m && safetyRiskMultiplier > 1.0m && aiLevMult > 1.2m)
                finalRisk = Math.Min(finalRisk * 1.1m, 2.7m);

            // 6) Абсолютный максимум риска в USDT
            decimal maxRisk = free * baseRiskPercent;
            maxRisk *= finalRisk; // AI-скейлинг

            // Жёсткие границы по абсолютному риску
            if (maxRisk < 1m) maxRisk = 1m;
            if (maxRisk > free * 0.20m) // не рискуем >20% депо в одной сделке
                maxRisk = free * 0.20m;

            // 7) RAW QTY до квантования
            decimal qty = maxRisk / slDist;

            // Учитываем выставленное плечо
            if (leverage > 0)
                qty *= leverage;

            // Квантуем по шагу
            qty = Math.Floor(qty / step) * step;
            if (qty < minQty)
                qty = minQty;

            decimal notional = qty * entryPrice;

            // 8) Фильтр по notional
            if (notional < minNotional)
            {
                _logger.LogWarning(
                    "[RISK6] {symbol}: Notional {notional:F2} < minNotional {minNotional:F2} → SKIP",
                    symbol, notional, minNotional);
                return 0;
            }

            // Не больше, чем free * leverage
            if (notional > free * leverage)
            {
                qty = Math.Floor((free * leverage) / entryPrice / step) * step;
                notional = qty * entryPrice;
            }

            if (qty <= 0 || notional <= 0)
            {
                _logger.LogWarning("[RISK6] {symbol}: qty or notional <= 0 → SKIP", symbol);
                return 0;
            }

            _logger.LogInformation(
                "[RISK6] {symbol}: free={free:F2}, baseRisk%={br:P2}, riskMult={rm:F2}, safety={sr:F2}, aiLev={alm:F2}, finalRisk={fr:F2}, maxRisk={mx:F2}, lev={lev:F1}, qty={qty:F4}, notional={notional:F2}",
                symbol,
                free,
                baseRiskPercent,
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
        // AI LEVERAGE MULTIPLIER HELPER
        // ====================================================================
        private async Task<decimal> GetAiLeverageMultiplierAsync(string symbol, CancellationToken ct)
        {
            try
            {
                // Для риск-модели берём M15 — золотая середина между шумом и дневкой
                var klines = await _marketData.GetKlines(symbol, KlineInterval.FifteenMinutes, 200);

                if (klines == null || klines.Count < 30)
                {
                    _logger.LogWarning("[RISK6][{symbol}] Not enough klines for AiLeverage (Count={cnt}) → 1.0",
                        symbol, klines?.Count ?? 0);
                    return 1.0m;
                }

                var mult = _aiLeverage.Calculate(symbol, KlineInterval.FifteenMinutes, klines);
                if (mult <= 0)
                    return 1.0m;

                return mult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RISK6][{symbol}] AiLeverage calculation error → 1.0", symbol);
                return 1.0m;
            }
        }
    }
}
