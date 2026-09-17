using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VertexAutoTradeBinance8.Services.Infra;

/// <summary>
/// Polls Binance /fapi/v1/time every 15 minutes and exposes server-offset for recvWindow safety.
/// </summary>
public sealed class BinanceTimeSyncService : BackgroundService
{
    private readonly ILogger<BinanceTimeSyncService> _log;
    private readonly IConfiguration _cfg;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private long _offsetMs;
    private DateTime _lastSyncUtc = DateTime.MinValue;

    public BinanceTimeSyncService(ILogger<BinanceTimeSyncService> log, IConfiguration cfg)
    {
        _log = log;
        _cfg = cfg;
    }

    public long OffsetMs => Interlocked.Read(ref _offsetMs);
    public DateTime LastSyncUtc => _lastSyncUtc;

    public long ServerTimeMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + OffsetMs;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SyncOnceAsync(stoppingToken); }
            catch (Exception ex) { _log.LogDebug(ex, "[TIME-SYNC] failed"); }

            try { await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    public async Task SyncOnceAsync(CancellationToken ct = default)
    {
        var baseUrl = _cfg["Binance:FuturesBaseUrl"] ?? "https://fapi.binance.com";
        var url = baseUrl.TrimEnd('/') + "/fapi/v1/time";
        var localBefore = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        // {"serverTime":1710000000000}
        var idx = json.IndexOf(':');
        if (idx < 0) return;
        var num = new string(json.Skip(idx + 1).Where(char.IsDigit).ToArray());
        if (!long.TryParse(num, out var serverTime)) return;
        var localAfter = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var localMid = (localBefore + localAfter) / 2;
        var offset = serverTime - localMid;
        Interlocked.Exchange(ref _offsetMs, offset);
        _lastSyncUtc = DateTime.UtcNow;
        _log.LogInformation("[TIME-SYNC] offset={ms}ms server={st}", offset, serverTime);
    }
}
