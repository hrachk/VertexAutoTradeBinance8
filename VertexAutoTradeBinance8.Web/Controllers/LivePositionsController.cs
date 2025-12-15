using Binance.Net.Enums;
using Microsoft.AspNetCore.Mvc;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.API.Controllers;

[ApiController]
[Route("api/exchange")]
public class LivePositionsController : ControllerBase
{
    private readonly BinanceClientFactory _factory;
    private readonly ILogger<LivePositionsController> _logger;

    public LivePositionsController(
        BinanceClientFactory factory,
        ILogger<LivePositionsController> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public sealed class LivePosDto
    {
        public string Symbol { get; set; } = "";
        public string PositionSide { get; set; } = ""; // LONG / SHORT
        public decimal PositionAmt { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal MarkPrice { get; set; }
        public decimal UnrealizedPnl { get; set; }
        public decimal Notional { get; set; }
        public decimal? LiquidationPrice { get; set; }
        public decimal? IsolatedMargin { get; set; }
        public int? Leverage { get; set; }
        public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Binance-truth snapshot (SAFE for Web/UI)
    /// GET /api/exchange/live-positions?symbols=ICNTUSDT
    /// </summary>
    [HttpGet("live-positions")]
    public async Task<ActionResult<List<LivePosDto>>> GetLivePositions(
        [FromQuery] string symbols,
        CancellationToken ct)
    {
        var list = new List<LivePosDto>();

        if (string.IsNullOrWhiteSpace(symbols))
            return Ok(list);

        // ✅ SAFE: может вернуть null
        var client = _factory.TryCreateRestClient();
        if (client == null)
        {
            _logger.LogDebug("[LIVE] Binance private endpoints disabled (no API keys)");
            return Ok(list);
        }

        var arr = symbols
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(900)); // anti-freeze UI

        foreach (var sym in arr)
        {
            try
            {
                var res = await client.UsdFuturesApi.Account
                    .GetPositionInformationAsync(sym, null, cts.Token);

                if (!res.Success || res.Data == null)
                    continue;

                foreach (var p in res.Data)
                {
                    if (p.Quantity == 0)
                        continue;

                    list.Add(new LivePosDto
                    {
                        Symbol = p.Symbol,
                        PositionSide = p.PositionSide.ToString().ToUpperInvariant(),
                        PositionAmt = p.Quantity,
                        EntryPrice = p.EntryPrice,
                        MarkPrice = p.MarkPrice,
                        UnrealizedPnl = p.UnrealizedPnl,
                        Notional = p.Notional,
                        LiquidationPrice = p.LiquidationPrice,
                        IsolatedMargin = p.IsolatedMargin,
                        Leverage = p.Leverage,
                        TimeUtc = DateTime.UtcNow
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // таймаут допустим — UI обновится следующим тиком
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LIVE] failed for {Symbol}", sym);
            }
        }

        return Ok(list);
    }
}
