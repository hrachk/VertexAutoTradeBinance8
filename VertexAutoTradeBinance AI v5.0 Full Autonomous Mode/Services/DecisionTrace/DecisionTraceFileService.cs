using System.Text.Json;

namespace VertexAutoTradeBinance8.Services.DecisionTrace
{
    public sealed class DecisionTraceFileService : IDecisionTraceService
    {
        private readonly object _lock = new();
        private readonly ILogger<DecisionTraceFileService> _logger;
        private readonly string _traceDir;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public DecisionTraceFileService(
            ILogger<DecisionTraceFileService> logger,
            IConfiguration cfg)
        {
            _logger = logger;

            // Use SharedData:Root like every other cross-process file in
            // this project (klines_bootstrap.json, missed_trades.json,
            // appsettings.runtime.json, etc) — AppContext.BaseDirectory
            // points at THIS process's own bin folder, which is wrong
            // when the Web app (running from a different bin folder)
            // needs to read these same files back.
            var root = cfg["SharedData:Root"];
            _traceDir = !string.IsNullOrWhiteSpace(root)
                ? Path.Combine(root, "ai-models", "decision-trace")
                : Path.Combine(AppContext.BaseDirectory, "ai-models", "decision-trace");

            try
            {
                Directory.CreateDirectory(_traceDir);
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
                    _traceDir,
                    $"decision_trace_{DateTime.UtcNow:yyyyMMdd}.jsonl");

                var json = JsonSerializer.Serialize(snapshot, JsonOptions);

                lock (_lock)
                {
                    Directory.CreateDirectory(_traceDir);

                    using var fs = new FileStream(
                        file,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read);

                    using var sw = new StreamWriter(fs)
                    {
                        AutoFlush = true
                    };

                    sw.WriteLine(json);
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
