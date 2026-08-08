using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.Levels;
using VertexAutoTradeBinance8.Services.Storage;
using VertexAutoTradeBinance8.Strategy;

namespace VertexAutoTradeBinance8;

public class Program
{
    public static async Task Main(string[] args)
    {
        Environment.SetEnvironmentVariable(
        "ASPNETCORE_ENVIRONMENT",
        "Development"
    );

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;
       
   

        using IHost host = Host.CreateDefaultBuilder(args)
             .ConfigureAppConfiguration((ctx, config) =>
             {
                 config.AddUserSecrets<Program>(optional: true);
             })

            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
            })
            .ConfigureServices((ctx, services) =>
            {
                services.Configure<Configuration.BinanceOptions>(ctx.Configuration.GetSection("Binance"));
                services.Configure<Configuration.TradingOptions>(ctx.Configuration.GetSection("Trading"));
                services.Configure<Configuration.TradingOptions>(ctx.Configuration.GetSection("TestMode"));


                // 🔥 ОБЯЗАТЕЛЬНО
                services.AddHttpClient();

                services.AddSingleton<BinanceClientFactory>();
                services.AddSingleton<MarketDataService>();
                services.AddSingleton<RiskManager>();
                services.AddSingleton<OrderExecutor>();

                services.AddSingleton<AiCorrelationService>();
                services.AddSingleton<AiMarketRegimeService>();
                services.AddSingleton<AiPatternEngineService>();
                services.AddSingleton<AdaptiveStrategyService>();
 
                services.AddSingleton<AiSelfLearningService>();  // Ensure that all dependencies are resolved correctly


                services.AddSingleton<SimulatedTradeService>();


                services.AddSingleton<AiModelSnapshotService>();
                services.AddSingleton<TradeResultMonitorService>();
                services.AddSingleton<TradeSignalMemoryService>();
                services.AddSingleton<OrderTracerService>();
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
                 services.AddSingleton<TradeStateManager>();

                services.AddSingleton<EngineStateBuilder>();
               

                services.Configure<EngineStateSettings>(ctx.Configuration.GetSection("EngineState"));
                services.AddSingleton<EngineStateSnapshotService>();

                services.AddSingleton(sp => sp.GetRequiredService<IOptions<TradingOptions>>().Value);

               
                services.AddSingleton<PredictiveEngineV4ConfirmationService>();
                services.AddSingleton<AiStopLossOptimizer>();
                services.AddSingleton<AiRiskScalerV2>(); // опционально, можно создавать вручную

                services.AddHostedService<BackgroundMarketScannerService>();
                services.AddSingleton<SymbolLiquidityScanner>();
                services.AddHostedService<TradingWorker>();
                services.AddSingleton<SymbolRegistryService>();
                services.AddSingleton<AiTimeframeSelectorService>();
                services.AddSingleton<ReverseProbeEngine>();

                services.AddSingleton<ExecutedSignalService>();

                // =========================================================
                // FIX: OrderDispatcher — BackgroundService, но раньше был
                // зарегистрирован ТОЛЬКО как AddSingleton. ExecuteAsync не
                // вызывался, канал не читался → ВСЕ SL/TP/BE/трейлинг,
                // поставленные через Enqueue, никогда не уходили на биржу.
                // Регистрируем один инстанс и как интерфейс, и как hosted.
                // =========================================================
                services.AddSingleton<OrderDispatcher>();
                services.AddSingleton<IOrderDispatcher>(sp => sp.GetRequiredService<OrderDispatcher>());
                services.AddHostedService(sp => sp.GetRequiredService<OrderDispatcher>());

                // =========================================================
                // Защита позиций: единая точка постановки стопа
                // =========================================================
                services.AddSingleton<IAlgoOrderRawClient, AlgoOrderRawClient>();
                services.AddSingleton<ProtectionOrderService>();

                // =========================================================
                // ХРАНИЛИЩЕ И ЖУРНАЛ РЕШЕНИЙ
                // VertexPaths даёт движку и Web один корень данных: раньше
                // каждый писал и читал в свой bin\Debug и они не встречались.
                // =========================================================
                services.AddSingleton<VertexPaths>();
                services.AddSingleton<TradeDecisionJournal>();

                // =========================================================
                // МЕТОДОЛОГИЯ УРОВНЕЙ SL / TP
                // =========================================================
                var levelOptions = ctx.Configuration.GetSection("Levels").Get<LevelOptions>() ?? new LevelOptions();
                services.AddSingleton(levelOptions);
                services.AddSingleton<TradeLevelEngine>();

                // =========================================================
                // Страховочные фоновые сервисы (тоже были не hosted)
                // =========================================================
                services.AddSingleton<RecoverLostOrdersService>();
                services.AddHostedService(sp => sp.GetRequiredService<RecoverLostOrdersService>());

                services.AddSingleton<CheckAfterFillService>();
                services.AddHostedService(sp => sp.GetRequiredService<CheckAfterFillService>());


            })
            .Build();

        await host.RunAsync();
    }
}