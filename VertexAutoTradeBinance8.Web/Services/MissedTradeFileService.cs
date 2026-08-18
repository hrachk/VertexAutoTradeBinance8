using System.Text.Json;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services
{
    public class MissedTradeFileService
    {
        private readonly string _filePath;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public MissedTradeFileService(IWebHostEnvironment env)
        {
          // After Publish: always next to the .exe / process
          _filePath = Path.Combine(
              Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
              "missed_trades.json");

        }
        public async Task<List<MissedTradeRecord>> LoadAsync()
        {
            if (!File.Exists(_filePath))
                return new List<MissedTradeRecord>();

            var json = await File.ReadAllTextAsync(_filePath);
            if (string.IsNullOrWhiteSpace(json))
                return new List<MissedTradeRecord>();

            return JsonSerializer.Deserialize<List<MissedTradeRecord>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new List<MissedTradeRecord>();
        }
        public List<MissedTradeRecord> Load()
        {
            if (!File.Exists(_filePath))
                return new();

            var json = File.ReadAllText(_filePath);

            try
            {

                return JsonSerializer.Deserialize<List<MissedTradeRecord>>(json, JsonOptions)
                       ?? new();
            }
            catch
            {
                return new(); // если формат временно испорчен
            }
        }
    }
}
