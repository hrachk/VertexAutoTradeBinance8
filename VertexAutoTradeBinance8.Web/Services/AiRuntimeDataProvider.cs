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
            if (snap == null)
                continue;

            // 1) Deduplicate MarketStates: (Symbol + Timeframe) -> latest
            var latestByTf =
                snap.MarketStates
                    .GroupBy(x => new { x.Symbol, x.Timeframe })
                    .Select(g =>
                        g.OrderByDescending(x => x.Time)
                         .First()
                    );

            // 2) Limit noise per symbol (max 2 TF per symbol)
            var normalizedMarketStates =
                latestByTf
                    .GroupBy(x => x.Symbol)
                    .SelectMany(g =>
                        g.OrderByDescending(x => x.Time)
                         .Take(2)
                    )
                    .OrderByDescending(x => x.Time)
                    .Take(150)
                    .ToList();

            // 3) Trades — UI-normalized (diversified, recent)
            var uiTrades =
                snap.Trades
                    .Where(t => t.Time > DateTime.UtcNow.AddDays(-2))
                    .GroupBy(t => t.Symbol)
                    .SelectMany(g =>
                        g.OrderByDescending(t => t.Time)
                         .Take(5)
                    )
                    .OrderByDescending(t => t.Time)
                    .Take(100)
                    .ToList();

            _state = new AiRuntimeState
            {
                Snapshot = snap,
                MarketStates = normalizedMarketStates,
                Trades = uiTrades,
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
