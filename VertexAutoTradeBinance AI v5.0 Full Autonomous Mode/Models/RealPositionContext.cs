using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;

namespace VertexAutoTradeBinance8.Models
{
    /// <summary>
    /// Реальный контекст позиции: точная инфа о позиции, PnL, side, entry, SL/TP.
    /// Используется Supervisor, OrderExecutor, AI.
    /// </summary>
    public class RealPositionContext
    {
        public string Symbol { get; init; } = string.Empty;

        public decimal Quantity { get; init; }
        public decimal EntryPrice { get; init; }
        public decimal MarkPrice { get; set; }

        public PositionSide Side { get; init; }

        public decimal UnrealizedPnl { get; set; }
        public decimal Leverage { get; init; }

        public decimal? StopLoss { get; set; }
        public List<decimal> TakeProfits { get; set; } = new();

        public bool HasSL => StopLoss.HasValue && StopLoss.Value > 0;
        public bool HasTP => TakeProfits != null && TakeProfits.Count > 0;

        public DateTime LastUpdate { get; set; } = DateTime.UtcNow;

        public bool IsLong => Side == PositionSide.Long;
        public bool IsShort => Side == PositionSide.Short;

        /// <summary>
        /// Если режим BOTH, вычисляем реальное направление.
        /// </summary>
        public PositionSide ResolveDynamicSide()
        {
            if (Side != PositionSide.Both)
                return Side;

            if (MarkPrice > EntryPrice)
                return PositionSide.Long;

            return PositionSide.Short;
        }

        /// <summary>
        /// Проверяем, выгодно ли устанавливать SL — не “в упор”.
        /// </summary>
        public bool IsSlTooClose(decimal safeDistance)
        {
            if (!HasSL) return false;

            var resolved = ResolveDynamicSide();

            if (resolved == PositionSide.Long)
                return StopLoss >= MarkPrice - safeDistance;

            return StopLoss <= MarkPrice + safeDistance;
        }
    }
}
