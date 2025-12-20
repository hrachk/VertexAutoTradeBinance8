using System.Text.Json;

namespace VertexAutoTradeBinance8.Services.DecisionTrace
{
    public sealed class DecisionTraceFileService : IDecisionTraceService
    {
        private readonly object _lock = new();
        private readonly ILogger<DecisionTraceFileService> _logger;

        private static readonly string TraceDir =
            Path.Combine(AppContext.BaseDirectory, "ai-models", "decision-trace");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public DecisionTraceFileService(
            ILogger<DecisionTraceFileService> logger)
        {
            _logger = logger;

            try
            {
                Directory.CreateDirectory(TraceDir);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TRACE] Failed to create trace directory");
            }
        }

        public void Record(DecisionTraceSnapshot snapshot)
        {
            try
            {
                var file = Path.Combine(
                    TraceDir,
                    $"decision_trace_{DateTime.UtcNow:yyyyMMdd}.jsonl");

                var json = JsonSerializer.Serialize(snapshot, JsonOptions);

                lock (_lock)
                {
                    File.AppendAllText(file, json + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[TRACE] Failed to record decision trace for {Symbol}",
                    snapshot.Symbol);
            }
        }
    }
}
