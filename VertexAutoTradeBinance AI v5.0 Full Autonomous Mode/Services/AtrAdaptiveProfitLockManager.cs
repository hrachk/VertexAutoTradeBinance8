using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using Binance.Net.Objects.Models.Futures;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace VertexAutoTradeBinance8.Services.Trading
{
    /// <summary>
    /// Institutional-grade ATR adaptive staircase profit locker
    /// Replaces fixed BE and fixed trailing
    /// </summary>
    public sealed class AtrAdaptiveProfitLockManager
    {
        private readonly ILogger _logger;

        // tracks current staircase level
        private readonly ConcurrentDictionary<string, int> _steps = new();

        // tracks partial closes
        private readonly ConcurrentDictionary<string, int> _partials = new();

        public AtrAdaptiveProfitLockManager(ILogger logger)
        {
            _logger = logger;
        }

        private static string Key(string symbol, PositionSide side)
            => symbol + "_" + side;

        /// <summary>
        /// Main entry point
        /// Call on EVERY supervise tick
        /// </summary>
        public async Task ProcessAsync(
            IBinanceRestClient client,
            string symbol,
            BinancePositionDetailsUsdt pos,
            decimal atr,
            IReadOnlyCollection<BinanceFuturesOrder> openOrders,
            Func<decimal, Task> closePartialAsync,
            Func<decimal, Task> moveStopAsync,
            CancellationToken ct)
        {
            if (pos == null || pos.Quantity == 0)
                return;

            if (atr <= 0)
                return;

            var side = pos.PositionSide;

            var key = Key(symbol, side);

            var qty = Math.Abs(pos.Quantity);

            var entry = pos.EntryPrice;

            var mark = pos.MarkPrice;

            if (entry <= 0 || mark <= 0)
                return;

            var stepSize = atr * 0.7m;
            var lockOffset = atr * 0.45m;

            decimal profitDistance =
                side == PositionSide.Long
                ? mark - entry
                : entry - mark;

            if (profitDistance <= 0)
                return;

            var step = (int)(profitDistance / stepSize);

            if (step <= 0)
                return;

            var currentStep = _steps.GetOrAdd(key, 0);

            if (step <= currentStep)
                return;

            _steps[key] = step;

            decimal newSl =
                side == PositionSide.Long
                ? entry + step * stepSize - lockOffset
                : entry - step * stepSize + lockOffset;

            var slOrder =
                openOrders.FirstOrDefault(o =>
                    o.PositionSide == side &&
                    o.Type == FuturesOrderType.StopMarket);

            if (slOrder == null)
            {
                _logger.LogWarning(
                    "[ATR LOCK][{symbol}][{side}] placing initial SL={sl}",
                    symbol,
                    side,
                    newSl);

                await moveStopAsync(newSl);

                return;
            }

            decimal existingSl = slOrder.StopPrice ?? 0;

            bool improve =
                side == PositionSide.Long
                ? newSl > existingSl
                : newSl < existingSl;

            if (!improve)
                return;

            _logger.LogInformation(
                "[ATR LOCK][{symbol}][{side}] step={step} SL {old} → {new}",
                symbol,
                side,
                step,
                existingSl,
                newSl);

            await moveStopAsync(newSl);

            await HandlePartialAsync(
                symbol,
                side,
                step,
                qty,
                closePartialAsync,
                ct);
        }

        private async Task HandlePartialAsync(
            string symbol,
            PositionSide side,
            int step,
            decimal qty,
            Func<decimal, Task> closePartialAsync,
            CancellationToken ct)
        {
            var key = Key(symbol, side);

            var partialStage = _partials.GetOrAdd(key, 0);

            var partialEverySteps = 2;

            var expectedStage = step / partialEverySteps;

            if (expectedStage <= partialStage)
                return;

            var closeQty = qty * 0.25m;

            _partials[key] = expectedStage;

            _logger.LogWarning(
                "[ATR PARTIAL][{symbol}][{side}] step={step} close={qty}",
                symbol,
                side,
                closeQty);

            await closePartialAsync(closeQty);
        }

        public void Reset(string symbol, PositionSide side)
        {
            var key = Key(symbol, side);

            _steps.TryRemove(key, out _);

            _partials.TryRemove(key, out _);
        }
    }
}
