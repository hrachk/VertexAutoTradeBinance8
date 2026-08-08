using System.Text.Json;
using VertexAutoTradeBinance8.Services.Storage;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services;

public class EngineStateService
{
    private readonly ILogger<EngineStateService> _logger;
    public string FilePath { get; }

    public EngineStateService(
        ILogger<EngineStateService> logger,
        VertexPaths paths)
    {
        _logger = logger;

        // Ключ EngineState:Path из конфига был мёртвым: сервис его не читал,
        // а путь считал от собственного BaseDirectory — то есть от папки Web,
        // куда движок никогда ничего не писал.
        FilePath = paths.EngineState;
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
