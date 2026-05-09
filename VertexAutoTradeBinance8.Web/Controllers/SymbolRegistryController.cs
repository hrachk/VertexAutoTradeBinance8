using Microsoft.AspNetCore.Mvc;
using VertexAutoTradeBinance8.Services;

[ApiController]
[Route("api/symbols")]
public class SymbolRegistryController : ControllerBase
{
    private readonly SymbolRegistryService _registry;

    public SymbolRegistryController(SymbolRegistryService registry)
    {
        _registry = registry;
    }

    [HttpGet("snapshot")]
    public IActionResult GetSnapshot()
    {
        var snap = _registry.GetSnapshot();
        return Ok(snap);
    }
}
