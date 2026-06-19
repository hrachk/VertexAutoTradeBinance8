using System.Text.Json;

namespace VertexAutoTradeBinance8.Strategy
{
    /// <summary>
    /// The Engine process is a plain Generic Host (no Kestrel/HTTP server),
    /// so the Web app can't call it directly via REST the way it would a
    /// normal API. Every existing cross-process control in this codebase
    /// (TradePermission, klines_bootstrap.json, missed_trades.json) uses a
    /// shared JSON file under SharedData:Root as the handoff mechanism, so
    /// this follows the same pattern for the strategy mode toggle:
    ///
    ///   Web "Strategy Mode" switch  -->  writes strategy_mode.json
    ///   This watcher (polls every 3s)  -->  reads it, updates StrategyModeState
    ///   StrategyRouter  -->  reads StrategyModeState.Current on every signal
    ///
    /// File content: {"mode":"Auto"} | {"mode":"TrendOnly"} | {"mode":"MeanReversionOnly"}
    /// Missing file or unreadable content is treated as "no change" — the
    /// last known StrategyModeState value (or the appsettings.json startup
    /// default) simply stays in effect, never crashes the engine.
    /// </summary>
    public sealed class StrategyModeFileWatcher : BackgroundService
    {
        private readonly ILogger<StrategyModeFileWatcher> _logger;
        private readonly StrategyModeState _modeState;
        private readonly string _filePath;

        private DateTime _lastWriteUtc = DateTime.MinValue;

        public StrategyModeFileWatcher(
            ILogger<StrategyModeFileWatcher> logger,
            StrategyModeState modeState,
            IConfiguration cfg)
        {
            _logger = logger;
            _modeState = modeState;

            var root = cfg["SharedData:Root"];
            _filePath = !string.IsNullOrWhiteSpace(root)
                ? Path.Combine(root, "strategy_mode.json")
                : Path.Combine(AppContext.BaseDirectory, "strategy_mode.json");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[STRATEGY-MODE-WATCHER] Watching {path}", _filePath);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    TryReadAndApply();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[STRATEGY-MODE-WATCHER] poll failed, keeping last known mode");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                }
                catch (OperationCanceledException) { }
            }
        }

        private void TryReadAndApply()
        {
            if (!File.Exists(_filePath)) return;

            var writeUtc = File.GetLastWriteTimeUtc(_filePath);
            if (writeUtc == _lastWriteUtc) return; // unchanged since last poll

            string json;
            using (var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
                json = sr.ReadToEnd();

            if (string.IsNullOrWhiteSpace(json)) return;

            var doc = JsonSerializer.Deserialize<StrategyModeFile>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (doc == null || string.IsNullOrWhiteSpace(doc.Mode)) return;

            if (!Enum.TryParse<StrategyMode>(doc.Mode, ignoreCase: true, out var parsed))
            {
                _logger.LogWarning(
                    "[STRATEGY-MODE-WATCHER] Unrecognized mode value '{mode}' in {path} — ignoring",
                    doc.Mode, _filePath);
                _lastWriteUtc = writeUtc; // don't keep re-parsing the same bad value every 3s
                return;
            }

            _lastWriteUtc = writeUtc;
            _modeState.Set(parsed);
        }

        private sealed class StrategyModeFile
        {
            public string Mode { get; set; } = "";
        }
    }
}
