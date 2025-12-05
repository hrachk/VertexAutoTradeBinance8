using Microsoft.AspNetCore.Mvc;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.API.Controllers
{
    [ApiController]
    [Route("api/ai")]
    public class AiLearningController : ControllerBase
    {
        private readonly AiSelfLearningService _ai;
        private readonly ILogger<AiLearningController> _logger;

        public AiLearningController(
            AiSelfLearningService ai,
            ILogger<AiLearningController> logger)
        {
            _ai = ai;
            _logger = logger;
        }

        /// <summary>
        /// Сводная статистика по символам / режимам (WinRate и т.п.).
        /// </summary>
        [HttpGet("stats")]
        public ActionResult<AiLearningSnapshot> GetStats()
        {
            var snap = _ai.ExportState();
            return Ok(snap);
        }

        /// <summary>
        /// Последние MarketState (фоновые наблюдения рынка).
        /// </summary>
        [HttpGet("market-states")]
        public ActionResult<IReadOnlyList<AiSelfLearningService.MarketState>> GetMarketStates(
            [FromQuery] int maxCount = 150)
        {
            var states = _ai.GetRecentStates(maxCount);
            return Ok(states);
        }

        /// <summary>
        /// Последние трейды для обучения (entry/exit/PnL/regime).
        /// </summary>
        [HttpGet("trades")]
        public ActionResult<IReadOnlyList<AiSelfLearningService.TradeHistoryEntry>> GetTrades(
            [FromQuery] int maxCount = 100)
        {
            var trades = _ai.GetRecentTrades(maxCount);
            return Ok(trades);
        }
    }
}
