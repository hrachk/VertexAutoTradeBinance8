using Binance.Net.Enums;

namespace VertexAutoTradeBinance8.Models
{
    public sealed class PositionActionRequest
    {
        public string Symbol { get; init; } = "";
        public PositionSide Side { get; init; }
        public PositionActionType Action { get; init; }
        public decimal? Price { get; init; } // для SL / TP
    }

    public enum PositionActionType
    {
        CloseMarket,
        UpdateStopLoss,
        UpdateTakeProfit
    }

    public enum CloseReason
    {
        ManualUi,
        Strategy,
        Emergency
    }
}
