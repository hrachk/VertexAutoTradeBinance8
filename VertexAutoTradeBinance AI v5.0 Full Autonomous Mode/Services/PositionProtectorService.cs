using System;
using System.Linq;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class PositionProtectorService
    {
        private readonly ILogger<PositionProtectorService> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly SymbolInfoService _symbolInfo;
        private readonly AiSelfLearningService _aiLearning;
        private readonly TradeResultMonitorService _tradeMonitor;

        public PositionProtectorService(
            ILogger<PositionProtectorService> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo,
            AiSelfLearningService aiLearning,
            TradeResultMonitorService tradeMonitor)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
            _aiLearning = aiLearning;
            _tradeMonitor = tradeMonitor;
        }

        public async Task<bool> AutoExitIfDangerAsync(
            string symbol,
            decimal dangerPrice,
            PositionSide side,
            CancellationToken ct = default)
        {
            if (dangerPrice <= 0)
            {
                _logger.LogWarning("[PROTECTOR][{symbol}] dangerPrice <= 0 → skip", symbol);
                return false;
            }

            using var client = _factory.CreateRestClient();

            var posRes = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, null, ct);
            if (!posRes.Success || posRes.Data == null)
            {
                _logger.LogWarning("[PROTECTOR][{symbol}] Can't load positions: {err}", symbol, posRes.Error);
                return false;
            }

            var p = posRes.Data.FirstOrDefault(x => x.PositionSide == side);
            if (p == null || Math.Abs(p.Quantity) <= 0)
            {
                _logger.LogInformation("[PROTECTOR][{symbol}] No active {side} position", symbol, side);
                return false;
            }

            var qty = Math.Abs(p.Quantity);
            if (qty <= 0)
            {
                _logger.LogInformation("[PROTECTOR][{symbol}] Position qty=0", symbol);
                return false;
            }

            decimal mark = p.MarkPrice > 0 ? p.MarkPrice : p.EntryPrice;

            if (side == PositionSide.Long)
            {
                if (mark > dangerPrice)
                {
                    _logger.LogDebug(
                        "[PROTECTOR][{symbol}] LONG: mark {mark:F4} > danger {danger:F4} → ещё рано",
                        symbol, mark, dangerPrice);
                    return false;
                }
            }
            else
            {
                if (mark < dangerPrice)
                {
                    _logger.LogDebug(
                        "[PROTECTOR][{symbol}] SHORT: mark {mark:F4} < danger {danger:F4} → ещё рано",
                        symbol, mark, dangerPrice);
                    return false;
                }
            }

            var filters = await _symbolInfo.GetFuturesFiltersAsync(symbol);
            var tick = filters.tickSize <= 0 ? 0.0001m : filters.tickSize;

            decimal exitPrice = Math.Round(dangerPrice / tick) * tick;
            if (exitPrice <= 0)
            {
                _logger.LogWarning("[PROTECTOR][{symbol}] Computed exitPrice <= 0 → skip", symbol);
                return false;
            }

            var exitSide = side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

            var exitOrder = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: exitSide,
                type: FuturesOrderType.Market,
                reduceOnly: null,
                timeInForce: TimeInForce.GoodTillCanceled,
                quantity: qty,
                price: exitPrice,
                positionSide: side,
                ct: ct);

            if (!exitOrder.Success)
            {
                _logger.LogError(
                    "[PROTECTOR][{symbol}] AUTO-EXIT order ERROR: {err}",
                    symbol, exitOrder.Error);
                return false;
            }

            bool isWin = side == PositionSide.Long
                ? exitPrice > p.EntryPrice
                : exitPrice < p.EntryPrice;

            var sigSide = side == PositionSide.Short ? SignalSide.Sell : SignalSide.Buy;

            try
            {
                _aiLearning.RecordTrade(
                    symbol: symbol,
                    side: sigSide,
                    entry: p.EntryPrice,
                    exit: exitPrice,
                    regime: MarketRegime.Range
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[PROTECTOR][{symbol}] AiSelfLearning.RecordTrade error (AUTO-EXIT)", symbol);
            }

            _logger.LogWarning(
               "[PROTECTOR][AUTO-EXIT] {Symbol} side={Side}, qty={Qty}, exit={Exit:F4}, entry={Entry:F4}, win={Win}",
               symbol, side, qty, exitPrice, p.EntryPrice, isWin);

            return true;
        }
    }
}
