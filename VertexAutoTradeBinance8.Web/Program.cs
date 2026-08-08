using Microsoft.AspNetCore.SignalR;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Web.Confs;
using VertexAutoTradeBinance8.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// ============================================================================
// CONFIGURATION
// ============================================================================
builder.Configuration
    .SetBasePath(builder.Environment.ContentRootPath)
    .AddJsonFile("appsettings.web.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.web.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

if (builder.Environment.IsDevelopment())
    builder.Configuration.AddUserSecrets<Program>();

// ============================================================================
// FRAMEWORK
// ============================================================================
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddControllers();
builder.Services.AddSignalR();

// ============================================================================
// OPTIONS
// ============================================================================
builder.Services.Configure<TradingOptions>(builder.Configuration.GetSection("TradingOptions"));
builder.Services.Configure<Binance.Net.Objects.Options.BinanceOptions>(builder.Configuration.GetSection("Binance"));

// ============================================================================
// ENGINE CORE (общее с движком)
// ============================================================================
builder.Services.AddSingleton<BinanceClientFactory>();
builder.Services.AddSingleton<MarketDataService>();
builder.Services.AddSingleton<SymbolInfoService>();
builder.Services.AddSingleton<SmartRegimeService>();
builder.Services.AddSingleton<AiMarketRegimeService>();
builder.Services.AddSingleton<AiCorrelationService>();
builder.Services.AddSingleton<AiStopLossOptimizer>();
builder.Services.AddSingleton<AiSelfLearningService>();
builder.Services.AddSingleton<ExecutedSignalService>();

// ============================================================================
// CONSOLE SERVICES
// ============================================================================
builder.Services.AddSingleton<EngineStateService>();
builder.Services.AddSingleton<AiLearningFileService>();
builder.Services.AddSingleton<MissedTradeFileService>();
builder.Services.AddSingleton<ExecutedSignalUiService>();
builder.Services.AddSingleton<ExecutedSignalsPushService>();
builder.Services.AddSingleton<ProtectionUiService>();

// ============================================================================
// HTTP CLIENTS
//
// "self" ходит в собственные контроллеры, поэтому базовый адрес берётся из
// конфига: при смене порта в launchSettings ломался именно этот вызов.
// "BinancePublic" — только публичные эндпоинты, без ключей.
// ============================================================================
var selfUrl = builder.Configuration["Console:SelfUrl"] ?? "http://localhost:5000";

builder.Services.AddHttpClient("self", c =>
{
    c.BaseAddress = new Uri(selfUrl);
    c.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient("BinancePublic", c =>
{
    c.BaseAddress = new Uri("https://fapi.binance.com");
    c.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient();

var app = builder.Build();

// ============================================================================
// PIPELINE
// ============================================================================
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapControllers();
app.MapBlazorHub();
app.MapHub<ExecutedSignalsHub>("/hubs/executedSignals");
app.MapFallbackToPage("/_Host");

// Движок сообщает об изменении журнала → рассылаем всем подключённым консолям
ExecutedSignalService.ExecutedSignalsChanged += async () =>
{
    var hub = app.Services.GetRequiredService<IHubContext<ExecutedSignalsHub>>();
    await hub.Clients.All.SendAsync("ExecutedSignalsUpdated");
};

app.Run();
