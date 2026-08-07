using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// OrderDispatcher v2 (FIXED)
///
/// БЫЛО (баги):
///   1) Класс BackgroundService, но в DI регистрировался ТОЛЬКО как AddSingleton
///      → ExecuteAsync никогда не запускался → канал никогда не читался
///      → ВСЕ SL/TP/BE/трейлинг, поставленные через Enqueue, НИКОГДА не отправлялись на биржу.
///   2) Даже если бы запускался: `_ = Task.Run(...)` — fire-and-forget без await,
///      без сериализации (rate-limit burst) и с проглатыванием исключений.
///
/// СТАЛО:
///   - Действия выполняются последовательно и await-ятся.
///   - Исключения логируются, а не теряются.
///   - Минимальный интервал между вызовами → защита от -1003 (rate limit).
///   - Enqueue возвращает bool, чтобы вызывающий знал о переполнении очереди.
/// </summary>
public sealed class OrderDispatcher : BackgroundService, IOrderDispatcher
{
    private const int QueueCapacity = 2000;
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(60);

    private readonly ILogger<OrderDispatcher> _logger;

    private readonly Channel<Func<CancellationToken, Task>> _queue =
        Channel.CreateBounded<Func<CancellationToken, Task>>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false
            });

    private DateTime _lastRunUtc = DateTime.MinValue;

    public OrderDispatcher(ILogger<OrderDispatcher> logger)
    {
        _logger = logger;
    }

    public bool Enqueue(Func<CancellationToken, Task> orderAction)
    {
        if (orderAction == null)
            return false;

        var ok = _queue.Writer.TryWrite(orderAction);

        if (!ok)
            _logger.LogError("[DISPATCHER] QUEUE FULL ({cap}) → action DROPPED", QueueCapacity);

        return ok;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogWarning("[DISPATCHER] started");

        await foreach (var action in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            var gap = DateTime.UtcNow - _lastRunUtc;
            if (gap < MinGap)
                await Task.Delay(MinGap - gap, stoppingToken);

            _lastRunUtc = DateTime.UtcNow;

            try
            {
                await action(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DISPATCHER] action failed");
            }
        }

        _logger.LogWarning("[DISPATCHER] stopped");
    }
}
