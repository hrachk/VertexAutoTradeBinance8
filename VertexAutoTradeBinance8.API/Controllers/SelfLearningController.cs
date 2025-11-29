using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.API.Controllers
{
    [ApiController]
    [Route("api/ai/[controller]")]
    public class SelfLearningController : ControllerBase
    {
        private readonly ILogger<SelfLearningController> _logger;
        private readonly AiSelfLearningService _learn;
        private readonly AiModelSnapshotService _snapshot;

        public SelfLearningController(
            ILogger<SelfLearningController> logger,
            AiSelfLearningService learn,
            AiModelSnapshotService snapshot)
        {
            _logger = logger;
            _learn = learn;
            _snapshot = snapshot;
        }

        /// <summary>
        /// Текущее состояние обучения (вся статистика по символам/режимам).
        /// </summary>
        [HttpGet]
        public IActionResult Get()
        {
            var state = _learn.ExportState(); // AiLearningState
            return Ok(state);
        }

        /// <summary>
        /// Сохранить снапшот состояния AI в storage.
        /// </summary>
        [HttpPost("save")]
        public async Task<IActionResult> Save(CancellationToken ct)
        {
            var state = _learn.ExportState();
            await _snapshot.SaveSnapshotAsync(state, ct);
            _logger.LogInformation("[API] AI-learning snapshot saved");
            return Ok(new { saved = true, timeUtc = DateTime.UtcNow });
        }

        /// <summary>
        /// Загрузить последний снапшот состояния AI.
        /// </summary>
        [HttpPost("load")]
        public async Task<IActionResult> Load(CancellationToken ct)
        {
            var state = await _snapshot.LoadLatestAsync(ct);
            if (state == null)
                return NotFound(new { loaded = false });

            _learn.ImportState(state);
            _logger.LogInformation("[API] AI-learning snapshot loaded");
            return Ok(new { loaded = true });
        }
    }
}
