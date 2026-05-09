using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Linq;
using System.Threading.Tasks;
using Binance.Net.Enums;
using VertexAutoTradeBinance8.API.Models;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Helpers;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.API.Controllers
{
    [ApiController]
    [Route("api/ai/[controller]")]
    public class LiquidityController : ControllerBase
    {
        private readonly ILogger<LiquidityController> _logger;
        private readonly BinanceOptions _binance;
        private readonly TradingOptions _trading;
        private readonly MarketDataService _marketData;
        private readonly LiquidityGuardService _guard;
        private readonly AiLiquidityClusterService _clusters;

        public LiquidityController(
            ILogger<LiquidityController> logger,
            IOptions<BinanceOptions> binanceOptions,
            IOptions<TradingOptions> tradingOptions,
            MarketDataService marketData,
            LiquidityGuardService guard,
            AiLiquidityClusterService clusters)
        {
            _logger = logger;
            _binance = binanceOptions.Value;
            _trading = tradingOptions.Value;
            _marketData = marketData;
            _guard = guard;
            _clusters = clusters;
        }

        /// <summary>
        /// LiquidityGuard по последним свечам: low-volume / stop-hunt.
        /// </summary>
        [HttpGet("guard")]
        public async Task<IActionResult> Guard(
            [FromQuery] string? symbol = null,
            [FromQuery] string? timeframe = null,
            [FromQuery] string side = "Buy")
        {
            symbol ??= _binance.Symbols.First();

            var tfStr = timeframe ?? _trading.TimeframeMinutes.ToTimeframeString();
            var tf = tfStr.ToKlineInterval();

            var klines = await _marketData.GetKlines(symbol, tf, 200);
            if (klines.Count == 0)
                return BadRequest(new { error = "No klines for symbol/timeframe" });

            var signalSide = side.ToLower() switch
            {
                "buy" => SignalSide.Buy,
                "sell" => SignalSide.Sell,
                _ => SignalSide.Buy
            };

            var res = _guard.Analyze(symbol, tf, klines, signalSide, superSignal: false);

            return Ok(new
            {
                symbol,
                timeframe = tf.ToString(),
                block = res.Block,
                reason = res.Reason.ToString(),
                details = res.Details
            });
        }

        /// <summary>
        /// Прогнать вход через AI-кластерный фильтр стакана.
        /// Может вернуть null (опасная зона) или скорректировать SL/Entry.
        /// </summary>
        [HttpPost("filter-entry")]
        public IActionResult FilterEntry([FromBody] LiquidityFilterRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Symbol))
                return BadRequest(new { error = "Symbol is required" });

            var signal = new TradeSignal
            {
                Symbol = req.Symbol,
                Side = req.Side,
                EntryPrice = req.EntryPrice,
                StopLoss = req.StopLoss,
                Time = DateTime.UtcNow,
                Timeframe = req.Timeframe ?? _trading.TimeframeMinutes.ToString() + "m"
            };

            var filtered = _clusters.FilterAndAdjust(signal);

            if (filtered == null)
            {
                // DANGER ZONE
                _logger.LogInformation("[API] LiquidityCluster blocked entry for {Symbol}", req.Symbol);
                return Ok(new
                {
                    symbol = req.Symbol,
                    blocked = true,
                    adjusted = false
                });
            }

            bool adjusted =
                filtered.StopLoss != req.StopLoss ||
                filtered.EntryPrice != req.EntryPrice;

            return Ok(new
            {
                symbol = filtered.Symbol,
                blocked = false,
                adjusted,
                entryOld = req.EntryPrice,
                entryNew = filtered.EntryPrice,
                slOld = req.StopLoss,
                slNew = filtered.StopLoss
            });
        }
    }
}
