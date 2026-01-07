using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.MarketData;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.Bootstrap;
using VertexAutoTradeBinance8.Services.DecisionTrace;
using VertexAutoTradeBinance8.Services.Engine;
using VertexAutoTradeBinance8.Services.Interface;
using VertexAutoTradeBinance8.Services.MarketState;
using VertexAutoTradeBinance8.Services.Recovery;
using VertexAutoTradeBinance8.Services.State;
using VertexAutoTradeBinance8.Services.Ws;
using VertexAutoTradeBinance8.Strategy;
// остальные using как у тебя

namespace VertexAutoTradeBinance8;

public class Program
{
    public static async Task Main(string[] args)
    {
        var baseDir = AppContext.BaseDirectory;
        var logDir = Path.Combine(baseDir, "logs");
        Directory.CreateDirectory(logDir);

        // =====================================================
        // 1️⃣ Serilog — PRO CONFIG (INFO + ERROR split)
        // =====================================================
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()

            // 🟢 INFO / DEBUG / WS / FLOW
            .WriteTo.File(
                path: Path.Combine(logDir, "engine-info-.log"),
                restrictedToMinimumLevel: LogEventLevel.Information,
                rollingInterval: RollingInterval.Hour,     // каждый час
                fileSizeLimitBytes: 10 * 1024 * 1024,      // 10 MB
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 48,                // 2 дня
                shared: false)

            // 🔴 ERROR / FATAL
            .WriteTo.File(
                path: Path.Combine(logDir, "engine-error-.log"),
                restrictedToMinimumLevel: LogEventLevel.Error,
                rollingInterval: RollingInterval.Day,      // по дням
                retainedFileCountLimit: 14,                // 14 дней
                shared: false)

            .CreateLogger();


        try
        {
            Log.Information("Engine starting");

            //Environment.SetEnvironmentVariable(
            //    "ASPNETCORE_ENVIRONMENT",
            //    "Development"
            //);

            //  Console.OutputEncoding = System.Text.Encoding.UTF8;
            //   Console.InputEncoding = System.Text.Encoding.UTF8;
            var isService = !Environment.UserInteractive;
            Log.Information("Startup mode: {Mode}", isService ? "SERVICE" : "INTERACTIVE");


            // =====================================================
            // 2️⃣ ОДИН Host
            // =====================================================
            using IHost host = Host.CreateDefaultBuilder(args)
                 .UseWindowsService() // 👈 ВОТ ЭТО КЛЮЧ
                .UseSerilog() // 👈 ПОДКЛЮЧАЕМ Serilog
                .ConfigureAppConfiguration((ctx, config) =>
                {
                    config.AddUserSecrets<Program>(optional: true);
                })
                 .ConfigureLogging(logging =>
                 {
                     logging.ClearProviders();
                   //  logging.AddConsole();
                 })
                .ConfigureServices((ctx, services) =>
                {
                    // ===== ТВОЙ СУЩЕСТВУЮЩИЙ DI — БЕЗ ИЗМЕНЕНИЙ =====
                    services.Configure<BinanceOptions>(ctx.Configuration.GetSection("Binance"));
                    services.Configure<TradingOptions>(
                         ctx.Configuration.GetSection("Trading"));

                    services.Configure<TestModeOptions>(
                        ctx.Configuration.GetSection("TestMode"));

                    services.AddSingleton(sp =>
                        sp.GetRequiredService<IOptions<TradingOptions>>().Value);

                    services.AddSingleton(sp =>
                        sp.GetRequiredService<IOptions<TestModeOptions>>().Value);

                    services.AddHttpClient();




                    // ===== BASE / MARKET =====
                    services.AddSingleton<BinanceClientFactory>();
                    services.AddSingleton<MarketDataKlineBuffer>();
                    services.AddSingleton<KlineBufferPersistence>();

                    services.AddSingleton<MarketContextService>();
                    services.AddSingleton<WsKlineSubscriber>();
                    services.AddSingleton<MarketDataFacade>();
                    services.AddSingleton<MarketStateService>();
                    services.AddSingleton<MarketDataService>();

                    // ⚠️ РЕГИСТРИРУЕМ ОДИН РАЗ
                  
                    services.AddSingleton<SymbolLiquidityScanner>();
                    services.AddSingleton<SymbolUniverseBuilder>();
                    services.AddSingleton<SymbolInfoService>(); // ← 🔴 КРИТИЧНО: ДО RiskManager

                    // ===== SYMBOL REGISTRY =====
                    services.AddSingleton<SymbolRegistryService>();
                    services.AddSingleton<UniverseDryRunFileLogger>();

                    // ===== AI / CORE =====
                    services.AddSingleton<AiSelfLearningService>();
                    services.AddSingleton<AiMarketRegimeService>();
                    services.AddSingleton<AiPatternEngineService>();
                    services.AddSingleton<AiCorrelationService>();
                    services.AddSingleton<AiLiquidityClusterService>();
                    services.AddSingleton<AiLeverageService>();
                    services.AddSingleton<AiStopLossOptimizer>();
                    services.AddSingleton<AiRiskScalerV2>();


                    // ===== RISK / EXECUTION =====
                    services.AddSingleton<RiskManager>();          // ← теперь SymbolInfoService уже есть
                    services.AddSingleton<OrderExecutor>();
                    services.AddSingleton<OrderCleanerService>();
                    services.AddSingleton<PnLMonitorService>();

                    // ===== STRATEGY =====
                    services.AddSingleton<AdaptiveStrategyService>();
                    services.AddSingleton<StrategyEngine>();
                    services.AddSingleton<SmartRegimeService>();
                    services.AddSingleton<ReverseProbeEngine>();


                    // ===== SUPERVISOR / STATE =====
                    services.AddSingleton<PositionSupervisorService>();
                    services.AddSingleton<PositionGuardService>();
                    services.AddSingleton<PositionProtectorService>();

                    services.AddSingleton<IAccountStateService, AccountStateService>();
                    services.AddSingleton<TradeStateManager>();
                    services.AddSingleton<EngineStateBuilder>();
                    services.AddSingleton<EngineStateSnapshotService>();


                    // ===== HOSTED =====
                    services.AddHostedService<SupervisorBootstrapHostedService>();
                    services.AddHostedService<BackgroundMarketScannerService>();
                    services.AddHostedService<TradingWorker>();
                    services.AddHostedService<BinanceUserDataHostedService>();



                    // ===== MISC =====
                    services.AddSingleton<ExecutedSignalService>();
                    services.AddSingleton<IDecisionTraceService, DecisionTraceFileService>();
                    services.AddSingleton<IOpenPositionProvider, EngineStateOpenPositionProvider>();


                    services.AddSingleton<OpenPositionSymbolTracker>();
                    services.AddSingleton<IOpenPositionSymbolSource>(
                        sp => sp.GetRequiredService<OpenPositionSymbolTracker>());


                    services.AddSingleton<IBootGate, BootGate>();

                    services.AddSingleton<IPositionRecoveryService, PositionRecoveryService>();
                    services.AddSingleton<IStrategyPreFilter, StrategyPreFilterService>();

                   

                    
                    
                    services.AddSingleton<SimulatedTradeService>();
                    services.AddSingleton<AiModelSnapshotService>();
                    services.AddSingleton<TradeResultMonitorService>();
                    services.AddSingleton<CheckAfterFillService>();
                    services.AddSingleton<TradeSignalMemoryService>();
                    services.AddSingleton<OrderTracerService>();
                    services.AddSingleton<RecoverLostOrdersService>();
                    services.AddSingleton<ManualPositionHandler>();
                    
                    
                    services.AddSingleton<LiquidityGuardService>(); 
                 
                
                    services.Configure<EngineStateSettings>(ctx.Configuration.GetSection("EngineState"));
                  
                    services.AddSingleton<PredictiveEngineV4ConfirmationService>();                  
                                         

                    services.AddSingleton<SymbolRegistryService>();
                    services.AddSingleton<AiTimeframeSelectorService>();                 
                  
                    services.AddSingleton<IOrderDispatcher, OrderDispatcher>();
                    services.AddSingleton<BinanceUserDataSubscriber>();
 

                })
                .Build();

            // =====================================================
            // 3️⃣ RUN
            // =====================================================
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Engine crashed");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
