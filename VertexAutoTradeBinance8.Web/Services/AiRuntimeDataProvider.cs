using VertexAutoTradeBinance8.Web.Services;

public sealed class AiRuntimeDataProvider : IAsyncDisposable
{
    private readonly AiLearningFileService _loader;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();

    private volatile AiRuntimeState? _state;

    public AiRuntimeState? Current => _state;

    public event Action? OnUpdated;

    public AiRuntimeDataProvider(AiLearningFileService loader)
    {
        _loader = loader;
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        _ = Loop();
    }

    private async Task Loop()
    {
        while (await _timer.WaitForNextTickAsync(_cts.Token))
        {
            var snap = await _loader.LoadSnapshot();
            if (snap == null) continue;

            _state = new AiRuntimeState
            {
                Snapshot = snap,
                MarketStates = snap.MarketStates
                    .OrderByDescending(x => x.Time)
                    .Take(150)
                    .ToList(),

                Trades = snap.Trades
                    .OrderByDescending(x => x.Time)
                    .Take(150)
                    .ToList(),

                LoadedAtUtc = DateTime.UtcNow
            };

            OnUpdated?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _timer.Dispose();
    }
}
