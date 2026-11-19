using Binance.Net.Enums;
using System.Text.Json.Serialization;

namespace VertexAutoTradeBinance8.Models
{
    public enum TradeExecutionStatus
    {
        SignalCreated,          // только сигнал
        OrderCreated,           // ордер создан, ждём fill
        OrderPartiallyFilled,   // частичное исполнение (если нужно)
        PositionOpened,         // позиция открыта (order filled)
        PositionClosedTp,       // закрыто по TP
        PositionClosedSl,       // закрыто по SL
        PositionClosedManual,   // ручное закрытие
        PositionError,          // ошибка при исполнении
        Cancelled,              // отменено
    }

    public class ExecutedSignalRecord
    {
        /// <summary>Уникальный id записи (раньше обновления шли «по последней записи символа» → закрытие писалось не в ту сделку).</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Symbol { get; set; } = string.Empty;

        /// <summary>Время создания сигнала / открытия. НЕ перезаписывается при закрытии.</summary>
        public DateTime Time { get; set; }

        public DateTime? OpenedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public string? CloseReason { get; set; }
        public decimal? Fees { get; set; }

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
    

        [JsonIgnore]
        public DateTime EffectiveCloseTime => ClosedAt ?? Time;

        [JsonIgnore]
        public bool IsClosed =>
            Status is TradeExecutionStatus.PositionClosedTp
                   or TradeExecutionStatus.PositionClosedSl
                   or TradeExecutionStatus.PositionClosedManual;

        [JsonIgnore]
        public bool IsOpen =>
            Status is TradeExecutionStatus.PositionOpened or TradeExecutionStatus.OrderPartiallyFilled;
    }
}
