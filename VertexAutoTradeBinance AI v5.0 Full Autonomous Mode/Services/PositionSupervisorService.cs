using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services
{
    public class PositionSupervisorService
    {
        private readonly ILogger<PositionSupervisorService> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly SymbolInfoService _symbolInfo;

        private const int MaxRetries = 3;
        private const decimal ManipSpikeAtr = 0.035m;     // 3.5% ATR — опасно
        private const decimal TrendDamageThreshold = -0.0045m;
        private const decimal SafeSlOffsetTicks = 20m;

        public PositionSupervisorService(
            ILogger<PositionSupervisorService> logger,
            BinanceClientFactory factory,
            SymbolInfoService symbolInfo)
        {
            _logger = logger;
            _factory = factory;
            _symbolInfo = symbolInfo;
        }

        // =====================================================================================
        // REAL CONTEXT
        // =====================================================================================
        private async Task<RealPositionContext?> ReadRealContextAsync(
            BinanceRestClient client,
            string symbol,
            CancellationToken ct)
        {
            var posResult = await client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, ct: ct);
            if (!posResult.Success || posResult.Data == null)
                return null;

            var p = posResult.Data.FirstOrDefault(x => x.Quantity != 0);
            if (p == null)
                return null;

            var price = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(symbol, ct: ct);
            decimal mark = price.Success ? price.Data.Price : p.MarkPrice;

            return new RealPositionContext
            {
                Symbol = symbol,
                Quantity = Math.Abs(p.Quantity),
                EntryPrice = p.EntryPrice,
                MarkPrice = mark,
                Side = p.PositionSide,
                UnrealizedPnl = p.UnrealizedPnl,
                Leverage = p.Leverage
            };
        }

        // =====================================================================================
        // SAFE STOP-LOSS CLAMP (исправляет "Order would immediately trigger")
        // =====================================================================================
        private async Task<decimal> SafeClampSlAsync(
            string symbol,
            decimal sl,
            decimal mark,
            PositionSide side)
        {
            decimal tick = await _symbolInfo.GetTickSizeAsync(symbol);
            decimal offset = tick * SafeSlOffsetTicks;

            if (side == PositionSide.Long)
            {
                decimal maxAllowed = mark - offset;
                return sl >= maxAllowed ? maxAllowed : sl;
            }
            else
            {
                decimal minAllowed = mark + offset;
                return sl <= minAllowed ? minAllowed : sl;
            }
        }

        // =====================================================================================
        // CHECK MANIPULATION (ATR spike)
        // =====================================================================================
        private bool IsManipulation(BinanceFuturesUsdtKline last, decimal atr)
        {
            if (atr <= 0) return false;

            decimal spike = (last.HighPrice - last.LowPrice) / last.ClosePrice;
            return spike >= ManipSpikeAtr;
        }

        // =====================================================================================
        // START V5.0 SUPERVISION
        // =====================================================================================
        public async Task SuperviseAsync(
            string symbol,
            TradeSignal lastSignal,
            CancellationToken ct)
        {
            using var client = _factory.CreateRestClient();

            var ctx = await ReadRealContextAsync(client, symbol, ct);
            if (ctx == null)
            {
                _logger.LogInformation("[SUPERVISOR] {S} no position", symbol);
                return;
            }

            // ----- Resolve BOTH → LONG/SHORT -----
            PositionSide realSide = ctx.ResolveDynamicSide();

            // =====================================================================================
            // LOAD KLINES (нужно для ATR, last candle, manipulation, trailing)
            // =====================================================================================
            var kl = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.OneMinute, limit: 50, ct: ct);
            if (!kl.Success || kl.Data == null) return;

            var klines = kl.Data.ToList();
            var last = klines.Last();
            var prev = klines[^2];

            decimal atr = CalculateAtr(klines);

            if (IsManipulation(last, atr))
            {
                _logger.LogWarning("[SUPERVISOR][{S}] Manipulation spike detected → HOLD", symbol);
                return;
            }

            // =====================================================================================
            // REPAIR SL/TP LOGIC
            // =====================================================================================
            await RepairSLTPAsync(client, ctx, lastSignal, realSide, atr, ct);

            // =====================================================================================
            // TRAILING STOP (V5.0 AI)
            // =====================================================================================
            await ApplyAiTrailingAsync(client, ctx, realSide, atr, ct);
        }

        // =====================================================================================
        // ATR helper
        // =====================================================================================
        private static decimal CalculateAtr(IReadOnlyList<BinanceFuturesUsdtKline> klines)
        {
            int n = klines.Count;
            int period = 14;

            if (n < period + 2)
                return 0;

            decimal sum = 0;
            for (int i = n - period; i < n; i++)
            {
                var c = klines[i];
                var p = klines[i - 1];

                decimal tr1 = c.HighPrice - c.LowPrice;
                decimal tr2 = Math.Abs(c.HighPrice - p.ClosePrice);
                decimal tr3 = Math.Abs(c.LowPrice - p.ClosePrice);

                sum += Math.Max(tr1, Math.Max(tr2, tr3));
            }

            return sum / period;
        }

        // =====================================================================================
        // REPAIR SL/TP
        // =====================================================================================
        private async Task RepairSLTPAsync(
            BinanceRestClient client,
            RealPositionContext ctx,
            TradeSignal sig,
            PositionSide realSide,
            decimal atr,
            CancellationToken ct)
        {
            var openOrders = await client.UsdFuturesApi.Trading.GetOpenOrdersAsync(ctx.Symbol, ct: ct);

            if (!openOrders.Success)
                return;

            bool hasSL = openOrders.Data.Any(o => o.Type == FuturesOrderType.StopMarket || o.Type == FuturesOrderType.Stop);
            bool hasTP = openOrders.Data.Any(o => o.Type == FuturesOrderType.TakeProfit || o.Type == FuturesOrderType.TakeProfitMarket);

            // SL отсутствует → создаём
            if (!hasSL)
            {
                _logger.LogWarning("[SUPERVISOR][{S}] SL восстановлен (user removed / not placed)", ctx.Symbol);

                decimal rawSl = sig.StopLoss;
                decimal safeSl = await SafeClampSlAsync(ctx.Symbol, rawSl, ctx.MarkPrice, realSide);

                await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: ctx.Symbol,
                    side: realSide == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                    type: FuturesOrderType.StopMarket,
                    stopPrice: safeSl,
                    quantity: ctx.Quantity,
                    positionSide: ctx.Side,
                    reduceOnly: true,
                    workingType: WorkingType.Mark,
                    ct: ct);
            }

            // TP отсутствует → создаём
            if (!hasTP && sig.TakeProfits != null && sig.TakeProfits.Count > 0)
            {
                _logger.LogWarning("[SUPERVISOR][{S}] TP восстановлен", ctx.Symbol);

                decimal part = ctx.Quantity / sig.TakeProfits.Count;

                foreach (var t in sig.TakeProfits)
                {
                    await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                        symbol: ctx.Symbol,
                        side: realSide == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                        type: FuturesOrderType.TakeProfitMarket,
                        stopPrice: t,
                        quantity: part,
                        positionSide: ctx.Side,
                        reduceOnly: true,
                        workingType: WorkingType.Mark,
                        ct: ct);
                }
            }
        }

        // =====================================================================================
        // AI TRAILING V5.0
        // =====================================================================================
        private async Task ApplyAiTrailingAsync(
            BinanceRestClient client,
            RealPositionContext ctx,
            PositionSide realSide,
            decimal atr,
            CancellationToken ct)
        {
            // Long trailing
            if (realSide == PositionSide.Long && ctx.MarkPrice > ctx.EntryPrice + atr * 1.0m)
            {
                decimal newSl = ctx.MarkPrice - atr * 0.8m;
                newSl = await SafeClampSlAsync(ctx.Symbol, newSl, ctx.MarkPrice, realSide);

                _logger.LogInformation("[SUPERVISOR][{S}] TRAILING LONG → SL={SL:F2}", ctx.Symbol, newSl);

                await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: ctx.Symbol,
                    side: OrderSide.Sell,
                    type: FuturesOrderType.StopMarket,
                    stopPrice: newSl,
                    quantity: ctx.Quantity,
                    positionSide: ctx.Side,
                    reduceOnly: true,
                    workingType: WorkingType.Mark,
                    ct: ct);
            }

            // Short trailing
            if (realSide == PositionSide.Short && ctx.MarkPrice < ctx.EntryPrice - atr * 1.0m)
            {
                decimal newSl = ctx.MarkPrice + atr * 0.8m;
                newSl = await SafeClampSlAsync(ctx.Symbol, newSl, ctx.MarkPrice, realSide);

                _logger.LogInformation("[SUPERVISOR][{S}] TRAILING SHORT → SL={SL:F2}", ctx.Symbol, newSl);

                await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: ctx.Symbol,
                    side: OrderSide.Buy,
                    type: FuturesOrderType.StopMarket,
                    stopPrice: newSl,
                    quantity: ctx.Quantity,
                    positionSide: ctx.Side,
                    reduceOnly: true,
                    workingType: WorkingType.Mark,
                    ct: ct);
            }
        }
    }
}
