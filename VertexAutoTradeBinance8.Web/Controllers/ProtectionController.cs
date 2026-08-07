using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Microsoft.AspNetCore.Mvc;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Controllers;

/// <summary>
/// GET /api/protection/summary
///
/// Один запрос отвечает на главный вопрос консоли: какие позиции открыты и
/// под какими из них реально висит стоп на бирже. Источник истины — Binance,
/// а не внутреннее состояние движка: расхождение между ними как раз и есть
/// то, что нужно увидеть.
/// </summary>
[ApiController]
[Route("api/protection")]
public class ProtectionController : ControllerBase
{
    private readonly BinanceClientFactory _factory;
    private readonly ILogger<ProtectionController> _logger;

    public ProtectionController(
        BinanceClientFactory factory,
        ILogger<ProtectionController> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    [HttpGet("summary")]
    public async Task<ActionResult<ProtectionSummary>> GetSummary(CancellationToken ct)
    {
        var result = new ProtectionSummary();

        var client = _factory.TryCreateRestClient();
        if (client == null)
        {
            result.Error = "Ключи Binance не заданы — приватные эндпоинты недоступны.";
            return Ok(result);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));

        try
        {
            var posRes = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: cts.Token);

            if (!posRes.Success || posRes.Data == null)
            {
                result.Error = posRes.Error?.Message ?? "Binance не вернул позиции.";
                return Ok(result);
            }

            var live = posRes.Data.Where(p => p.Quantity != 0m).ToList();
            if (live.Count == 0)
                return Ok(result);

            // Открытые ордера тянем по символам, где реально есть позиция
            var symbols = live.Select(p => p.Symbol).Distinct(StringComparer.OrdinalIgnoreCase);
            var ordersBySymbol = new Dictionary<string, List<BinanceUsdFuturesOrder>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var sym in symbols)
            {
                var ordRes = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(sym, ct: cts.Token);
                ordersBySymbol[sym] = ordRes.Success && ordRes.Data != null
                    ? ordRes.Data.ToList()
                    : new();
            }

            foreach (var p in live)
            {
                var orders = ordersBySymbol.TryGetValue(p.Symbol, out var o)
                    ? o.Where(x => x.PositionSide == p.PositionSide).ToList()
                    : new List<BinanceUsdFuturesOrder>();

                var stops = orders
                    .Where(x => x.Type == FuturesOrderType.StopMarket || x.Type == FuturesOrderType.Stop)
                    .ToList();

                var takes = orders
                    .Where(x => x.Type == FuturesOrderType.TakeProfitMarket || x.Type == FuturesOrderType.TakeProfit)
                    .ToList();

                bool isLong = p.PositionSide == PositionSide.Long
                              || (p.PositionSide == PositionSide.Both && p.Quantity > 0);

                // При нескольких стопах показываем ближайший к рынку — он сработает первым
                decimal? stopPrice = stops.Count == 0
                    ? null
                    : isLong
                        ? stops.Max(x => x.StopPrice)
                        : stops.Min(x => x.StopPrice);

                decimal? tpPrice = takes.Count == 0
                    ? null
                    : isLong
                        ? takes.Min(x => x.StopPrice)
                        : takes.Max(x => x.StopPrice);

                result.Positions.Add(new PositionProtectionModel
                {
                    Symbol = p.Symbol,
                    PositionSide = p.PositionSide.ToString().ToUpperInvariant(),
                    Quantity = p.Quantity,
                    EntryPrice = p.EntryPrice,
                    MarkPrice = p.MarkPrice,
                    UnrealizedPnl = p.UnrealizedPnl,
                    Notional = p.Notional,
                    Leverage = p.Leverage,
                    LiquidationPrice = p.LiquidationPrice,
                    StopPrice = stopPrice > 0 ? stopPrice : null,
                    TakeProfitPrice = tpPrice > 0 ? tpPrice : null,
                    StopOrderCount = stops.Count
                });
            }

            result.Positions = result.Positions
                .OrderBy(x => x.HasStop)                 // незащищённые — наверх
                .ThenByDescending(x => Math.Abs(x.Notional))
                .ToList();

            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            result.Error = "Binance не ответил вовремя.";
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PROTECTION] summary failed");
            result.Error = "Не удалось получить состояние позиций.";
            return Ok(result);
        }
    }
}
