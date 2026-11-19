using Microsoft.Extensions.Hosting.WindowsServices;
using VertexAutoTradeBinance8.Hosting;
using VertexAutoTradeBinance8.Web.Security;
using VertexAutoTradeBinance8.Web.Services;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : default
});
builder.Host.UseWindowsService(o => o.ServiceName = "VertexTerminalWeb");

// ---------------- configuration ----------------
builder.Configuration
    .SetBasePath(builder.Environment.ContentRootPath)
    .AddJsonFile("appsettings.web.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.web.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

// конфиг бота (Trading/Binance) — тот же файл, что использует worker
var botSettingsPath = builder.Configuration["BotSettings:Path"];
if (!string.IsNullOrWhiteSpace(botSettingsPath) && File.Exists(botSettingsPath))
    builder.Configuration.AddJsonFile(botSettingsPath, optional: true, reloadOnChange: true);

builder.Configuration.AddVertexConfiguration(builder.Environment.ContentRootPath); // Local.json + env (секреты)
if (builder.Environment.IsDevelopment())
    builder.Configuration.AddUserSecrets<Program>(optional: true);

builder.Logging.AddProvider(new FileLoggerProvider("web"));

// ---------------- services ----------------
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor(o => o.DetailedErrors = builder.Environment.IsDevelopment());
builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("binance", c =>
{
    c.BaseAddress = new Uri("https://fapi.binance.com");
    c.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped(sp =>
{
    var nav = sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
});

// торговое ядро (read-mostly в Web: позиции, журнал, управляющий файл)
builder.Services.AddVertexCore(builder.Configuration);

// UI services
builder.Services.AddSingleton<AiLearningFileService>();
builder.Services.AddSingleton<MissedTradeFileService>();
builder.Services.AddSingleton<EngineStateService>();
builder.Services.AddSingleton<TradingSettingsService>();
builder.Services.AddSingleton<ExecutedSignalUiService>();
builder.Services.AddSingleton<OpenOrdersUiService>();
builder.Services.AddSingleton<TerminalDataService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseMiddleware<AccessKeyMiddleware>();
app.UseRouting();
app.MapControllers();
app.MapBlazorHub();
app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));
app.MapFallbackToPage("/_Host");

app.Run();
