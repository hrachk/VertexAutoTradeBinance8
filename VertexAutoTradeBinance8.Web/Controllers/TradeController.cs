using Binance.Net.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.Web.Controllers;

[ApiController]
[Route("api/trade")]
public class TradeController : ControllerBase
{
    private readonly BinanceClientFactory _factory;
    private readonly ILogger<TradeController> _logger;
    private readonly TradingOptions _trading;

    public TradeController(
        BinanceClientFactory factory,
        IOptions<TradingOptions> trading,
        ILogger<TradeController> logger)
    {
        _factory = factory;
        _trading = trading.Value;
        _logger = logger;
    }

    [HttpGet("account")]
    public async Task<IActionResult> Account(CancellationToken ct)
    {
        try
        {
            using var client = _factory.CreateRestClient();
            var bal = await client.UsdFuturesApi.Account.GetBalancesAsync(ct: ct);
            if (!bal.Success)
                return Ok(new { ok = false, error = bal.Error?.Message ?? "balance failed", available = 0m, wallet = 0m });

            var usdt = bal.Data?.FirstOrDefault(x => x.Asset == "USDT");
            var available = usdt?.AvailableBalance ?? 0m;
            var wallet = usdt?.WalletBalance ?? available;

            var pos = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
            var open = new List<object>();
            decimal unrealized = 0;
            if (pos.Success && pos.Data != null)
            {
                foreach (var p in pos.Data.Where(x => x.Quantity != 0))
                {
                    unrealized += p.UnrealizedPnl;
                    open.Add(new
                    {
                        symbol = p.Symbol,
                        side = p.Quantity > 0 ? "LONG" : "SHORT",
                        qty = Math.Abs(p.Quantity),
                        entry = p.AveragePrice,
                        mark = p.MarkPrice,
                        pnl = p.UnrealizedPnl,
                        leverage = (int)p.Leverage,
                        margin = p.InitialMargin,
                        positionSide = p.PositionSide.ToString()
                    });
                }
            }

            return Ok(new
            {
                ok = true,
                available,
                wallet,
                unrealized,
                positions = open
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "account");
            return Ok(new { ok = false, error = ex.Message, available = 0m, wallet = 0m, positions = Array.Empty<object>() });
        }
    }

    public class PlaceRequest
    {
        public string Symbol { get; set; } = "BTCUSDT";
        public bool IsBuy { get; set; } = true;
        public decimal Quantity { get; set; }
        public string OrderType { get; set; } = "Market"; // Market | Limit
        public decimal? Price { get; set; }
        public int Leverage { get; set; } = 10;
        public decimal? StopLoss { get; set; }
        public decimal? TakeProfit { get; set; }
    }

    [HttpPost("place")]
    public async Task<IActionResult> Place([FromBody] PlaceRequest req, CancellationToken ct)
    {
        if (req.Quantity <= 0)
            return Ok(new { ok = false, error = "Quantity must be > 0" });

        try
        {
            using var client = _factory.CreateRestClient();
            var symbol = req.Symbol.ToUpperInvariant();

            // leverage
            try
            {
                await client.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, req.Leverage, ct: ct);
            }
            catch (Exception lex)
            {
                _logger.LogWarning(lex, "leverage set failed");
            }

            var side = req.IsBuy ? OrderSide.Buy : OrderSide.Sell;
            // Hedge mode: position side
            var posSide = req.IsBuy ? PositionSide.Long : PositionSide.Short;

            Binance.Net.Objects.Models.Futures.BinanceUsdFuturesOrder? orderData = null;
            string? err = null;

            if (string.Equals(req.OrderType, "Limit", StringComparison.OrdinalIgnoreCase) && req.Price is > 0)
            {
                var o = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: side,
                    type: FuturesOrderType.Limit,
                    quantity: req.Quantity,
                    price: req.Price,
                    timeInForce: TimeInForce.GoodTillCanceled,
                    positionSide: posSide,
                    ct: ct);
                if (!o.Success) err = o.Error?.Message;
                else orderData = o.Data;
            }
            else
            {
                var o = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: side,
                    type: FuturesOrderType.Market,
                    quantity: req.Quantity,
                    positionSide: posSide,
                    ct: ct);
                if (!o.Success) err = o.Error?.Message;
                else orderData = o.Data;
            }

            if (err != null || orderData == null)
                return Ok(new { ok = false, error = err ?? "order failed" });

            var entry = orderData.AverageFillPrice ?? orderData.Price ?? req.Price ?? 0m;
            long? slId = null, tpId = null;
            var closeSide = req.IsBuy ? OrderSide.Sell : OrderSide.Buy;

            if (req.StopLoss is > 0)
            {
                var sl = await client.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
                    symbol: symbol,
                    side: closeSide,
                    type: ConditionalOrderType.StopMarket,
                    quantity: req.Quantity,
                    triggerPrice: req.StopLoss,
                    positionSide: posSide,
                    workingType: WorkingType.Mark,
                    timeInForce: TimeInForce.GoodTillCanceled,
                    ct: ct);
                if (sl.Success) slId = sl.Data?.Id;
                else _logger.LogWarning("SL fail: {e}", sl.Error?.Message);
            }

            if (req.TakeProfit is > 0)
            {
                var tp = await client.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
                    symbol: symbol,
                    side: closeSide,
                    type: ConditionalOrderType.TakeProfitMarket,
                    quantity: req.Quantity,
                    triggerPrice: req.TakeProfit,
                    positionSide: posSide,
                    workingType: WorkingType.Mark,
                    timeInForce: TimeInForce.GoodTillCanceled,
                    ct: ct);
                if (tp.Success) tpId = tp.Data?.Id;
                else _logger.LogWarning("TP fail: {e}", tp.Error?.Message);
            }

            return Ok(new
            {
                ok = true,
                orderId = orderData.Id,
                avgPrice = entry,
                qty = orderData.QuantityFilled > 0 ? orderData.QuantityFilled : req.Quantity,
                slAlgoId = slId,
                tpAlgoId = tpId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "place order");
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    [HttpPost("close")]
    public async Task<IActionResult> Close([FromBody] CloseRequest req, CancellationToken ct)
    {
        try
        {
            using var client = _factory.CreateRestClient();
            var symbol = req.Symbol.ToUpperInvariant();
            var pos = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, ct: ct);
            if (!pos.Success)
                return Ok(new { ok = false, error = pos.Error?.Message });

            var p = pos.Data?.FirstOrDefault(x =>
                string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase) && x.Quantity != 0);
            if (p == null)
                return Ok(new { ok = false, error = "No open position" });

            var qty = Math.Abs(p.Quantity);
            var isLong = p.Quantity > 0;
            var side = isLong ? OrderSide.Sell : OrderSide.Buy;
            var posSide = p.PositionSide;

            var o = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: side,
                type: FuturesOrderType.Market,
                quantity: qty,
                positionSide: posSide,
                reduceOnly: posSide == PositionSide.Both ? true : null,
                ct: ct);

            if (!o.Success)
                return Ok(new { ok = false, error = o.Error?.Message });

            return Ok(new { ok = true, orderId = o.Data?.Id });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    public class CloseRequest
    {
        public string Symbol { get; set; } = "";
    }
}
