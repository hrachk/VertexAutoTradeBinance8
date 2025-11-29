using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Linq;
using System.Threading.Tasks;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Helpers;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.API.Controllers
{
    [ApiController]
    [Route("api/ai/[controller]")]
    public class MarketRegimeController : ControllerBase
    {
        private readonly ILogger<MarketRegimeController> _logger;
        private readonly BinanceOptions _binance;
        private readonly TradingOptions _trading;
        private readonly MarketDataService _marketData;
        private readonly AiMarketRegimeService _regime;

        public MarketRegimeController(
            ILogger<MarketRegimeController> logger,
            IOptions<BinanceOptions> binanceOptions,
            IOptions<TradingOptions> tradingOptions,
            MarketDataService marketData,
            AiMarketRegimeService regime)
        {
            _logger = logger;
            _binance = binanceOptions.Value;
            _trading = tradingOptions.Value;
            _marketData = marketData;
            _regime = regime;
        }

        /// <summary>
        /// AI-режим рынка по инструменту (trend/range/chop).
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Get(
            [FromQuery] string? symbol = null,
            [FromQuery] string? timeframe = null)
        {
            symbol ??= _binance.Symbols.First();

            // "5m" / "1h" и т.п.
            var tfStr = timeframe ?? _trading.TimeframeMinutes.ToTimeframeString();
            var tf = tfStr.ToKlineInterval();

            var klines = await _marketData.GetKlines(symbol, tf, 200);
            if (klines.Count == 0)
                return BadRequest(new { error = "No klines for symbol/timeframe" });

            var result = _regime.DetectRegime(symbol, tf, klines);

            return Ok(new
            {
                result.Symbol,
                result.Timeframe,
                Regime = result.Regime.ToString(),
                SlopePercent = result.TrendSlopePercent,
                VolatilityPercent = result.VolatilityPercent,
                DeviationScore = result.DeviationScore
            });
        }
    }
}
