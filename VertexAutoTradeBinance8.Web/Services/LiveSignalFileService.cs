using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace VertexAutoTradeBinance8.Web.Services
{
    /// <summary>
    /// Reads live_signals.json written by the Engine's LiveSignalService.
    /// Used by /market page to show confirmed signals in real-time.
    /// </summary>
    public class LiveSignalFileService
    {
        private readonly string _filePath;

        public LiveSignalFileService(IConfiguration cfg)
        {
            var root = cfg["SharedData:Root"] ?? @"C:\Vertex\Engines\client_001";
            _filePath = Path.Combine(root, "live_signals.json");
        }

        public async Task<List<LiveSignalDto>> LoadAsync()
        {
            try
            {
                if (!File.Exists(_filePath)) return new();
                var json = await File.ReadAllTextAsync(_filePath);
                return JsonSerializer.Deserialize<List<LiveSignalDto>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new();
            }
            catch { return new(); }
        }
    }

    public class LiveSignalDto
    {
        public string Symbol { get; set; } = "";
        public string Side { get; set; } = "";
        public DateTime Time { get; set; }
        public decimal Entry { get; set; }
        public decimal StopLoss { get; set; }
        public List<decimal> TakeProfits { get; set; } = new();
        public decimal? EntryRangeLow { get; set; }
        public decimal? EntryRangeHigh { get; set; }
        public int Confidence { get; set; }
        public int Score { get; set; }
        public string Reason { get; set; } = "";
        public decimal Atr { get; set; }
    }
}
