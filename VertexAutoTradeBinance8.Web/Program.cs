using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Web;
using VertexAutoTradeBinance8.Web.Data;
using VertexAutoTradeBinance8.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSingleton<WeatherForecastService>();


// ============= DEPENDENCY INJECTION =============
builder.Services.Configure<TradingOptions>(
    builder.Configuration.GetSection("TradingOptions"));

builder.Services.AddSingleton<AiStopLossOptimizer>();


// ========================================================================
//  CORE SERVICES (общие для анализа и Dashboard)
// ========================================================================
builder.Services.AddSingleton<BinanceClientFactory>();
builder.Services.AddSingleton<MarketDataService>();
builder.Services.AddSingleton<SmartRegimeService>();
builder.Services.AddSingleton<SymbolInfoService>();
builder.Services.AddSingleton<AiCorrelationService>();

// Учебный модуль
builder.Services.AddSingleton<AiSelfLearningService>();
builder.Services.AddSingleton<AiLearningFileService>();
builder.Services.AddSingleton<MissedTradeFileService>();
builder.Services.AddSingleton<EngineStateService>();
 


// Режим рынка (AI Smart Regime)
builder.Services.AddSingleton<AiMarketRegimeService>();

builder.Services.AddHttpClient("api", client =>
{
    client.BaseAddress = new Uri("https://localhost:7185/"); // твой Web API адрес
});


// REST API для получения данных
builder.Services.AddControllers();

// Blazor Server
 
builder.Services.AddHttpClient();


var app = builder.Build();

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

app.Run();
