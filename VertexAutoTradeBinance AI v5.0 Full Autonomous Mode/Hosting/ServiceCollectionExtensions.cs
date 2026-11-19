using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Strategy;

namespace VertexAutoTradeBinance8.Hosting;

/// <summary>
/// Единая регистрация торгового ядра. Раньше Worker и API регистрировали сервисы
/// вручную и по-разному (в API не хватало зависимостей → падение DI при старте).
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVertexCore(this IServiceCollection services, IConfiguration cfg)
    {
        services.Configure<BinanceOptions>(cfg.GetSection("Binance"));
        services.Configure<TradingOptions>(cfg.GetSection("Trading"));
        services.Configure<TradingOptions>(cfg.GetSection("TestMode")); // test-mode флаги в тот же объект (legacy)
        services.Configure<EngineStateSettings>(cfg.GetSection("EngineState"));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<TradingOptions>>().Value);

        // infrastructure
        services.AddSingleton<BinanceClientFactory>();
        services.AddSingleton<AccountStateCache>();
        services.AddSingleton<SymbolInfoService>();
        services.AddSingleton<MarketDataService>();

        // AI / analytics
        services.AddSingleton<AiCorrelationService>();
        services.AddSingleton<AiMarketRegimeService>();
        services.AddSingleton<SmartRegimeService>();
        services.AddSingleton<AiPatternEngineService>();
        services.AddSingleton<AdaptiveStrategyService>();
        services.AddSingleton<AiSelfLearningService>();
        services.AddSingleton<AiModelSnapshotService>();
        services.AddSingleton<AiLeverageService>();
        services.AddSingleton<AiLiquidityClusterService>();
        services.AddSingleton<AiTimeframeSelectorService>();
        services.AddSingleton<PredictiveEngineV4ConfirmationService>();
        services.AddSingleton<AiStopLossOptimizer>();
        services.AddSingleton<AiRiskScalerV2>();
        services.AddSingleton<SimulatedTradeService>();

        // trading
        services.AddSingleton<StrategyEngine>();
        services.AddSingleton<RiskManager>();
        services.AddSingleton<OrderExecutor>();
        services.AddSingleton<LiquidityGuardService>();
        services.AddSingleton<PositionGuardService>();
        services.AddSingleton<PositionProtectorService>();
        services.AddSingleton<PnLMonitorService>();
        services.AddSingleton<OrderCleanerService>();
        services.AddSingleton<PositionSupervisorService>();
        services.AddSingleton<TradeResultMonitorService>();
        services.AddSingleton<CheckAfterFillService>();
        services.AddSingleton<OrderTracerService>();
        services.AddSingleton<RecoverLostOrdersService>();

        // state / journal
        services.AddSingleton<TradeSignalMemoryService>();
        services.AddSingleton<ManualPositionHandler>();
        services.AddSingleton<ManagedPositionRegistry>();
        services.AddSingleton<ExecutedSignalService>();
        services.AddSingleton<TradeStateManager>();
        services.AddSingleton<EngineStateBuilder>();
        services.AddSingleton<EngineStateSnapshotService>();

        // control
        services.AddSingleton<TradingSessionGate>();
        services.AddSingleton<TradingControlService>();
        services.AddSingleton<SymbolRegistryService>();
        services.AddSingleton<SymbolLiquidityScanner>();

        return services;
    }

    /// <summary>Источники конфигурации для production: appsettings.Local.json (секреты, в .gitignore) + env.</summary>
    public static IConfigurationBuilder AddVertexConfiguration(this IConfigurationBuilder b, string contentRoot)
    {
        b.AddJsonFile(Path.Combine(contentRoot, "appsettings.Local.json"), optional: true, reloadOnChange: true);
        b.AddJsonFile(Path.Combine(VertexPaths.DataDir, "appsettings.Local.json"), optional: true, reloadOnChange: true);
        b.AddEnvironmentVariables();
        return b;
    }
}
