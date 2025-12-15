using System.Collections.Concurrent;
using System.Net.Http.Json;

namespace VertexAutoTradeBinance8.Web.Services;

public class LivePnlService : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<LivePnlService> _logger;

    private readonly ConcurrentDictionary<string, LivePos> _map = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action? Updated;

    public LivePnlService(HttpClient http, ILogger<LivePnlService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public sealed class LivePos
    {
        public string Symbol { get; set; } = "";
        public string PositionSide { get; set; } = ""; // LONG/SHORT
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

    private static string Key(string symbol, string posSide) => $"{symbol}|{posSide}".ToUpperInvariant();

    public LivePos? Get(string symbol, string posSide)
    {
        _map.TryGetValue(Key(symbol, posSide), out var v);
        return v;
    }

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
                    var data = await _http.GetFromJsonAsync<List<LivePos>>(url, ct) ?? new();

                    // обновляем map
                    foreach (var p in data)
                    {
                        // в Hedge Binance вернёт "Long"/"Short" → мы приводим к LONG/SHORT
                        var ps = (p.PositionSide ?? "").ToUpperInvariant();
                        if (ps == "LONG" || ps == "SHORT")
                            _map[Key(p.Symbol, ps)] = p;
                    }

                    Updated?.Invoke();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[LIVE] poll failed");
                }

                try { await Task.Delay(intervalMs, ct); }
                catch { }
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
