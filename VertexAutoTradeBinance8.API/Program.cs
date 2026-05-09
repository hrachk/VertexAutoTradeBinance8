using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VertexAutoTradeBinance8.Configuration;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Strategy;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;
var builder = WebApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────
// Logging
// ─────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// ─────────────────────────────────────────────────────────────
// Configuration binding (Binance, Trading)
// ─────────────────────────────────────────────────────────────
builder.Services.Configure<BinanceOptions>(builder.Configuration.GetSection("Binance"));
builder.Services.Configure<TradingOptions>(builder.Configuration.GetSection("Trading"));

// ─────────────────────────────────────────────────────────────
// Core services из твоего основного проекта
// ─────────────────────────────────────────────────────────────
builder.Services.AddSingleton<BinanceClientFactory>();
builder.Services.AddSingleton<MarketDataService>();
builder.Services.AddSingleton<RiskManager>();
builder.Services.AddSingleton<OrderExecutor>();

builder.Services.AddSingleton<AiCorrelationService>();
builder.Services.AddSingleton<AiMarketRegimeService>();
builder.Services.AddSingleton<AiPatternEngineService>();
builder.Services.AddSingleton<AdaptiveStrategyService>();
builder.Services.AddSingleton<AiSelfLearningService>();
builder.Services.AddSingleton<AiModelSnapshotService>();
builder.Services.AddSingleton<TradeResultMonitorService>();

builder.Services.AddSingleton<AiLiquidityClusterService>();
builder.Services.AddSingleton<StrategyEngine>();
builder.Services.AddSingleton<SymbolInfoService>();
builder.Services.AddSingleton<LiquidityGuardService>();
builder.Services.AddSingleton<PositionGuardService>();
builder.Services.AddSingleton<PositionProtectorService>();
builder.Services.AddSingleton<PnLMonitorService>();
builder.Services.AddSingleton<OrderCleanerService>();
builder.Services.AddSingleton<PositionSupervisorService>();
builder.Services.AddSingleton<PredictiveEngineV4ConfirmationService>();
builder.Services.AddSingleton<AiStopLossOptimizer>();
builder.Services.AddSingleton<AiRiskScalerV2>();
builder.Services.AddSingleton<SmartRegimeService>();

// ─────────────────────────────────────────────────────────────
// ASP.NET Core
// ─────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();

app.MapControllers();

app.Run();
