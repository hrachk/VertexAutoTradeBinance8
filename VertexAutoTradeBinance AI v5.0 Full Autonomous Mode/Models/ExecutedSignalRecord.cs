using Binance.Net.Enums;

namespace VertexAutoTradeBinance8.Models
{
    public enum TradeExecutionStatus
    {
        SignalCreated,          // только сигнал
        OrderCreated,           // ордер создан, ждём fill
        OrderPartiallyFilled,   // частичное исполнение
        PositionOpened,         // позиция открыта

        ProtectionComputed,     // ✅ SL/TP рассчитаны, ждём Supervisor

        PositionClosedTp,       // закрыто по TP
        PositionClosedSl,       // закрыто по SL
        PositionClosedManual,   // ручное закрытие
        PositionError,          // ошибка при исполнении
        Cancelled,              // отменено
        Blocked,              // Blocked
    }


    public class ExecutedSignalRecord
    {
        public string Symbol { get; set; } = string.Empty;
        public DateTime Time { get; set; }

        public SignalSide Side { get; set; }

        public decimal Entry { get; set; }
        public decimal StopLoss { get; set; }
        public List<decimal> TakeProfits { get; set; } = new();

        public decimal Qty { get; set; }
        public decimal Notional { get; set; }

        public TradeExecutionStatus Status { get; set; }

        public string Reason { get; set; } = string.Empty;

        // AI / режим
        public int OpportunityScore { get; set; }
        public decimal Atr { get; set; }
        public decimal Volatility { get; set; }
        public decimal Slope { get; set; }

        // Результат сделки (если закрыта)
        public decimal? ExitPrice { get; set; }
        public decimal? PnL { get; set; }
        public decimal? RoiPercent { get; set; }

        // Доп. инфа (например, AIrisk, regime и т.п.)
        public string Tags { get; set; } = string.Empty;

        public decimal? FilledEntryPrice { get; set; }

        public bool IsOpen =>
    Status == TradeExecutionStatus.OrderCreated ||
    Status == TradeExecutionStatus.OrderPartiallyFilled ||
    Status == TradeExecutionStatus.PositionOpened;
    }
}
