using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;

namespace VertexAutoTradeBinance8.API.Controllers;

[ApiController]
[Route("api/system/[controller]")]
public class StatusController : ControllerBase
{
    private readonly BinanceOptions _binance;
    private readonly TradingOptions _trading;

    public StatusController(
        IOptions<BinanceOptions> binance,
        IOptions<TradingOptions> trading)
    {
        _binance = binance.Value;
        _trading = trading.Value;
    }

    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new
        {
            status = "ok",
            timeUtc = DateTime.UtcNow,
            symbols = _binance.Symbols,
            timeframeMinutes = _trading.TimeframeMinutes,
            cooldownSeconds = _trading.CooldownSeconds,
            leverage = _trading.Leverage
        });
    }
}
