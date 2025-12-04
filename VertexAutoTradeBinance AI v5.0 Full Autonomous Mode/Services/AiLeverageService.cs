using System;
using System.Collections.Generic;
using System.Linq;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    /// <summary>
    /// AI-подбор безопасного плеча с учётом режима рынка, волатильности, корреляции и AI-риска.
    /// </summary>
    public class AiLeverageService
    {
        private readonly ILogger<AiLeverageService> _logger;
        private readonly AiMarketRegimeService _regime;
        private readonly AiCorrelationService _corr;
        private readonly AiSelfLearningService _aiRisk;

        public AiLeverageService(
            ILogger<AiLeverageService> logger,
            AiMarketRegimeService regime,
            AiCorrelationService corr,
            AiSelfLearningService aiRisk)
        {
            _logger = logger;
            _regime = regime;
            _corr = corr;
            _aiRisk = aiRisk;
        }

        /// <summary>
        /// Главный калькулятор плеча.
        /// </summary>
        public int CalculateSafeLeverage(
            string symbol,
            decimal price,
            decimal atr,
            decimal balanceUSDT,
            KlineInterval tf,
            IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            try
            {
                if (price <= 0 || balanceUSDT <= 0)
                    return 1;

                if (klines == null || klines.Count < 30)
                {
                    _logger.LogWarning("[LEV][{Symbol}] Not enough klines ({Count}) → fallback 3x",
                        symbol, klines?.Count ?? 0);
                    return 3;
                }

                // 1) Режим рынка (ТОЛЬКО DetectRegime — ничего больше нет)
                var mr = _regime.DetectRegime(symbol, tf, klines);

                int lev = mr.Regime switch
                {
                    MarketRegime.StrongUpTrend => 7,
                    MarketRegime.StrongDownTrend => 7,
                    MarketRegime.Range => 3,
                    MarketRegime.VolatileChop => 2,
                    _ => 3
                };

                var vol = mr.VolatilityPercent;   // ATR/price, 0.02 = 2%
                var slope = mr.TrendSlopePercent;   // 0.01 = +1%

                // 2) Волатильность
                if (vol < 0.010m)        // < 1% – тихий рынок
                    lev += 1;
                else if (vol > 0.040m)   // > 4% – бешеный
                    lev -= 2;
                else if (vol > 0.025m)   // 2.5–4%
                    lev -= 1;

                // 3) Качество тренда
                if (mr.Regime == MarketRegime.Range)
                {
                    if (Math.Abs(slope) < 0.002m)
                        lev -= 1; // глухой боковик
                }
                else if (Math.Abs(slope) < 0.003m)
                {
                    lev -= 1; // тренд слабый
                }

                // 4) Корреляция с BTC
                var corr = _corr.GetCorrelation("BTCUSDT", symbol);
                if (corr.HasValue && Math.Abs(corr.Value) > 0.90m)
                {
                    // Очень высокая корреляция с BTC → чуть уменьшаем плечо
                    lev -= 1;
                }

                // 5) AI-Risk (self-learning)
                var riskW = _aiRisk.GetDynamicRiskWeight(symbol, mr.Regime);
                if (riskW < 0.80m)
                    lev -= 1;    // AI считает инструмент опасным
                else if (riskW > 1.20m)
                    lev += 1;    // AI видит комфортный риск

                // 6) ATR-фильтр
                if (atr > 0)
                {
                    var atrPct = atr / price;
                    if (atrPct > 0.050m)       // > 5% ATR
                        lev -= 2;
                    else if (atrPct > 0.030m)  // 3–5%
                        lev -= 1;
                    else if (atrPct < 0.010m)  // < 1%
                        lev += 1;
                }

                // 7) Ограничения по депозиту
                if (balanceUSDT < 100m && lev > 3)
                    lev = 3;
                else if (balanceUSDT < 250m && lev > 5)
                    lev = 5;

                // 8) Жёсткий диапазон плеча
                if (lev < 1) lev = 1;
                if (lev > 10) lev = 10;

                _logger.LogInformation(
                    "[LEV][{Symbol}] regime={Regime} vol={Vol:P2} slope={Slope:P2} riskW={Risk:F2} → lev={Lev}",
                    symbol, mr.Regime, vol, slope, riskW, lev);

                return lev;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LEV][{Symbol}] ERROR in CalculateSafeLeverage → fallback 3x", symbol);
                return 3;
            }
        }
    }
}
