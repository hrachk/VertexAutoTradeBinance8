using Binance.Net;
using Binance.Net.Clients;
using CryptoExchange.Net.Authentication;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.MarketData;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Models.DTO;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.Bootstrap;
using VertexAutoTradeBinance8.Services.DecisionTrace;
using VertexAutoTradeBinance8.Services.Engine;
using VertexAutoTradeBinance8.Services.Interface;
using VertexAutoTradeBinance8.Services.MarketData;
using VertexAutoTradeBinance8.Services.MarketState;
using VertexAutoTradeBinance8.Services.Recovery;
using VertexAutoTradeBinance8.Services.State;
using VertexAutoTradeBinance8.Services.Ws;
using VertexAutoTradeBinance8.Strategy;
using VertexAutoTradeBinance8.Strategy.MeanReversion;
using static VertexAutoTradeBinance8.Services.OrderExecutor;
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

            //var builder = Host.CreateDefaultBuilder(args);

            //if (!Environment.UserInteractive)
            //{
            //    builder.UseWindowsService();
            //}


            using IHost host = Host.CreateDefaultBuilder(args)
                 .UseWindowsService() // 👈 ВОТ ЭТО КЛЮЧ
                .UseSerilog() // 👈 ПОДКЛЮЧАЕМ Serilog
                .ConfigureAppConfiguration((ctx, config) =>
                {
                    config.AddUserSecrets<Program>(optional: true);

                    // =====================================================
                    // v9: LIVE SETTINGS OVERLAY
                    // =====================================================
                    // Adds appsettings.runtime.json (under SharedData:Root)
                    // as an EXTRA config source layered on top of the base
                    // appsettings.json. Whatever value exists in this file
                    // overrides the matching key from appsettings.json —
                    // same section/key structure, just a sparse subset of
                    // whatever the Web Settings page has been used to edit.
                    //
                    // reloadOnChange: true is .NET's BUILT-IN file-watcher —
                    // no custom BackgroundService/polling needed for this
                    // particular mechanism. Any IOptionsMonitor<T> consumer
                    // (not IOptions<T>, which snapshots once at startup)
                    // automatically picks up changes within ~1-2 seconds of
                    // the file being saved, no engine restart required.
                    //
                    // We need SharedData:Root from the config to know WHERE
                    // this file lives, but that section is itself defined
                    // in appsettings.json — so we do a small first-pass
                    // build of just the sources added so far to resolve it.
                    var partial = config.Build();
                    var sharedRoot = partial["SharedData:Root"];
                    if (!string.IsNullOrWhiteSpace(sharedRoot))
                    {
                        var runtimePath = Path.Combine(sharedRoot, "appsettings.runtime.json");
                        config.AddJsonFile(runtimePath, optional: true, reloadOnChange: true);
                    }
                })
                 .ConfigureLogging(logging =>
                 {
                     logging.ClearProviders();
                     //  logging.AddConsole();
                 })
                 .ConfigureHostOptions(o =>
                 {
                     o.ShutdownTimeout = TimeSpan.FromSeconds(30);
                 })
                .ConfigureServices((ctx, services) =>
                {
                    // ===== ТВОЙ СУЩЕСТВУЮЩИЙ DI — БЕЗ ИЗМЕНЕНИЙ =====
                    services.Configure<BinanceOptions>(ctx.Configuration.GetSection("Binance"));

                    services.Configure<TradingOptions>(
                      ctx.Configuration.GetSection("Trading")); // TRUE default

                    // NOTE: SignalOnlyMode section previously also bound to TradingOptions
                    // here, silently overriding any Trading-section value of EnableExecution.
                    // Removed: EnableExecution defaults to true in TradingOptions and is
                    // set explicitly in the Trading section if needed — a second silent
                    // binding only made config harder to reason about.

                    services.Configure<TradingOptions>("BTC",
                        ctx.Configuration.GetSection("Trading:BTC"));

                    services.Configure<TradingOptions>("ETH",
                        ctx.Configuration.GetSection("Trading:ETH"));

                    services.AddSingleton<TradingOptionsResolver>();

                    services.Configure<TestModeOptions>(
     ctx.Configuration.GetSection("TestMode"));

                    services.Configure<HedgeKillSettings>(
                        ctx.Configuration.GetSection("HedgeKill"));

                    services.Configure<SignalConfidenceSettings>(
                        ctx.Configuration.GetSection("SignalConfidence"));

                    services.Configure<TradingSettings>(
                        ctx.Configuration.GetSection("TradingSettings"));

                    services.AddSingleton<ConfidenceResolver>();

                    services.AddSingleton(sp =>
                       sp.GetRequiredService<IOptions<TradingOptions>>().Value);

                    services.AddSingleton(sp =>
                        sp.GetRequiredService<IOptions<TestModeOptions>>().Value);

                    services.AddSingleton(sp =>
                        sp.GetRequiredService<IOptions<HedgeKillSettings>>().Value);

                    services.AddSingleton(sp =>
                        sp.GetRequiredService<IOptions<SignalConfidenceSettings>>().Value);

                    services.AddSingleton<TradingCredentialStore>();
                    services.AddSingleton<BinanceRestClient>(sp =>
                    {
                        var cfg = sp.GetRequiredService<IOptions<BinanceOptions>>().Value;
                        var store = sp.GetRequiredService<TradingCredentialStore>();
                        string apiKey = cfg.ApiKey, apiSecret = cfg.SecretKey;
                        if (store.TryGet(out _, out var uk, out var us))
                        {
                            apiKey = uk; apiSecret = us;
                        }

                        return new BinanceRestClient(opt =>
                        {
                            opt.Environment = cfg.UseTestnet
                                ? BinanceEnvironment.Testnet
                                : BinanceEnvironment.Live;

                            opt.ApiCredentials = new BinanceCredentials(apiKey, apiSecret);
                            opt.AutoTimestamp = true;
                            opt.RequestTimeout = TimeSpan.FromSeconds(15);
                        });
                    });


                    services.AddSingleton<BinanceSocketClient>(sp =>
                    {
                        var cfg = sp.GetRequiredService<IOptions<BinanceOptions>>().Value;

                        return new BinanceSocketClient(opt =>
                        {
                            opt.Environment = cfg.UseTestnet
                                ? BinanceEnvironment.Testnet
                                : BinanceEnvironment.Live;

                            // ✅ Binance.Net v12: BinanceCredentials
                            opt.ApiCredentials = new BinanceCredentials(
                                cfg.ApiKey,
                                cfg.SecretKey);
                        });
                    });

                    services.AddHttpClient();






                    // ===== BASE / MARKET =====
                    services.AddSingleton<BinanceClientFactory>();
                    services.AddSingleton<MarketDataKlineBuffer>();
                    services.AddSingleton<KlineBufferPersistence>();
                    services.AddSingleton<RealtimePriceService>();

                    services.AddSingleton<MarketContextService>();
                    services.AddSingleton<FearGreedService>();
                    services.AddHostedService(sp => sp.GetRequiredService<FearGreedService>());
                    services.AddSingleton<WsKlineSubscriber>();
                    services.AddSingleton<MarketDataFacade>();
                    services.AddSingleton<MarketStateService>();
                    services.AddSingleton<MarketDataService>();
                    services.AddSingleton<PositionLifecycleTracker>();

                    services.AddSingleton<BinanceHistoryImporter>();
                    //services.AddSingleton<AtrAdaptiveProfitLockManager>();




                    // ⚠️ РЕГИСТРИРУЕМ ОДИН РАЗ

                    services.AddSingleton<SymbolLiquidityScanner>();
                    services.AddSingleton<SymbolUniverseBuilder>();
                    services.AddSingleton<SymbolInfoService>(); // ← 🔴 КРИТИЧНО: ДО RiskManager

                    // =====================================================
                    // Independent file-based historical data store +
                    // background loader. Deliberately separate from the
                    // trading universe / SymbolRegistryService — its job
                    // is building up a permanent, portable chart-history
                    // archive (datadb/SYMBOL/TF.json) regardless of what
                    // the strategy is currently trading. Disabled by
                    // default (HistoricalData:Enabled=false) — turn on and
                    // configure Symbols/Timeframes in appsettings.json
                    // when ready to start backfilling.
                    // =====================================================
                    services.AddSingleton<VertexAutoTradeBinance8.Services.HistoricalData.HistoricalDataStore>();
                    services.AddHostedService<HistoricalDataLoaderService>();
                    // On-demand kline fetcher for Web UI symbol requests
                    services.AddSingleton<KlineOnDemandService>();
                    services.AddHostedService(sp => sp.GetRequiredService<KlineOnDemandService>());

                    // ===== SYMBOL REGISTRY =====
                    services.AddSingleton<SymbolRegistryService>();
                    services.AddSingleton<UniverseDryRunFileLogger>();

                    // ===== AI / CORE =====
                    services.AddSingleton<AiSelfLearningService>();

                    services.AddSingleton<AiMarketRegimeService>();
                    services.AddSingleton<Lazy<MarketDataService>>(sp => new Lazy<MarketDataService>(() => sp.GetRequiredService<MarketDataService>()));
                    services.AddSingleton<EntryTracker>();
                    services.AddSingleton<CooldownGuard>();

                    services.AddSingleton<AiPatternEngineService>();
                    services.AddSingleton<AiCorrelationService>();
                    services.AddSingleton<AiLiquidityClusterService>();
                    services.AddSingleton<AiLeverageService>();
                    services.AddSingleton<AiStopLossOptimizer>();
                    services.AddSingleton<AiRiskScalerV2>();


                    // ===== RISK / EXECUTION =====
                    services.AddSingleton<RiskManager>();          // ← теперь SymbolInfoService уже есть
                    services.AddSingleton<BinanceAlgoOrderService>();
                    services.AddSingleton<OrderExecutor>();
                    services.AddSingleton<OrderCleanerService>();
                    services.AddSingleton<PnLMonitorService>();

                    // ===== STRATEGY =====
                    services.AddSingleton<AdaptiveStrategyService>();
                    services.AddSingleton<StrategyEngine>();
                    services.AddSingleton<SmartRegimeService>();
                    services.AddSingleton<ReverseProbeEngine>();
                    services.AddSingleton<IAccountStateService, AccountStateService>();

                    // ===== STRATEGY v9: MEAN-REVERSION + ROUTER =====
                    // StrategyEngine above is the existing trend-following
                    // engine — untouched. These new pieces add a parallel
                    // mean-reversion strategy plus a thin router that
                    // decides which engine's signals reach the order
                    // pipeline (regime-based auto, or a manual override —
                    // see Strategy:Mode in appsettings.json / the Web UI
                    // toggle for live switching without a restart).
                    services.Configure<MeanReversionOptions>(
                        ctx.Configuration.GetSection("MeanReversion"));
                    services.AddSingleton<MeanReversionEngine>();
                    services.AddSingleton<VertexAutoTradeBinance8.Strategy.StrategyCore.StrategyCoreEngine>();
                    services.Configure<SymbolSelectionOptions>(
                        ctx.Configuration.GetSection("SymbolSelection"));
                    services.Configure<PullbackEntryOptions>(
                        ctx.Configuration.GetSection("Strategy:PullbackEntry"));
                    services.Configure<DcaOptions>(
                        ctx.Configuration.GetSection("Dca"));
                    services.AddSingleton(sp =>
                    {
                        // NOTE: uses "StrategyRouting:Mode", NOT "Strategy:Mode" —
                        // that path is already used by the existing pullback
                        // config (Strategy:Mode="Pullback"), unrelated to this
                        // Auto/TrendOnly/MeanReversionOnly routing setting.
                        var startupMode = ctx.Configuration["StrategyRouting:Mode"];
                        var parsed = Enum.TryParse<StrategyMode>(startupMode, ignoreCase: true, out var m)
                            ? m
                            : StrategyMode.Auto;
                        return new StrategyModeState(parsed);
                    });
                    services.AddSingleton<StrategyRouter>();


                    // ===== SUPERVISOR / STATE =====
                    services.AddSingleton<PositionSupervisorService>();
                    services.AddSingleton<PositionGuardService>();
                    services.AddSingleton<PositionProtectorService>();
                    services.AddSingleton<TradeStateManager>();
                    services.AddSingleton<EngineStateBuilder>();
                    services.AddSingleton<EngineStateSnapshotService>();

                    services.AddSingleton<DecisionMarkerSink>();
                    services.AddSingleton<DecisionMarkersFileService>();

                    // ===== HOSTED =====
                    services.AddHostedService<SupervisorBootstrapHostedService>();
                    services.AddHostedService<BackgroundMarketScannerService>();
                    services.AddHostedService<DcaService>();
                    services.AddHostedService<TradingWorker>();
                    services.AddHostedService<BinanceUserDataHostedService>();
                    services.AddHostedService<KlineSnapshotLiveSaver>();
                    // Warms 1h/4h/1d for every universe symbol (not just pinned).
                    // Waits on BootGate internally, so it never delays startup.
                    services.AddHostedService<HtfUniverseWarmupService>();
                    // Register as singleton first so OrderExecutor / PSS can inject it
                    services.AddSingleton<MarketDataPushClient>();
                    services.AddHostedService(sp => sp.GetRequiredService<MarketDataPushClient>());
                    services.AddHostedService<DecisionMarkersPersistenceHostedService>();
                    services.AddHostedService<StrategyModeFileWatcher>();



                    // ===== MISC =====
                    services.AddSingleton<ExecutedSignalService>();
                    services.AddSingleton<IDecisionTraceService, DecisionTraceFileService>();
                    services.AddSingleton<IOpenPositionProvider, EngineStateOpenPositionProvider>();


                    services.AddSingleton<OpenPositionSymbolTracker>();
                    services.AddSingleton<IOpenPositionSymbolSource>(
                        sp => sp.GetRequiredService<OpenPositionSymbolTracker>());
                    // DataDbSymbolFeed aggregates: appsettings list + Pinned +
                    // live universe (SymbolRegistryService) + open positions +
                    // seen signals. Registered AFTER all sources it depends on.
                    services.AddSingleton<VertexAutoTradeBinance8.Services.HistoricalData.DataDbSymbolFeed>();


                    services.AddSingleton<IBootGate, BootGate>();

                    services.AddSingleton<IPositionRecoveryService, PositionRecoveryService>();
                    services.AddSingleton<IStrategyPreFilter, StrategyPreFilterService>();




                    services.AddSingleton<SimulatedTradeService>();
                    services.AddSingleton<LiveSignalService>();
                    services.AddSingleton<AiModelSnapshotService>();
                    services.AddSingleton<TradeResultMonitorService>();
              
                    services.AddSingleton<TradeSignalMemoryService>();
                    services.AddSingleton<OrderTracerService>();
                    services.AddSingleton<RecoverLostOrdersService>();
                    services.AddSingleton<ManualPositionHandler>();


                    services.AddSingleton<LiquidityGuardService>();


                    services.Configure<EngineStateSettings>(ctx.Configuration.GetSection("EngineState"));

                    services.AddSingleton<PredictiveEngineV4ConfirmationService>();


                    services.AddSingleton<AiTimeframeSelectorService>();

                    services.AddSingleton<IOrderDispatcher, OrderDispatcher>();
                    services.AddSingleton<BinanceUserDataSubscriber>();
                    // =====================================================
                    // Funding Rate Service — динамическое отслеживание
                    // funding rate в реальном времени (каждые 60 сек)
                    // =====================================================
                    services.AddSingleton<FundingRateService>();
                    services.AddHostedService(sp => sp.GetRequiredService<FundingRateService>());
                    // =====================================================
                    // Liquidation Risk Engine — pre-trade расчёт цены
                    // ликвидации + real-time мониторинг открытых позиций
                    // =====================================================
                    services.AddSingleton<LiquidationRiskEngine>();
                    services.AddHostedService(sp => sp.GetRequiredService<LiquidationRiskEngine>());
                    // =====================================================
                    // Realtime Momentum Detector — event-driven обнаружение
                    // начала тренда на 1M свечах (без polling задержки)
                    // =====================================================
                    services.AddSingleton<RealtimeMomentumDetector>();


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





