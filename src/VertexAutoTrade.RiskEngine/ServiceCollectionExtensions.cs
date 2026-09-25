using Microsoft.Extensions.DependencyInjection;
using VertexAutoTrade.Execution;
using VertexAutoTrade.MLEngine;
using VertexAutoTrade.NewsMacro;
using VertexAutoTrade.Regime;
using VertexAutoTrade.RiskEngine.Abstractions;
using VertexAutoTrade.RiskEngine.Guards;
using VertexAutoTrade.RiskEngine.Options;
using VertexAutoTrade.RiskEngine.Pipeline;
using VertexAutoTrade.RiskEngine.Sizing;

namespace VertexAutoTrade.RiskEngine;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVertexRiskEngine(this IServiceCollection services)
    {
        services.AddOptions<HardRiskOptions>();
        services.AddOptions<AntiTiltOptions>();
        services.AddOptions<RegimeOptions>();
        services.AddOptions<InstitutionalGateOptions>();

        services.AddSingleton<IHardRiskGuard, HardRiskGuard>();
        services.AddSingleton<IAntiTiltCircuitBreaker, AntiTiltCircuitBreaker>();
        services.AddSingleton<IPositionSizer>(sp =>
        {
            var opt = sp.GetService<Microsoft.Extensions.Options.IOptions<HardRiskOptions>>();
            return new PositionSizer(opt?.Value.HardCapRiskUsd ?? 0m);
        });
        services.AddSingleton<IEntryRiskGate, EntryRiskGate>();
        services.AddSingleton<MarketRegimeAnalyzer>(_ => new MarketRegimeAnalyzer());
        services.AddSingleton<BtcCorrelationGuard>(_ => new BtcCorrelationGuard());
        services.AddSingleton<CalendarMacroGuard>(_ => new CalendarMacroGuard());
        services.AddSingleton<NewsSentimentGate>();
        services.AddSingleton<OiFundingTracker>();
        services.AddSingleton<MlSetupClassifier>(_ => new MlSetupClassifier(hardReject: false));
        services.AddSingleton<InstitutionalEntryPipeline>(sp =>
        {
            return new InstitutionalEntryPipeline(
                sp.GetRequiredService<IHardRiskGuard>(),
                sp.GetRequiredService<IAntiTiltCircuitBreaker>(),
                sp.GetRequiredService<IPositionSizer>(),
                sp.GetService<MarketRegimeAnalyzer>(),
                sp.GetService<BtcCorrelationGuard>(),
                sp.GetService<CalendarMacroGuard>(),
                sp.GetService<NewsSentimentGate>(),
                sp.GetService<OiFundingTracker>(),
                sp.GetService<MlSetupClassifier>(),
                sp.GetService<Microsoft.Extensions.Options.IOptionsMonitor<InstitutionalGateOptions>>());
        });
        return services;
    }
}
