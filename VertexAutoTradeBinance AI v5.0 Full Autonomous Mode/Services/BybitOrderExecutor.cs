using Bybit.Net.Clients;
using Bybit.Net.Enums;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services;

/// <summary>
/// Phase-2: place CORE market entries on Bybit USDT linear (V5).
/// Parallel to Binance OrderExecutor — same signal/qty/leverage inputs.
/// Failures are logged and returned; they do not throw into TradingWorker.
/// </summary>
public sealed class BybitOrderExecutor
{
    private readonly BybitClientFactory _factory;
    private readonly ILogger<BybitOrderExecutor> _log;

    public BybitOrderExecutor(BybitClientFactory factory, ILogger<BybitOrderExecutor> log)
    {
        _factory = factory;
        _log = log;
    }

    public async Task<OrderResult> ExecuteAsync(
        TradeSignal signal,
        decimal qty,
        decimal leverage,
        CancellationToken ct = default)
    {
        if (signal == null)
            return Fail("null signal");
        if (qty <= 0)
            return Fail("qty <= 0");

        var symbol = (signal.Symbol ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(symbol))
            return Fail("empty symbol");

        BybitRestClient client;
        try
        {
            client = _factory.CreateRestClient();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[BYBIT] no client for {sym}", symbol);
            return Fail("no credentials: " + ex.Message);
        }

        int lev = (int)Math.Clamp(Math.Round(leverage <= 0 ? 5m : leverage), 1, 100);

        // Leverage (buy/sell same for one-way)
        try
        {
            var levRes = await client.V5Api.Account.SetLeverageAsync(
                Category.Linear,
                symbol,
                lev,
                lev,
                ct: ct).ConfigureAwait(false);

            if (!levRes.Success)
            {
                // 110043 = leverage not modified — OK
                var code = levRes.Error?.Code;
                if (code != 110043)
                    _log.LogWarning("[BYBIT][{sym}] SetLeverage {lev}x failed: {err}",
                        symbol, lev, levRes.Error?.Message);
            }
            else
            {
                _log.LogInformation("[BYBIT][{sym}] leverage={lev}x", symbol, lev);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[BYBIT][{sym}] SetLeverage exception — continuing", symbol);
        }

        var side = signal.Side == SignalSide.Buy ? OrderSide.Buy : OrderSide.Sell;

        decimal? stopLoss = signal.StopLoss > 0 ? signal.StopLoss : null;
        decimal? takeProfit = null;
        if (signal.TakeProfits != null && signal.TakeProfits.Count > 0 && signal.TakeProfits[0] > 0)
            takeProfit = signal.TakeProfits[0];
        else if (signal.TakeProfit is decimal tp && tp > 0)
            takeProfit = tp;

        try
        {
            // Market entry on linear USDT perp (one-way mode positionIdx=0)
            var orderRes = await client.V5Api.Trading.PlaceOrderAsync(
                category: Category.Linear,
                symbol: symbol,
                side: side,
                type: NewOrderType.Market,
                quantity: qty,
                stopLoss: stopLoss,
                takeProfit: takeProfit,
                positionIdx: 0,
                ct: ct).ConfigureAwait(false);

            if (!orderRes.Success)
            {
                _log.LogWarning("[BYBIT][{sym}] PlaceOrder failed: {err}",
                    symbol, orderRes.Error?.Message);
                return Fail(orderRes.Error?.Message ?? "PlaceOrder failed");
            }

            var orderId = orderRes.Data?.OrderId ?? "?";
            _log.LogInformation(
                "[BYBIT][{sym}] MARKET {side} qty={qty} lev={lev}x orderId={id} SL={sl} TP={tp}",
                symbol, side, qty, lev, orderId, stopLoss, takeProfit);

            return new OrderResult
            {
                Success = true,
                Error = string.Empty
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[BYBIT][{sym}] execution exception", symbol);
            return Fail(ex.Message);
        }
    }

    private static OrderResult Fail(string err) =>
        new() { Success = false, Error = err ?? "unknown" };
}
