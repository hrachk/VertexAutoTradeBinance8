namespace VertexAutoTradeBinance8.Models
{
    public class LearningEvent
    {
        public string Type { get; set; }
        public string Side { get; set; }
        public decimal Entry { get; set; }
        public decimal StopLoss { get; set; }
        public decimal TakeProfit { get; set; }
        public decimal Result { get; set; }
        public string Reason { get; set; }
        public DateTime Time { get; set; }
    }
}
