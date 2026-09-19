using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Services.News;

namespace VertexAutoTradeBinance8.Services.Learning;

/// <summary>Marks symbols opened as SoftSkip probe (0.25x) for journal on close.</summary>
public static class ProbeEntryTracker
{
    private static readonly ConcurrentDictionary<string, DateTime> _open =
        new(StringComparer.OrdinalIgnoreCase);

    public static void MarkOpened(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        _open[symbol] = DateTime.UtcNow;
    }

    public static bool TryConsume(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        return _open.TryRemove(symbol, out _);
    }

    public static bool IsMarked(string symbol) =>
        !string.IsNullOrWhiteSpace(symbol) && _open.ContainsKey(symbol);
}

/// <summary>Rolling BTC/ETH prices for delta at trade close.</summary>
public sealed class MarketDeltaTracker : BackgroundService
{
    private readonly ILogger<MarketDeltaTracker> _log;
    private readonly ConcurrentQueue<(DateTime t, decimal btc, decimal eth)> _hist = new();
    private decimal _lastBtc, _lastEth;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public MarketDeltaTracker(ILogger<MarketDeltaTracker> log) => _log = log;

    public void Observe(decimal btc, decimal eth)
    {
        if (btc > 0) _lastBtc = btc;
        if (eth > 0) _lastEth = eth;
        if (_lastBtc <= 0 && _lastEth <= 0) return;
        _hist.Enqueue((DateTime.UtcNow, _lastBtc, _lastEth));
        while (_hist.Count > 120 && _hist.TryDequeue(out _)) { }
    }

    /// <summary>Pct change over lookback (default 5 minutes).</summary>
    public (decimal btcPct, decimal ethPct) GetDeltaPct(TimeSpan? lookback = null)
    {
        var lb = lookback ?? TimeSpan.FromMinutes(5);
        var cutoff = DateTime.UtcNow - lb;
        var arr = _hist.Where(x => x.t >= cutoff).ToList();
        if (arr.Count < 2)
        {
            // fall back to oldest available
            arr = _hist.ToList();
            if (arr.Count < 2) return (0, 0);
        }
        var a = arr[0];
        var b = arr[^1];
        decimal btcPct = a.btc > 0 && b.btc > 0 ? (b.btc - a.btc) / a.btc * 100m : 0;
        decimal ethPct = a.eth > 0 && b.eth > 0 ? (b.eth - a.eth) / a.eth * 100m : 0;
        return (btcPct, ethPct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("[MKT-DELTA] tracker started (BTC/ETH 5m window)");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshFromBinanceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[MKT-DELTA] refresh failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task RefreshFromBinanceAsync(CancellationToken ct)
    {
        // Public ticker price — no keys
        async Task<decimal> Px(string sym)
        {
            var url = $"https://fapi.binance.com/fapi/v1/ticker/price?symbol={sym}";
            var json = await _http.GetStringAsync(url, ct);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("price", out var p) &&
                decimal.TryParse(p.GetString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                return v;
            return 0;
        }
        var btc = await Px("BTCUSDT");
        var eth = await Px("ETHUSDT");
        Observe(btc, eth);
    }
}

public interface ICloseContextProvider
{
    (decimal btcPct, decimal ethPct, bool newsActive) GetContext();
}

public sealed class CloseContextProvider : ICloseContextProvider
{
    private readonly MarketDeltaTracker _delta;
    private readonly INewsCatalystService? _news;

    public CloseContextProvider(MarketDeltaTracker delta, INewsCatalystService? news = null)
    {
        _delta = delta;
        _news = news;
    }

    public (decimal btcPct, decimal ethPct, bool newsActive) GetContext()
    {
        var (b, e) = _delta.GetDeltaPct(TimeSpan.FromMinutes(5));
        bool news = false;
        try
        {
            var d = _news?.TryGetActiveDirective();
            news = d != null;
        }
        catch { }
        return (b, e, news);
    }
}
