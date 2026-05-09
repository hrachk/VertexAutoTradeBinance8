using System.Collections.Concurrent;
using System.Net.Http.Json;

namespace VertexAutoTradeBinance8.Web.Services;

public sealed class LivePositionsService : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<LivePositionsService> _logger;

    private readonly ConcurrentDictionary<string, LivePosition> _map = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action? Updated;

    public LivePositionsService(HttpClient http, ILogger<LivePositionsService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public sealed class LivePosition
    {
        public string Symbol { get; set; } = "";
        public string PositionSide { get; set; } = ""; // LONG / SHORT
        public decimal PositionAmt { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal MarkPrice { get; set; }
        public decimal UnrealizedPnl { get; set; }
        public decimal Notional { get; set; }
        public decimal? LiquidationPrice { get; set; }
        public decimal? IsolatedMargin { get; set; }
        public int? Leverage { get; set; }
        public DateTime TimeUtc { get; set; }
    }

    private static string Key(string symbol, string side)
        => $"{symbol}|{side}".ToUpperInvariant();

    public IReadOnlyCollection<LivePosition> GetAll()
        => (IReadOnlyCollection<LivePosition>)_map.Values;

    public void Start(string symbolsCsv, int intervalMs = 900)
    {
        Stop();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var url = $"/api/exchange/live-positions?symbols={Uri.EscapeDataString(symbolsCsv)}";
                    var data = await _http.GetFromJsonAsync<List<LivePosition>>(url, ct) ?? new();

                    _map.Clear();
                    foreach (var p in data)
                    {
                        var side = p.PositionSide.ToUpperInvariant();
                        if (side == "LONG" || side == "SHORT")
                            _map[Key(p.Symbol, side)] = p;
                    }

                    Updated?.Invoke();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[LIVE-POSITIONS] poll failed");
                }

                try { await Task.Delay(intervalMs, ct); } catch { }
            }
        }, ct);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _loop = null;
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
