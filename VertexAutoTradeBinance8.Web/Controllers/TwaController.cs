using Microsoft.AspNetCore.Mvc;
using VertexAutoTradeBinance8.Web.Services.Auth;
using VertexAutoTradeBinance8.Web.Services.Twa;

namespace VertexAutoTradeBinance8.Web.Controllers;

[ApiController]
[Route("api/twa")]
public sealed class TwaController : ControllerBase
{
    private readonly TwaDashboardService _svc;
    private readonly SessionTokenService _tokens;
    private readonly ClientDbService _clients;

    public TwaController(TwaDashboardService svc, SessionTokenService tokens, ClientDbService clients)
    {
        _svc = svc;
        _tokens = tokens;
        _clients = clients;
    }

    [HttpGet("summary")]
    public async Task<IActionResult> Summary()
        => await IsAuthenticatedAsync() ? Ok(_svc.GetSummary()) : Unauthorized();

    [HttpGet("positions")]
    public async Task<IActionResult> Positions()
        => await IsAuthenticatedAsync() ? Ok(_svc.GetPositions()) : Unauthorized();

    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
        => await IsAuthenticatedAsync() ? Ok(_svc.GetStats()) : Unauthorized();

    [HttpPost("pause")]
    public async Task<IActionResult> Pause([FromQuery] int minutes = 30)
    {
        if (!await IsAuthenticatedAsync().ConfigureAwait(false)) return Unauthorized();
        _svc.RequestPause(minutes);
        return Ok(new { ok = true, minutes });
    }

    [HttpPost("kill")]
    public async Task<IActionResult> Kill()
    {
        if (!await IsAuthenticatedAsync().ConfigureAwait(false)) return Unauthorized();
        _svc.RequestKill();
        return Ok(new { ok = true, action = "kill+flatten_demo" });
    }

    [HttpPost("resume")]
    public async Task<IActionResult> Resume()
    {
        if (!await IsAuthenticatedAsync().ConfigureAwait(false)) return Unauthorized();
        _svc.RequestResume();
        return Ok(new { ok = true });
    }

    [HttpPost("close")]
    public async Task<IActionResult> Close([FromQuery] string symbol)
    {
        if (!await IsAuthenticatedAsync().ConfigureAwait(false)) return Unauthorized();
        _svc.CloseDemoSymbol(symbol);
        return Ok(new { ok = true, symbol });
    }

    private async Task<bool> IsAuthenticatedAsync()
    {
        if (!Request.Cookies.TryGetValue(AuthSessionService.CookieName, out var token) ||
            string.IsNullOrWhiteSpace(token))
            return false;

        var clientId = await _tokens.ValidateAsync(token).ConfigureAwait(false);
        if (clientId is null) return false;

        var client = await _clients.FindByIdAsync(clientId).ConfigureAwait(false);
        return client?.IsActive == true;
    }
}
