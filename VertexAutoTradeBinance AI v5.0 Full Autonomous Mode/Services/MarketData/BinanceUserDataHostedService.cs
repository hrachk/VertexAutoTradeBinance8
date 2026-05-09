using VertexAutoTradeBinance8.Services.Ws;

public sealed class BinanceUserDataHostedService : IHostedService
{
    private readonly BinanceUserDataSubscriber _sub;

    public BinanceUserDataHostedService(BinanceUserDataSubscriber sub)
    {
        _sub = sub;
    }

    public Task StartAsync(CancellationToken ct)
        => _sub.StartAsync(ct);

    public Task StopAsync(CancellationToken ct)
        => _sub.DisposeAsync().AsTask();
}
