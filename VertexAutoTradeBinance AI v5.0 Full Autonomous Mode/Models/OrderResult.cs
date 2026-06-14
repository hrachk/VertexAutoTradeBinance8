// ============================================================================
// ORDER RESULT v5.1
// ============================================================================
namespace VertexAutoTradeBinance8.Models
{
    public class OrderResult
    {
        public bool Success { get; init; }
        public string Error { get; init; } = string.Empty;

        // Детальная причина от Binance API
        public int? BinanceErrorCode { get; init; }
        public string? BinanceErrorMessage { get; init; }

        public decimal EntryPrice { get; init; }
        public decimal ExecutedQty { get; init; }
        public long OrderId { get; init; }

        public static OrderResult Fail(string error, int? code = null, string? binanceMsg = null)
            => new OrderResult
            {
                Success = false,
                Error = error,
                BinanceErrorCode = code,
                BinanceErrorMessage = binanceMsg
            };

        public static OrderResult Successs(decimal entryPrice, decimal qty, long orderId)
            => new OrderResult
            {
                Success = true,
                EntryPrice = entryPrice,
                ExecutedQty = qty,
                OrderId = orderId
            };
    }
}
