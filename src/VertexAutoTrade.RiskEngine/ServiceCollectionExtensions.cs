using Microsoft.Extensions.DependencyInjection;
using VertexAutoTrade.RiskEngine.Abstractions;
using VertexAutoTrade.RiskEngine.Guards;
using VertexAutoTrade.RiskEngine.Options;
using VertexAutoTrade.RiskEngine.Sizing;

namespace VertexAutoTrade.RiskEngine;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVertexRiskEngine(this IServiceCollection services)
    {
        services.AddOptions<HardRiskOptions>();
        services.AddOptions<AntiTiltOptions>();
        services.AddSingleton<IHardRiskGuard, HardRiskGuard>();
        services.AddSingleton<IAntiTiltCircuitBreaker, AntiTiltCircuitBreaker>();
        services.AddSingleton<IPositionSizer>(sp =>
        {
            var opt = sp.GetService<Microsoft.Extensions.Options.IOptions<HardRiskOptions>>();
            return new PositionSizer(opt?.Value.HardCapRiskUsd ?? 0m);
        });
        services.AddSingleton<IEntryRiskGate, EntryRiskGate>();
        return services;
    }
}
