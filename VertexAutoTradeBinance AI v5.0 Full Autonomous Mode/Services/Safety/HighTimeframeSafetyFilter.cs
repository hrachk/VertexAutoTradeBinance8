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

        /// <summary>
        /// Проверяем 1H и 1D режимы.
        /// Если оба сильные — включается Swing-режим.
        /// </summary>
        public async Task<HighTFResult> EvaluateAsync(
            string symbol,
            CancellationToken ct)
        {
            try
            {
                // =====================================================
                // LOAD KLINES
                // =====================================================

                var klines1h = await _regime
                    .LoadKlinesSafe(symbol, KlineInterval.OneHour, 200)
                    .ConfigureAwait(false);

                var klines1d = await _regime
                    .LoadKlinesSafe(symbol, KlineInterval.OneDay, 200)
                    .ConfigureAwait(false);

                if (klines1h.Count < 60 || klines1d.Count < 60)
                {
                    _logger.LogWarning(
                        "[HTF] Not enough klines for {symbol}",
                        symbol);

                    return new HighTFResult();
                }

                // =====================================================
                // DETECT REGIMES
                // =====================================================

                var r1h = _regime.DetectRegime(
                    symbol,
                    KlineInterval.OneHour,
                    klines1h);

                var r1d = _regime.DetectRegime(
                    symbol,
                    KlineInterval.OneDay,
                    klines1d);

                bool strong1h = IsTrend(r1h.Regime);
                bool strong1d = IsTrend(r1d.Regime);

                var result = new HighTFResult
                {
                    StrongTrend1H = strong1h,
                    StrongTrend1D = strong1d
                };

                // =====================================================
                // SWING MODE
                // =====================================================

                if (strong1h && strong1d)
                {
                    result.SwingModeEnabled = true;
                    result.SwingRiskMultiplier = 0.50m;
                }

                // =====================================================
                // PROFESSIONAL CHECKS
                // =====================================================

                result.AntiLiquidityGrab =
                    IsManipulationDetected(r1h, r1d);

                result.MultiTFConfirmed =
                    MultiTFTrendOK(r1h, r1d);

                LogResult(symbol, result);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[HTF] EvaluateAsync failed for {symbol}",
                    symbol);

                return new HighTFResult();
            }
        }

        // =====================================================
        // TREND CHECK
        // =====================================================

        private static bool IsTrend(MarketRegime r)
        {
            return r is
                MarketRegime.StrongUpTrend or
                MarketRegime.StrongDownTrend or
                MarketRegime.UpTrend or
                MarketRegime.DownTrend;
        }

        // =====================================================
        // MANIPULATION DETECTION
        // =====================================================

        private bool IsManipulationDetected(
            MarketRegimeResult r1h,
            MarketRegimeResult r1d)
        {
            bool spike =
                Math.Abs(r1h.DeviationScore) >= 1.8m ||
                Math.Abs(r1d.DeviationScore) >= 1.8m;

            bool vol =
                r1h.VolatilityPercent >= 0.015m ||
                r1d.VolatilityPercent >= 0.015m;

            return spike || vol;
        }

        // =====================================================
        // MULTI TF TREND
        // =====================================================

        private bool MultiTFTrendOK(
            MarketRegimeResult r1h,
            MarketRegimeResult r1d)
        {
            bool up1h = r1h.Regime is MarketRegime.StrongUpTrend or MarketRegime.UpTrend;
            bool up1d = r1d.Regime is MarketRegime.StrongUpTrend or MarketRegime.UpTrend;

            bool down1h = r1h.Regime is MarketRegime.StrongDownTrend or MarketRegime.DownTrend;
            bool down1d = r1d.Regime is MarketRegime.StrongDownTrend or MarketRegime.DownTrend;

            return (up1h && up1d) || (down1h && down1d);
        }

        // =====================================================
        // LOGGING
        // =====================================================

        private void LogResult(string symbol, HighTFResult r)
        {
            _logger.LogInformation(
            """
            ────────────────────────────────────────────────
            🔍 HighTimeframeSafetyFilter: {symbol}
            1H strong: {strong1h}
            1D strong: {strong1d}
            SwingMode: {swing}
            MultiTF trend OK: {multi}
            Anti Liquidity Grab: {liq}
            SwingRiskMult: {risk}
            """,
            symbol,
            r.StrongTrend1H,
            r.StrongTrend1D,
            r.SwingModeEnabled,
            r.MultiTFConfirmed,
            r.AntiLiquidityGrab,
            r.SwingRiskMultiplier);
        }
    }

    // =====================================================
    // RESULT MODEL
    // =====================================================

    public class HighTFResult
    {
        public bool StrongTrend1H { get; set; }
        public bool StrongTrend1D { get; set; }

        public bool MultiTFConfirmed { get; set; }

        public bool AntiLiquidityGrab { get; set; }

        public bool SwingModeEnabled { get; set; } = false;

        public decimal SwingRiskMultiplier { get; set; } = 1.0m;
    }
}