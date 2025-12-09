namespace VertexAutoTradeBinance8.Models
{
    public class AiLearningTradeMeta
    {
        public decimal? RunnerQty { get; set; }
        public decimal? Tp1Price { get; set; }
        public decimal? Tp2Start { get; set; }
        public List<decimal> Tp2Extensions { get; set; } = new();

        public bool ExhaustionDetected { get; set; }
        public string? ExhaustionLevel { get; set; }

        public bool SweepInFavor { get; set; }
        public bool SweepAgainst { get; set; }

        public decimal? FinalExitPrice { get; set; }
        public string? ExitReason { get; set; }
    }
}
