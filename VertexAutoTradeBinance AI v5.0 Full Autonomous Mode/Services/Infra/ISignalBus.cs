using System.Threading.Channels;

namespace VertexAutoTradeBinance8.Services.Infra;

public sealed class LiveSignalEvent
{
    public string Symbol { get; init; } = "";
    public string Side { get; init; } = "";
    public decimal Entry { get; init; }
    public decimal StopLoss { get; init; }
    public decimal Confidence { get; init; }
    public string Reason { get; init; } = "";
    public DateTime Utc { get; init; } = DateTime.UtcNow;
}

public interface ISignalBus
{
    ValueTask PublishAsync(LiveSignalEvent ev, CancellationToken ct = default);
    ChannelReader<LiveSignalEvent> Reader { get; }
    event Action<LiveSignalEvent>? SignalPublished;
}

/// <summary>In-process fan-out: Channel + event. File write remains for cross-process Web.</summary>
public sealed class InProcessSignalBus : ISignalBus
{
    private readonly Channel<LiveSignalEvent> _ch =
        Channel.CreateBounded<LiveSignalEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        });

    public ChannelReader<LiveSignalEvent> Reader => _ch.Reader;
    public event Action<LiveSignalEvent>? SignalPublished;

    public ValueTask PublishAsync(LiveSignalEvent ev, CancellationToken ct = default)
    {
        _ch.Writer.TryWrite(ev);
        try { SignalPublished?.Invoke(ev); } catch { }
        return ValueTask.CompletedTask;
    }
}
