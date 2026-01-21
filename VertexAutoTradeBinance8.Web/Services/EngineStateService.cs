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

      //  FilePath = config["EngineState:Path"]
                //   ?? throw new Exception("EngineState path not found in config");
        FilePath = Path.Combine(AppContext.BaseDirectory, "engine_state.json");
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
