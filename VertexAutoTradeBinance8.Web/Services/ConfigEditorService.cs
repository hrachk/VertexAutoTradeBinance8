using System.Text.Json;
using VertexAutoTradeBinance8.Web.Services.Runtime;

public class ConfigEditorService
{
    private readonly string _runtimePath;
    private readonly EngineRuntimeSettings _runtime;

    public ConfigEditorService(IConfiguration cfg, EngineRuntimeSettings runtime)
    {
        _runtime = runtime;
        var root = cfg["SharedData:Root"]
            ?? throw new InvalidOperationException("SharedData:Root missing");

        _runtimePath = Path.Combine(root, "appsettings.runtime.json");

        // bootstrap
        var boot = Load();
        _runtime.Set(boot);
    }

    public EngineConfigRoot Load()
    {
        if (!File.Exists(_runtimePath))
            return new EngineConfigRoot();

        var json = File.ReadAllText(_runtimePath);
        if (string.IsNullOrWhiteSpace(json))
            return new EngineConfigRoot();

        return JsonSerializer.Deserialize<EngineConfigRoot>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new EngineConfigRoot();
    }

    public void SaveAndApply(EngineConfigRoot cfg)
    {
        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_runtimePath, json);

        _runtime.Set(cfg);
    }

    public void Save(EngineConfigRoot cfg)
    {
        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(_runtimePath, json);
    }
}
