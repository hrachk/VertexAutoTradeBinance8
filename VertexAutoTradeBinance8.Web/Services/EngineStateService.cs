using System.Text.Json;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services;

public class EngineStateService
{
    private readonly ILogger<EngineStateService> _logger;
    public string FilePath { get; }

    public EngineStateService(
        ILogger<EngineStateService> logger,
        IConfiguration config)
    {
        _logger = logger;

        var cfgPath = config["EngineState:Path"];
        FilePath = string.IsNullOrWhiteSpace(cfgPath)
            ? VertexAutoTradeBinance8.Configuration.VertexPaths.EngineState
            : cfgPath;
    }

    public EngineStateModel? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;

            var json = SafeFileRead.ReadAllTextShared(FilePath);
            if (json == null) return null;

            var state = JsonSerializer.Deserialize<EngineStateModel>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return state;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EngineStateService.Load() ERROR");
            return null;
        }
    }
}
