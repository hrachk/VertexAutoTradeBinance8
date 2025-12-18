using Microsoft.Extensions.Hosting;

namespace VertexAutoTradeBinance8.Services.Ws
{
    public sealed class BinanceUserDataHostedService : IHostedService
    {
        private readonly BinanceUserDataSubscriber _subscriber;

        public BinanceUserDataHostedService(BinanceUserDataSubscriber subscriber)
        {
            _subscriber = subscriber;
        }

        public Task StartAsync(CancellationToken cancellationToken)
            => _subscriber.StartAsync(cancellationToken);

        public async Task StopAsync(CancellationToken cancellationToken)
            => await _subscriber.DisposeAsync();
    }
}
