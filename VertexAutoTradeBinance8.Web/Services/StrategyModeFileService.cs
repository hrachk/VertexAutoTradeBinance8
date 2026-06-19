using System.Text.Json;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// Writes strategy_mode.json to SharedData:Root — the file the Engine's
/// StrategyModeFileWatcher polls every few seconds to live-update its
/// StrategyModeState. The Engine has no HTTP server to call directly, so
/// this file is the control channel, matching every other cross-process
/// handoff already used in this codebase.
///
/// Write is atomic (write to a .tmp file, then File.Move) so the Engine
/// never reads a half-written file mid-write.
/// </summary>
public sealed class StrategyModeFileService
{
    private readonly string _filePath;
    private readonly ILogger<StrategyModeFileService> _logger;

    public StrategyModeFileService(IConfiguration cfg, ILogger<StrategyModeFileService> logger)
    {
        _logger = logger;
        var root = cfg["SharedData:Root"];
        _filePath = !string.IsNullOrWhiteSpace(root)
            ? Path.Combine(root, "strategy_mode.json")
            : Path.Combine(AppContext.BaseDirectory, "strategy_mode.json");
    }

    public async Task<string> ReadCurrentModeAsync()
    {
        try
        {
            if (!File.Exists(_filePath)) return "Auto";
            using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var json = await sr.ReadToEndAsync();
            var doc = JsonSerializer.Deserialize<StrategyModeFileDto>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return string.IsNullOrWhiteSpace(doc?.Mode) ? "Auto" : doc!.Mode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[StrategyModeFileService] read failed, defaulting to Auto");
            return "Auto";
        }
    }

    public async Task SetModeAsync(string mode)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmpPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(new StrategyModeFileDto { Mode = mode });

        await File.WriteAllTextAsync(tmpPath, json);
        File.Move(tmpPath, _filePath, overwrite: true);

        _logger.LogInformation("[StrategyModeFileService] mode set to {mode}", mode);
    }

    private sealed class StrategyModeFileDto
    {
        public string Mode { get; set; } = "Auto";
    }
}
