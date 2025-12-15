using System.Threading.Channels;
 

public sealed class OrderDispatcher : BackgroundService, IOrderDispatcher
{
    private readonly Channel<Func<CancellationToken, Task>> _queue =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>();

    public void Enqueue(Func<CancellationToken, Task> orderAction)
    {
        _queue.Writer.TryWrite(orderAction);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var action in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                _ = Task.Run(() => action(stoppingToken), stoppingToken);
            }
            catch { /* swallow */ }
        }
    }
}
