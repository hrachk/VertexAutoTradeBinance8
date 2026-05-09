using System.Text.Json;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services;

public class EngineStateService
{
    private readonly ILogger<EngineStateService> _logger;
    public string FilePath { get; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public EngineStateService(
        ILogger<EngineStateService> logger,
        IConfiguration config)
    {
        _logger = logger;
        FilePath = config["Paths:EngineState"]
                   ?? throw new Exception("EngineState path not found in config");
    }

    public EngineStateModel? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;

            using var fs = new FileStream(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

            using var sr = new StreamReader(fs);
            var json = sr.ReadToEnd();

            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JsonSerializer.Deserialize<EngineStateModel>(json, JsonOptions);
        }
        catch (IOException)
        {
            // engine пишет файл
            return null;
        }
        catch (JsonException)
        {
            // JSON half-written
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EngineStateService.Load() ERROR");
            return null;
        }
    }
}
