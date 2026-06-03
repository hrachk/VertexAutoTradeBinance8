using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Binance.Net.Objects.Models.Futures.AlgoOrders;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services
{

    public static class SimpleFileLogger
    {
        private static readonly object _lock = new();
        private static readonly string _path =
            Path.Combine(AppContext.BaseDirectory, "manual_orders.log");

        public static void Log(string message)
        {
            var line = $"{DateTime.UtcNow:O} {message}{Environment.NewLine}";
            lock (_lock)
            {
                File.AppendAllText(_path, line);
            }
        }
    }


    public class ManualOrderService
    {
        private readonly BinanceClientFactory _factory;
        private readonly ILogger<ManualOrderService> _logger;

        public ManualOrderService(BinanceClientFactory factory, ILogger<ManualOrderService> logger)
        {
            _factory = factory;
            _logger = logger;
        }

        private static decimal RoundToStep(decimal value, decimal step, bool floor = false)
        {
            if (step <= 0) return value;
            var x = value / step;
            return (floor ? Math.Floor(x) : Math.Round(x)) * step;
        }

        public async Task<(bool ok, string? error)> OpenPositionWithTpAsync(
            string symbol,
            string sideText,
            decimal requestedMargin,   // МАРЖА из UI (USDT)
            int leverage,
            decimal entryPrice,
            decimal tpPrice,
            CancellationToken ct = default)
        {
            using var client = _factory.CreateRestClientForWeb();

            var side = sideText == "Buy" ? OrderSide.Buy : OrderSide.Sell;
            var posSide = sideText == "Buy" ? PositionSide.Long : PositionSide.Short;

            // ------------------------------------------------------------------
            // 0) Баланс
            // ------------------------------------------------------------------
            var balRes = await client.UsdFuturesApi.Account.GetBalancesAsync(null, ct);
            if (!balRes.Success || balRes.Data == null)
            {
                var msg = $"Balance error: {balRes.Error?.Message}";
                _logger.LogError("[MANUAL][{sym}] {msg}", symbol, msg);
                SimpleFileLogger.Log($"[MANUAL][{symbol}] {msg}");
                return (false, msg);
            }

            var free = balRes.Data.FirstOrDefault(x => x.Asset == "USDT")?.AvailableBalance ?? 0m;
            if (free <= 0)
            {
                var msg = "Free balance is zero";
                _logger.LogError("[MANUAL][{sym}] {msg}", symbol, msg);
                SimpleFileLogger.Log($"[MANUAL][{symbol}] {msg}");
                return (false, msg);
            }

            // ------------------------------------------------------------------
            // 1) ExchangeInfo → фильтры и precision
            // ------------------------------------------------------------------
            var exInfo = await client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(ct);
            if (!exInfo.Success || exInfo.Data == null)
            {
                var msg = $"ExchangeInfo error: {exInfo.Error?.Message}";
                _logger.LogError("[MANUAL][{sym}] {msg}", symbol, msg);
                SimpleFileLogger.Log($"[MANUAL][{symbol}] {msg}");
                return (false, msg);
            }

            var symInfo = exInfo.Data.Symbols.FirstOrDefault(s => s.Name == symbol);
            if (symInfo == null)
            {
                var msg = $"Symbol {symbol} not found in ExchangeInfo";
                _logger.LogError("[MANUAL][{sym}] {msg}", symbol, msg);
                SimpleFileLogger.Log($"[MANUAL][{symbol}] {msg}");
                return (false, msg);
            }

            var priceFilter = symInfo.PriceFilter;
            var lotFilter = symInfo.LotSizeFilter;
            var minNotionalFilter = symInfo.MinNotionalFilter;

            decimal tickSize = priceFilter?.TickSize ?? 0.0001m;
            decimal stepSize = lotFilter?.StepSize ?? 0.0001m;
            decimal minQty = lotFilter?.MinQuantity ?? stepSize;
            decimal minNotional = minNotionalFilter?.MinNotional ?? 0m;

            int pricePrecision = symInfo.PricePrecision;      // ВАЖНО для NEAR и т.п.

            // pricePrecision → режем лишние знаки
            decimal RoundPrice(decimal p)
            {
                if (pricePrecision < 0) return p;
                return Math.Round(p, pricePrecision, MidpointRounding.ToZero);
            }

            entryPrice = RoundPrice(entryPrice);
            if (tpPrice > 0)
                tpPrice = RoundPrice(tpPrice);

            // Дополнительно по tickSize, на всякий случай
            entryPrice = RoundToStep(entryPrice, tickSize);
            if (tpPrice > 0)
                tpPrice = RoundToStep(tpPrice, tickSize);

            if (entryPrice <= 0)
            {
                var msg = "Invalid entryPrice after rounding";
                _logger.LogError("[MANUAL][{sym}] {msg}", symbol, msg); 
                SimpleFileLogger.Log($"[MANUAL][{symbol}] {msg}");
                return (false, msg);
            }

            // ------------------------------------------------------------------
            // 2) Маржа и количество
            // ------------------------------------------------------------------
            if (requestedMargin <= 0)
                requestedMargin = 1m;        // дефолт

            // Нельзя использовать больше free и возьмём запас 80%
            var maxMargin = free * 0.8m;
            var marginToUse = Math.Min(requestedMargin, maxMargin);
            if (marginToUse <= 0)
            {
                var msg = $"Requested margin too high. Free={free:F4}";
                _logger.LogError("[MANUAL][{sym}] {msg}", symbol, msg);
                SimpleFileLogger.Log($"[MANUAL][{symbol}] {msg}");
                return (false, msg);
            }

            // NOTIONAL = margin * leverage
            var targetNotional = marginToUse * leverage;
            if (targetNotional <= 0)
            {
                var msg = "Invalid notional (<=0)";
                _logger.LogError("[MANUAL][{sym}] {msg}", symbol, msg);
                SimpleFileLogger.Log($"[MANUAL][{symbol}] {msg}");
                return (false, msg);
            }

            // qty в монетах
            var qty = targetNotional / entryPrice;

            // подгоняем qty по шагу
            qty = RoundToStep(qty, stepSize, floor: true);
            if (qty < minQty)
            {
                var msg = $"Quantity {qty} < MinQty {minQty}";
                _logger.LogError("[MANUAL][{sym}] {msg}", symbol, msg);
                SimpleFileLogger.Log($"[MANUAL][{symbol}] {msg}");
                return (false, msg);
            }

            var notional = qty * entryPrice;
            if (minNotional > 0 && notional < minNotional)
            {
                var msg = $"Notional {notional:F4} < MinNotional {minNotional:F4}";
                _logger.LogError("[MANUAL][{sym}] {msg}", symbol, msg);
                SimpleFileLogger.Log($"[MANUAL][{symbol}] {msg}");
                return (false, msg);
            }

            var requiredMargin = notional / leverage;
            if (requiredMargin > free)
            {
                var msg = $"Required margin {requiredMargin:F4} > free {free:F4}";
                _logger.LogError("[MANUAL][{sym}] {msg}", symbol, msg);
                SimpleFileLogger.Log($"[MANUAL][{symbol}] {msg}");
                return (false, msg);
            }

            // ------------------------------------------------------------------
            // 3) Леверидж
            // ------------------------------------------------------------------
            var levRes = await client.UsdFuturesApi.Account.ChangeInitialLeverageAsync(
                symbol, leverage, ct: ct);
            if (!levRes.Success)
            {
                var msg = $"Change leverage error: {levRes.Error?.Message}";
                _logger.LogError("[MANUAL][{sym}] {msg}", symbol, msg);
                return (false, msg);
            }

            // ------------------------------------------------------------------
            // 4) ЛИМИТНЫЙ ВХОД
            // ------------------------------------------------------------------
            var openRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: symbol,
                side: side,
                type: FuturesOrderType.Limit,
                quantity: qty,
                price: entryPrice,
                positionSide: posSide,
                timeInForce: TimeInForce.GoodTillCanceled,
                ct: ct
            );

            if (!openRes.Success)
            {
                var msg = $"LIMIT entry error: {openRes.Error?.Message}";
                _logger.LogError("[MANUAL][{sym}] {msg}", symbol, msg);
                SimpleFileLogger.Log($"[MANUAL][{symbol}] {msg}");
                return (false, msg);
            }

            _logger.LogInformation(
                "[MANUAL][{sym}] LIMIT entry placed: qty={qty}, price={price}, orderId={id}",
                symbol, qty, entryPrice, openRes.Data.Id);
            SimpleFileLogger.Log($"[MANUAL][{symbol}] LIMIT entry placed: qty={qty}, price={entryPrice}, orderId={openRes.Data.Id}");

            // 5) TP через ALGO / CONDITIONAL TAKE_PROFIT_MARKET (без SL)
            if (tpPrice <= 0)
                return (true, null);

            var tpSide = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

            var tpRes = await client.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
                symbol: symbol,
                side: tpSide,
                type: ConditionalOrderType.TakeProfitMarket,  // ВАЖНО: *_MARKET
                quantity: null,                               // при closePosition = true
                price: null,                                  // market-исполнение
                positionSide: posSide,
                timeInForce: null,
                reduceOnly: null,
                clientOrderId: null,
                triggerPrice: tpPrice,                        // триггер TP
                activationPrice: null,
                callbackRate: null,
                workingType: WorkingType.Mark,               // или Contract, как хочешь
                closePosition: true,                          // закрыть всю позицию
                priceProtect: null,
                priceMatch: null,
                selfTradePreventionMode: null,
                goodTillDate: null,
                receiveWindow: null,
                ct: ct
            );

            if (!tpRes.Success)
            {
                var msg = $"TP conditional place error: {tpRes.Error?.Message}";
                _logger.LogError("[MANUAL][{sym}] {msg}", symbol, msg);
                return (false, msg);
            }

            _logger.LogInformation(
                "[MANUAL][{sym}] TP conditional placed: tp={tp}, algoId={id}",
                symbol, tpPrice, tpRes.Data.Id
            ); 
            SimpleFileLogger.Log($"[MANUAL][{symbol}] TP conditional placed: tp={tpPrice}, algoId={tpRes.Data.Id}");

            return (true, null);
        }

    }
}
