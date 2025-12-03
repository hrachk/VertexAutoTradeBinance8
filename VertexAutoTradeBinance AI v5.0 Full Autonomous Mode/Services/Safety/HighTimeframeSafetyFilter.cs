using Binance.Net.Enums;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// Optional-фильтр: 1H + 1D трендовая защита против ложных пробоев.
    /// Усиливает надёжность входа на младших ТФ.
    /// </summary>
    public class HighTimeframeSafetyFilter
    {
        private readonly ILogger<HighTimeframeSafetyFilter> _logger;
        private readonly AiMarketRegimeService _regime;

        public HighTimeframeSafetyFilter(
            ILogger<HighTimeframeSafetyFilter> logger,
            AiMarketRegimeService regime)
        {
            _logger = logger;
            _regime = regime;

        }

        public HighTimeframeSafetyFilter()
        {
        }

        public static HighTimeframeSafetyFilter Instance { get; } = new();


        /// <summary>
        /// Проверяем 1H и 1D режимы.
        /// Если оба сильные — включается Swing-режим.
        /// </summary>
        public async Task<HighTFResult> EvaluateAsync(
            string symbol,
            CancellationToken ct)
        {
            // 1) Загружаем 1H и 1D
            var klines1h = await _regime.LoadKlinesSafe(symbol, KlineInterval.OneHour, 200);
            var klines1d = await _regime.LoadKlinesSafe(symbol, KlineInterval.OneDay, 200);

            var r1h = _regime.DetectRegime(symbol, KlineInterval.OneHour, klines1h);
            var r1d = _regime.DetectRegime(symbol, KlineInterval.OneDay, klines1d);

            bool strong1h = r1h.Regime is MarketRegime.StrongUpTrend or MarketRegime.StrongDownTrend;
            bool strong1d = r1d.Regime is MarketRegime.StrongUpTrend or MarketRegime.StrongDownTrend;

            var result = new HighTFResult
            {
                StrongTrend1H = strong1h,
                StrongTrend1D = strong1d
            };

            // Если оба ТФ показывают силу — активируем Swing Safe Mode
            if (strong1h && strong1d)
            {
                result.SwingModeEnabled = true;
                result.SwingRiskMultiplier = 0.50m;      // уменьшаем риск ×0.5
            }

            // Проверка манипуляций
            result.AntiLiquidityGrab = IsManipulationDetected(r1h, r1d);

            // Подтверждение много-ТФ тренда
            result.MultiTFConfirmed = MultiTFTrendOK(r1h, r1d);

            LogResult(symbol, result);

            return result;
        }

        // =====================================================================
        // МЕХАНИЗМЫ ПРОФИ-ПРОВЕРОК
        // =====================================================================
        private bool IsManipulationDetected(MarketRegimeResult r1h, MarketRegimeResult r1d)
        {
            // Девиация больше чем 1.8 sigma
            bool spike = Math.Abs(r1h.DeviationScore) >= 1.8m ||
                         Math.Abs(r1d.DeviationScore) >= 1.8m;

            // Высокая волатильность (1.5% в день — условие манипуляции)
            bool vol = r1h.VolatilityPercent >= 0.015m ||
                       r1d.VolatilityPercent >= 0.015m;

            return spike || vol;
        }

        private bool MultiTFTrendOK(MarketRegimeResult r1h, MarketRegimeResult r1d)
        {
            // Если направление совпадает
            bool sameDirection =
                (r1h.Regime == MarketRegime.StrongUpTrend && r1d.Regime == MarketRegime.StrongUpTrend) ||
                (r1h.Regime == MarketRegime.StrongDownTrend && r1d.Regime == MarketRegime.StrongDownTrend);

            return sameDirection;
        }

        private void LogResult(string symbol, HighTFResult r)
        {
            _logger.LogInformation(
                $"""
                ────────────────────────────────────────────────
                🔍 HighTimeframeSafetyFilter v1.0: {symbol}
                1H strong: {r.StrongTrend1H}
                1D strong: {r.StrongTrend1D}
                SwingMode: {r.SwingModeEnabled}
                MultiTF trend OK: {r.MultiTFConfirmed}
                Anti Liquidity Grab: {r.AntiLiquidityGrab}
                SwingRiskMult: {r.SwingRiskMultiplier}
                """);
        }
    }

    public class HighTFResult
    {
        public bool StrongTrend1H { get; set; }
        public bool StrongTrend1D { get; set; }

        public bool MultiTFConfirmed { get; set; }

        public bool AntiLiquidityGrab { get; set; }

        public bool SwingModeEnabled { get; set; } = false;

        public decimal SwingRiskMultiplier { get; set; } = 1.0m; // default
    }
}
