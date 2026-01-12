using System.Text.Json;
using VertexAutoTradeBinance8.Web.Models.Config;

namespace VertexAutoTradeBinance8.Web.Services.Runtime;

public sealed class EngineRuntimeSettings
{
    private readonly object _gate = new();
    public EngineConfigRoot Current { get; private set; } = new();

    public event Action? Changed;

    public void Set(EngineConfigRoot cfg)
    {
        lock (_gate)
        {
            Current = cfg;
        }
        Changed?.Invoke();
    }

    public HedgeKillConfig HedgeKill => Current.HedgeKill;
}
