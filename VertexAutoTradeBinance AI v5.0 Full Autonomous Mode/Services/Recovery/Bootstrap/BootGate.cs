using System.Threading.Tasks;

namespace VertexAutoTradeBinance8.Services.Bootstrap;

public sealed class BootGate : IBootGate
{
    private readonly TaskCompletionSource<bool> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady => _tcs.Task.IsCompleted;

    public Task WaitReadyAsync(CancellationToken ct)
        => _tcs.Task.WaitAsync(ct);

    public void MarkReady()
        => _tcs.TrySetResult(true);
}
