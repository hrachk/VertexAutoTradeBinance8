using System.Text.Json;
using VertexAutoTradeBinance8.Configuration;
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

        // Config if set, otherwise shared C:\VertexShared\engine_state.json
        FilePath = config["EngineState:Path"]
                   ?? SharedDataPaths.EngineStateJson;

        _logger.LogInformation("[ENGINE-WEB] Using path → {Path}", FilePath);
    }

    public EngineStateModel? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;

            var json = File.ReadAllText(FilePath);

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
