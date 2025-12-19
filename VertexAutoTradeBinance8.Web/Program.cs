using Microsoft.AspNetCore.SignalR;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.MarketData;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Services.Interface;
using VertexAutoTradeBinance8.Services.State;
using VertexAutoTradeBinance8.Web.Data;
 
using VertexAutoTradeBinance8.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// ===============================
// Конфиги (правильная версия)
// ===============================
builder.Configuration
    .SetBasePath(builder.Environment.ContentRootPath)
    .AddJsonFile("appsettings.web.json", optional: false, reloadOnChange: true)         // основной конфиг Web
    .AddJsonFile($"appsettings.web.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true) // Dev/Staging/Prod
    .AddEnvironmentVariables();

// Если хочешь, можно добавить User Secrets только в Development
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>();
}


// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSingleton<WeatherForecastService>();


// ============= DEPENDENCY INJECTION =============
builder.Services.Configure<TradingOptions>(
    builder.Configuration.GetSection("TradingOptions"));

builder.Services.Configure<BinanceOptions>(
    builder.Configuration.GetSection("Binance"));


builder.Services.AddSingleton<AiStopLossOptimizer>();


// ========================================================================
//  CORE SERVICES (общие для анализа и Dashboard)
// ========================================================================
builder.Services.AddSingleton<BinanceClientFactory>();
builder.Services.AddSingleton<MarketDataKlineBuffer>();
builder.Services.AddSingleton<WsKlineSubscriber>();
builder.Services.AddSingleton<MarketDataFacade>();
builder.Services.AddSingleton<MarketDataService>();

builder.Services.AddSingleton<SmartRegimeService>();
builder.Services.AddSingleton<SymbolInfoService>();
builder.Services.AddSingleton<AiCorrelationService>();

// Учебный модуль
builder.Services.AddSingleton<AiSelfLearningService>();
builder.Services.AddSingleton<AiLearningFileService>();
builder.Services.AddSingleton<MissedTradeFileService>();
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

 // Режим рынка (AI Smart Regime)
builder.Services.AddSingleton<AiMarketRegimeService>();

builder.Services.AddHttpClient("api", client =>
{
    client.BaseAddress = new Uri("https://localhost:7185/"); // твой Web API адрес
});

builder.Services.AddSingleton<PositionsLiveService>();
 

// REST API для получения данных
builder.Services.AddControllers();

// Blazor Server

builder.Services.AddHttpClient();
builder.Services.AddSignalR();


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

app.UseHttpsRedirection();

app.UseStaticFiles();


app.UseRouting();
app.MapControllers();   // API маршруты
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
app.MapHub<ExecutedSignalsHub>("/hubs/executedSignals");
 
ExecutedSignalService.ExecutedSignalsChanged += async () =>
{
    var hub = app.Services.GetRequiredService<IHubContext<ExecutedSignalsHub>>();
    await hub.Clients.All.SendAsync("ExecutedSignalsUpdated");
};

app.Run();
