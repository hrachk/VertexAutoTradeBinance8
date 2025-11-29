namespace VertexAutoTradeBinance8.API.Models
{
    public class ApiClusterFilterResultDto
    {
        public string Symbol { get; set; } = string.Empty;

        public bool Blocked { get; set; }
        public bool Adjusted { get; set; }

        public decimal EntryOld { get; set; }
        public decimal EntryNew { get; set; }

        public decimal SlOld { get; set; }
        public decimal SlNew { get; set; }
    }
}
