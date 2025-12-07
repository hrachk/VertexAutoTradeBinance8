using System.Text.Json;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8.Web.Services
{
    public class AiLearningFileService
    {
        private readonly string FilePath;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public AiLearningFileService(IWebHostEnvironment env)
        {
            // путь к файлу ai-models/ai_learning.json
          //  FilePath =  @"C:\Users\karap\source\repos\VertexAutoTradeBinance8\VertexAutoTradeBinance AI v5.0 Full Autonomous Mode\bin\Debug\net8.0\ai-models\ai_learning.json";
           FilePath = @"F:\VERTEX TRADING SYSTEM\TradingAI\VertexAutoTradeBinance AI v5.0 Full Autonomous Mode\bin\Debug\net8.0\ai-models\ai_learning.json";
        }

        public AiLearningSnapshot? LoadSnapshot()
        {
            if (!File.Exists(FilePath))
                return null;

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AiLearningSnapshot>(json, JsonOptions);
        }
    }
}
