using VertexAutoTradeBinance8.Hosting;
using VertexAutoTradeBinance8.Services;

namespace VertexAutoTradeBinance8;

public class Program
{
    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        using IHost host = Host.CreateDefaultBuilder(args)
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureAppConfiguration((ctx, cfg) => cfg.AddVertexConfiguration(ctx.HostingEnvironment.ContentRootPath))
            .ConfigureLogging((ctx, logging) =>
            {
                logging.ClearProviders();
                logging.AddConfiguration(ctx.Configuration.GetSection("Logging"));
                logging.AddSimpleConsole(o =>
                {
                    o.SingleLine = true;
                    o.TimestampFormat = "HH:mm:ss ";
                    o.UseUtcTimestamp = true;
                });
                logging.AddProvider(new FileLoggerProvider("bot"));
            })
            .UseWindowsServiceIfAvailable()
            .ConfigureServices((ctx, services) =>
            {
                services.Configure<HostOptions>(o =>
                {
                    // ошибка в фоновом сервисе не должна тихо гасить торговлю/сопровождение
                    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
                    o.ShutdownTimeout = TimeSpan.FromSeconds(20);
                });

                services.AddVertexCore(ctx.Configuration);
                services.AddHostedService<BackgroundMarketScannerService>();
                services.AddHostedService<TradingWorker>();
            })
            .Build();

        await host.RunAsync();
    }
}

internal static class HostBuilderWindowsServiceExtensions
{
    /// <summary>Заглушка: в .NET 8 без пакета WindowsServices — запуск как консоль / через NSSM / systemd.</summary>
    public static IHostBuilder UseWindowsServiceIfAvailable(this IHostBuilder b) => b;
}
