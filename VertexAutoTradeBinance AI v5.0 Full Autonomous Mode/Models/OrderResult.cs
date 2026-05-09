// ============================================================================
// ORDER RESULT v5.0
// ============================================================================
namespace VertexAutoTradeBinance8.Models
{
    public class OrderResult
    {
        public bool Success { get; init; }
        public string Error { get; init; } = string.Empty;

        public decimal EntryPrice { get; init; }
        public decimal ExecutedQty { get; init; }
        public long OrderId { get; init; }

        public static OrderResult Fail(string error)
            => new OrderResult
            {
                Success = false,
                Error = error
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
