using Microsoft.AspNetCore.SignalR;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.MarketData;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.Interface;
using VertexAutoTradeBinance8.Services.State;
using VertexAutoTradeBinance8.Web.Confs;
using VertexAutoTradeBinance8.Web.Data;
using VertexAutoTradeBinance8.Web.Hubs;
 
using VertexAutoTradeBinance8.Web.Services;
using VertexAutoTradeBinance8.Web.Services.Runtime;

var builder = WebApplication.CreateBuilder(args);

// ===============================
// Конфиги (правильная версия)
// ===============================
builder.Configuration
    .SetBasePath(builder.Environment.ContentRootPath)
    .AddJsonFile("appsettings.web.json", optional: false, reloadOnChange: true)         // основной конфиг Web
    .AddJsonFile($"appsettings.web.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true) // Dev/Staging/Prod
    .AddEnvironmentVariables();

// Adds the SAME shared runtime-override file the Engine itself loads
// (and RuntimeConfigService writes to) — needed so settings like Dca
// (configured once via the Settings page) apply identically whether
// the symbol is traded for real (Engine) or in demo mode (this Web
// process), without a separate write path for each.
{
    var sharedRoot = builder.Configuration["SharedData:Root"];
    if (!string.IsNullOrWhiteSpace(sharedRoot))
    {
        var runtimePath = Path.Combine(sharedRoot, "appsettings.runtime.json");
        builder.Configuration.AddJsonFile(runtimePath, optional: true, reloadOnChange: true);
    }
}

// Если хочешь, можно добавить User Secrets только в Development
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>();
}


// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSingleton<VertexAutoTradeBinance8.Services.Learning.TradeJournalService>();
builder.Services.AddSingleton<WeatherForecastService>();

// ── Client Auth (JSON-based, no DB) ──────────────────────────
// ClientDbService: singleton — reads/writes clients.json thread-safely
// AuthSessionService: scoped — one per Blazor circuit (per browser tab)
// ClientDataService: scoped — resolves file paths per logged-in client
builder.Services.AddSingleton<VertexAutoTradeBinance8.Web.Services.Auth.ClientDbService>();
builder.Services.AddSingleton<VertexAutoTradeBinance8.Web.Services.Auth.VerificationCodeCache>();
builder.Services.AddSingleton<VertexAutoTradeBinance8.Web.Services.Auth.EmailService>();
builder.Services.AddSingleton<VertexAutoTradeBinance8.Web.Services.Auth.OAuthService>();
builder.Services.AddHttpClient("oauth", c => { c.Timeout = TimeSpan.FromSeconds(10); });
builder.Services.AddSingleton<VertexAutoTradeBinance8.Web.Services.Auth.SessionTokenService>();
builder.Services.AddScoped<VertexAutoTradeBinance8.Web.Services.Auth.AuthSessionService>();
builder.Services.AddScoped<VertexAutoTradeBinance8.Web.Services.ClientDataService>();

builder.Services.AddSingleton<AiRuntimeDataProvider>();
builder.Services.AddSingleton<SymbolRegistryService>();
builder.Services.AddSingleton<TradePermissionFileService>();
builder.Services.AddSingleton<MarketSnapshotFileService>();
builder.Services.AddSingleton<HistoricalDataReaderService>();
builder.Services.AddSingleton<HistoricalDataWriterService>();
builder.Services.AddHttpClient("WebHistoryBootstrap");
builder.Services.AddHostedService<WebHistoryBootstrapService>();
builder.Services.AddSingleton<RealDcaHistoryReader>();
builder.Services.Configure<DcaOptions>(builder.Configuration.GetSection("Dca"));
builder.Services.AddSingleton<MarketDataLiveState>();
builder.Services.AddSingleton<VertexAutoTradeBinance8.Web.Services.DemoAccountService>();
builder.Services.AddHostedService<VertexAutoTradeBinance8.Web.Services.DemoAutoTradeService>();
builder.Services.AddSingleton<DecisionMarkersFileService>();

builder.Services.AddSingleton<IAiPulseEngine, AiPulseEngine>();
builder.Services.AddSingleton<AiSelfLearningService>();
builder.Services.AddHostedService<AiPulseBackgroundUpdater>();
builder.Services.AddHostedService<AiPulseUiFeeder>();
builder.Services.AddScoped<AiPulseUiState>();

 
// ============= DEPENDENCY INJECTION =============
builder.Services.Configure<TradingOptions>(
    builder.Configuration.GetSection("TradingOptions"));

builder.Services.Configure<ClientOptions>(
    builder.Configuration.GetSection("Client")
);

builder.Services.AddSingleton<ConfigEditorService>();
builder.Services.Configure<EngineConfigRoot>(
    builder.Configuration.GetSection("Runtime")); // future-ready

builder.Services.Configure<VertexAutoTradeBinance8.Configuration.BinanceOptions>(
    builder.Configuration.GetSection("Binance"));
builder.Services.Configure<VertexAutoTradeBinance8.Configuration.BybitOptions>(
    builder.Configuration.GetSection("Bybit"));
builder.Services.Configure<VertexAutoTradeBinance8.Configuration.ExchangeRuntimeOptions>(
    builder.Configuration.GetSection("Exchanges"));

// Needed so the Market page can determine, per symbol, whether a
// displayed signal's confidence is actually above the real entry
// threshold the Engine uses — SimulatedTradeService (which produces
// the "Live Signals" shown on /market) does NOT apply MinEntry
// filtering itself, so without this the UI has no way to distinguish
// a signal the bot would actually act on from one purely shown for
// visibility.
builder.Services.Configure<VertexAutoTradeBinance8.Configuration.SignalConfidenceSettings>(
    builder.Configuration.GetSection("SignalConfidence"));
builder.Services.AddSingleton<VertexAutoTradeBinance8.Configuration.ConfidenceResolver>();


builder.Services.AddSingleton<AiStopLossOptimizer>();
builder.Services.AddSingleton<EngineRuntimeSettings>();
 
builder.Services.AddSingleton<ConfigAdvisorService>(); // ниже

// BinanceTimeService — fetches Binance server time once and caches the
// offset so all Web-side raw signed requests use an adjusted timestamp.
// Prevents -1021 "Timestamp ahead of server time" errors caused by local
// clock drift (Windows NTP can be 1-3 s off from Binance NTP servers).
builder.Services.AddSingleton<BinanceTimeService>();


// ========================================================================
//  CORE SERVICES (общие для анализа и Dashboard)
// ========================================================================
// Per-user LIVE keys (filled by AuthSession when user switches to LIVE)
builder.Services.AddSingleton<VertexAutoTradeBinance8.Services.TradingCredentialStore>();
builder.Services.AddSingleton<BinanceClientFactory>();
builder.Services.AddSingleton<BybitClientFactory>();
builder.Services.AddSingleton<VertexAutoTradeBinance8.Web.Services.BybitAccountReadService>();
builder.Services.AddSingleton<MarketDataKlineBuffer>();
builder.Services.AddSingleton<WsKlineSubscriber>();
builder.Services.AddSingleton<MarketDataFacade>();
builder.Services.AddSingleton<MarketDataService>();

builder.Services.AddSingleton<SmartRegimeService>();
builder.Services.AddSingleton<SymbolInfoService>();
builder.Services.AddSingleton<AiCorrelationService>();

// Учебный модуль
builder.Services.AddSingleton<AiLearningFileService>();
builder.Services.AddSingleton<MissedTradeFileService>();
builder.Services.AddSingleton<LiveSignalFileService>();
builder.Services.AddSingleton<StrategyModeFileService>();
builder.Services.AddSingleton<RuntimeConfigService>();
builder.Services.AddSingleton<EngineStateService>();

builder.Services.AddSingleton<ExecutedSignalUiService>();
builder.Services.AddSingleton<ExecutedSignalsPushService>();
builder.Services.AddSingleton<ExecutedSignalService>();
builder.Services.AddSingleton<BinancePositionsWsService>();

builder.Services.AddSingleton<IAccountStateService, AccountStateService>();

builder.Services.AddScoped<LivePnlService>();


builder.Services.AddHttpClient<LivePnlService>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5253");
});

// Manual SL/TP order placement on /market reuses this exact,
// production-proven Engine service (raw HTTP, manually-signed Algo
// Order API calls) rather than Binance.Net's library calls, which
// don't properly support TP/SL conditional orders post the Dec 2025
// Algo Order API migration this service was specifically built for.
builder.Services.AddHttpClient("BinanceAlgoRaw");
builder.Services.AddSingleton<VertexAutoTradeBinance8.Services.BinanceAlgoOrderService>();

 // Режим рынка (AI Smart Regime)
builder.Services.AddSingleton<AiMarketRegimeService>();

builder.Services.AddHttpClient("api", client =>
{
    client.BaseAddress = new Uri("https://localhost:7185/"); // твой Web API адрес
});

// читаем IP и порт из конфига
string ip = builder.Configuration.GetValue<string>("Kestrel:Http:IP");
int port = builder.Configuration.GetValue<int>("Kestrel:Http:Port");

if (string.IsNullOrWhiteSpace(ip))
    throw new InvalidOperationException("Kestrel:Http:IP not configured");

if (port <= 0)
    throw new InvalidOperationException("Kestrel:Http:Port not configured");

// биндим вручную
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(System.Net.IPAddress.Parse(ip), port);
});
builder.Services.AddSingleton<PositionsLiveService>();
 

// REST API для получения данных
builder.Services.AddControllers();

// Blazor Server

builder.Services.AddHttpClient();
builder.Services.AddSignalR();
builder.Host.UseWindowsService();

var app = builder.Build();
var ws = app.Services.GetRequiredService<BinancePositionsWsService>();
_ = ws.StartAsync(); // fire & forget

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

//app.UseHttpsRedirection();

app.UseStaticFiles();


app.UseRouting();
app.MapControllers();   // API маршруты
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
app.MapHub<ExecutedSignalsHub>("/hubs/executedSignals");
app.MapHub<MarketDataHub>("/hubs/marketData");
 
ExecutedSignalService.ExecutedSignalsChanged += async () =>
{
    var hub = app.Services.GetRequiredService<IHubContext<ExecutedSignalsHub>>();
    await hub.Clients.All.SendAsync("ExecutedSignalsUpdated");
};


// Wire Demo closes -> trade-journal.json (per client; demo-account.json unchanged)
try
{
    var journal = app.Services.GetRequiredService<VertexAutoTradeBinance8.Services.Learning.TradeJournalService>();
    VertexAutoTradeBinance8.Web.Services.DemoAccountService.TradeJournalHook = (clientId, pos, exit, closeQty, pnl, reason) =>
    {
        journal.Append(new VertexAutoTradeBinance8.Services.Learning.TradeJournalEntry
        {
            ClientId = clientId,
            Source = "Demo",
            Symbol = pos.Symbol,
            Side = pos.Side,
            EntryPrice = pos.EntryPrice,
            ExitPrice = exit,
            Qty = closeQty,
            Leverage = pos.Leverage,
            StopLoss = pos.StopLoss,
            TakeProfits = pos.TakeProfits?.Select(t => t.Price).ToList() ?? new(),
            RealizedPnl = pnl,
            CloseReason = reason,
            OpenedAtUtc = pos.OpenedAtUtc,
            ClosedAtUtc = DateTime.UtcNow,
            Setup = pos.Setup ?? "",
            Mfe = pos.MaxFavorable,
            Mae = pos.MaxAdverse
        });
    };
}
catch (Exception ex)
{
    Console.WriteLine("[JOURNAL] wire failed: " + ex.Message);
}
app.Run();




