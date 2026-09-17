using Microsoft.AspNetCore.Mvc;
using VertexAutoTradeBinance8.Web.Services.Twa;

namespace VertexAutoTradeBinance8.Web.Controllers;

[ApiController]
[Route("api/twa")]
public sealed class TwaController : ControllerBase
{
    private readonly TwaDashboardService _svc;

    public TwaController(TwaDashboardService svc) => _svc = svc;

    [HttpGet("summary")]
    public IActionResult Summary() => Ok(_svc.GetSummary());

    [HttpGet("positions")]
    public IActionResult Positions() => Ok(_svc.GetPositions());

    [HttpGet("stats")]
    public IActionResult Stats() => Ok(_svc.GetStats());

    [HttpPost("pause")]
    public IActionResult Pause([FromQuery] int minutes = 30)
    {
        _svc.RequestPause(minutes);
        return Ok(new { ok = true, minutes });
    }

    [HttpPost("kill")]
    public IActionResult Kill()
    {
        _svc.RequestKill();
        return Ok(new { ok = true, action = "kill+flatten_demo" });
    }

    [HttpPost("resume")]
    public IActionResult Resume()
    {
        _svc.RequestResume();
        return Ok(new { ok = true });
    }

    [HttpPost("close")]
    public IActionResult Close([FromQuery] string symbol)
    {
        _svc.CloseDemoSymbol(symbol);
        return Ok(new { ok = true, symbol });
    }
}
