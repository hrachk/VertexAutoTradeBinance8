using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.API.Models;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly ILogger<OrdersController> _logger;
    private readonly BinanceOptions _binanceOptions;
    private readonly TradingOptions _tradingOptions;
    private readonly RiskManager _riskManager;
    private readonly OrderExecutor _executor;

    public OrdersController(
        ILogger<OrdersController> logger,
        IOptions<BinanceOptions> binanceOptions,
        IOptions<TradingOptions> tradingOptions,
        RiskManager riskManager,
        OrderExecutor executor)
    {
        _logger = logger;
        _binanceOptions = binanceOptions.Value;
        _tradingOptions = tradingOptions.Value;
        _riskManager = riskManager;
        _executor = executor;
    }

    /// <summary>
    /// /api/orders/entry — открыть сделку по сигналу (ручной вызов).
    /// Кол-во рассчитывается через RiskManager.
    /// </summary>
    [HttpPost("entry")]
    public async Task<IActionResult> CreateOrder([FromBody] OrderEntryRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (request.RiskMultiplier <= 0m)
            request.RiskMultiplier = 1m;

        var signal = new TradeSignal
        {
            Symbol = request.Symbol,
            Side = request.Side,
            EntryPrice = request.EntryPrice,
            StopLoss = request.StopLoss,
            Timeframe = $"{_tradingOptions.TimeframeMinutes}m",
            Time = DateTime.UtcNow
        };

        // риск как в TradingWorker, но без AI-grade – используем переданный multiplier
        var qty = await _riskManager.CalculateSafeQty(
            signal.Symbol,
            signal.EntryPrice,
            signal.StopLoss,
            request.RiskMultiplier);

        if (qty <= 0m)
            return BadRequest(new { error = "Calculated quantity is 0. Check risk settings / prices." });

        await _executor.ExecuteAsync(signal, qty, ct);

        _logger.LogInformation("Manual order created via API: {Symbol} {Side} qty={Qty}",
            signal.Symbol, signal.Side, qty);

        return Ok(new
        {
            message = "Order created",
            symbol = signal.Symbol,
            side = signal.Side.ToString(),
            quantity = qty,
            entryPrice = signal.EntryPrice,
            stopLoss = signal.StopLoss
        });
    }

    /// <summary>
    /// /api/orders/close — пока заглушка, можно будет позже связать с PositionProtectorService.
    /// </summary>
    [HttpPost("close")]
    public IActionResult CloseOrderPlaceholder()
    {
        // Здесь позже можно будет использовать PositionProtectorService / BinanceClientFactory
        return Ok(new { message = "Close endpoint stub. Implement close logic later." });
    }

    /// <summary>
    /// /api/orders/super-signal — заглушка под будущий супер-сигнал.
    /// </summary>
    [HttpGet("super-signal")]
    public IActionResult SuperSignalPlaceholder()
    {
        return Ok(new { message = "Super-signal endpoint stub. To be implemented with combined AI + Liquidity." });
    }
}
