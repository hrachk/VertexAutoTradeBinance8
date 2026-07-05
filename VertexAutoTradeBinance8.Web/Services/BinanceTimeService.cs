namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// Singleton that keeps a running clock-offset between the local machine
/// and Binance server time.  All Web-side raw Binance HTTP calls must use
/// <see cref="NowMs"/> instead of DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
/// so that signed requests are accepted even when the local clock is
/// slightly ahead of Binance's NTP — the root cause of -1021 errors.
///
/// The offset is computed once at first use and refreshed every 10 minutes.
/// GET /fapi/v1/time is a public, unsigned endpoint — no credentials needed.
/// </summary>
public sealed class BinanceTimeService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<BinanceTimeService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private long   _offsetMs  = 0;           // serverTime - localTime (negative = local clock ahead)
    private DateTime _syncedAt = DateTime.MinValue;
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(10);

    private const string FapiTimeUrl = "https://fapi.binance.com/fapi/v1/time";

    public BinanceTimeService(IHttpClientFactory httpFactory, ILogger<BinanceTimeService> logger)
    {
        _httpFactory = httpFactory;
        _logger      = logger;
    }

    /// <summary>
    /// Returns a Binance-aligned Unix timestamp in milliseconds.
    /// Syncs with the server on first call and every 10 minutes thereafter.
    /// </summary>
    public async Task<long> NowMsAsync(CancellationToken ct = default)
    {
        await EnsureSyncedAsync(ct);
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _offsetMs;
    }

    /// <summary>
    /// Force an immediate re-sync.  Call this when a -1021 response is received
    /// so the next request is corrected without waiting for the normal interval.
    /// </summary>
    public void InvalidateSync() => _syncedAt = DateTime.MinValue;

    private async Task EnsureSyncedAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _syncedAt < SyncInterval)
            return;

        await _lock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _syncedAt < SyncInterval)
                return;

            await SyncAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        try
        {
            using var http = _httpFactory.CreateClient("BinanceTimeSync");
            http.Timeout = TimeSpan.FromSeconds(5);

            var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var resp = await http.GetAsync(FapiTimeUrl, ct);
            var after  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (!resp.IsSuccessStatusCode) return;

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc  = System.Text.Json.JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("serverTime", out var st)) return;

            long serverMs   = st.GetInt64();
            long localMs    = (before + after) / 2;   // mid-point to compensate RTT
            _offsetMs       = serverMs - localMs;
            _syncedAt       = DateTime.UtcNow;

            if (Math.Abs(_offsetMs) > 500)
                _logger.LogWarning(
                    "[BinanceTimeService] Clock offset = {off}ms — timestamps adjusted automatically",
                    _offsetMs);
            else
                _logger.LogDebug("[BinanceTimeService] Synced. Offset = {off}ms", _offsetMs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BinanceTimeService] Sync failed — using raw local clock");
        }
    }
}
