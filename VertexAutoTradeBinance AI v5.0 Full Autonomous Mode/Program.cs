using Binance.Net.Clients;
using CryptoExchange.Net.Authentication;
using Microsoft.Extensions.Options;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.MarketData;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.Interface;
using VertexAutoTradeBinance8.Services.MarketData;
using VertexAutoTradeBinance8.Services.State;
using VertexAutoTradeBinance8.Services.Ws;
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
                services.AddSingleton<MarketDataKlineBuffer>();

                services.AddSingleton<BinanceSocketClient>(sp =>
                {
                    var cfg = sp.GetRequiredService<IOptions<BinanceOptions>>().Value;

                    return new BinanceSocketClient(options =>
                    {
                        options.ApiCredentials = new ApiCredentials(
                            cfg.ApiKey,
                            cfg.SecretKey
                        );
                        options.ReconnectPolicy = CryptoExchange.Net.Objects.ReconnectPolicy.ExponentialBackoff;

                    });
                });

                services.AddSingleton<WsKlineSubscriber>();
                services.AddSingleton<MarketDataFacade>();
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
                services.AddSingleton<IOrderDispatcher, OrderDispatcher>();

                services.AddSingleton<BinanceUserDataSubscriber>();
                services.AddHostedService<BinanceUserDataHostedService>();

                services.AddSingleton<IAccountStateService, AccountStateService>();


            })
            .Build();
 


        await host.RunAsync();
    }
}