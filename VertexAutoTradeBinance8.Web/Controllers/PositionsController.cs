using Microsoft.AspNetCore.Mvc;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;

[ApiController]
[Route("api/positions")]
public class PositionsController : ControllerBase
{
    private readonly PositionSupervisorService _supervisor;

    public PositionsController(PositionSupervisorService supervisor)
    {
        _supervisor = supervisor;
    }

    [HttpPost("action")]
    public async Task<IActionResult> Execute([FromBody] PositionActionRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Symbol))
            return BadRequest();

       // await _supervisor.HandleUiActionAsync(req);
        return Ok();
    }

}
