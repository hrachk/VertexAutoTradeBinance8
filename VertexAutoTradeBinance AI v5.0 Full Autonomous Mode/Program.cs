using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Strategy;

namespace VertexAutoTradeBinance8;

public class Program
{
    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        using IHost host = Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
            })
            .ConfigureServices((ctx, services) =>
            {
                services.Configure<Configuration.BinanceOptions>(ctx.Configuration.GetSection("Binance"));
                services.Configure<Configuration.TradingOptions>(ctx.Configuration.GetSection("Trading"));

                services.AddSingleton<BinanceClientFactory>();
                services.AddSingleton<MarketDataService>();
                services.AddSingleton<RiskManager>();
                services.AddSingleton<OrderExecutor>();

                services.AddSingleton<AiCorrelationService>();
                services.AddSingleton<AiMarketRegimeService>();
                services.AddSingleton<AiPatternEngineService>();
                services.AddSingleton<AdaptiveStrategyService>();
                services.AddSingleton<AiSelfLearningService>();
                services.AddSingleton<AiModelSnapshotService>();
                services.AddSingleton<TradeResultMonitorService>();
                services.AddSingleton<CheckAfterFillService>();
                services.AddSingleton<TradeSignalMemoryService>();
                services.AddSingleton<OrderTracerService>();
                services.AddSingleton<RecoverLostOrdersService>();
                services.AddSingleton<TradeSignalMemoryService>();
                services.AddSingleton<ManualPositionHandler>();
                services.AddSingleton<AiLeverageService>();

                services.AddSingleton<AiLiquidityClusterService>();
                services.AddSingleton<StrategyEngine>();
                services.AddSingleton<SymbolInfoService>();
                services.AddSingleton<LiquidityGuardService>();
                services.AddSingleton<PositionGuardService>();
                services.AddSingleton<PositionProtectorService>();
                services.AddSingleton<PnLMonitorService>();
                services.AddSingleton<OrderCleanerService>();
                services.AddSingleton<PositionSupervisorService>();
                services.AddSingleton<SmartRegimeService>();
               // services.AddSingleton<RealContextSupervisor>();

              

                services.AddHostedService<TradingWorker>();
                services.AddSingleton<PredictiveEngineV4ConfirmationService>();
                services.AddSingleton<AiStopLossOptimizer>();
                services.AddSingleton<AiRiskScalerV2>(); // опционально, можно создавать вручную

            })
            .Build();

        await host.RunAsync();
    }
}