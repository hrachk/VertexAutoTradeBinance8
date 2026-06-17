using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures;
using Microsoft.AspNetCore.SignalR.Client;
using System.Threading.Channels;

namespace VertexAutoTradeBinance8.Services;

/// <summary>
/// Relays the Engine's already-live Binance WS feed (WsKlineSubscriber.OnPrice /
/// OnClosedKline) to the Web dashboard process over a local SignalR connection,
/// so the /market chart can update tick-by-tick instead of waiting on the
/// 45s file snapshot. Opens ZERO new Binance connections — it only forwards
/// events that are already firing for trading purposes.
///
/// Events are queued (not invoked synchronously) so a slow/disconnected Web
/// process can never add latency to the WS callback path that StrategyEngine
/// and RealtimeMomentumDetector depend on.
/// </summary>
public sealed class MarketDataPushClient : BackgroundService
{
    private readonly WsKlineSubscriber _ws;
    private readonly IConfiguration _cfg;
    private readonly ILogger<MarketDataPushClient> _logger;

    private readonly Channel<Action<HubConnection>> _outbox =
        Channel.CreateBounded<Action<HubConnection>>(new BoundedChannelOptions(2000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    public MarketDataPushClient(
        WsKlineSubscriber ws,
        IConfiguration cfg,
        ILogger<MarketDataPushClient> logger)
    {
        _ws = ws;
        _cfg = cfg;
        _logger = logger;
    }

    private static long ToUnixMs(DateTime dt)
        => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_cfg.GetValue("DashboardPush:Enabled", true))
        {
            _logger.LogInformation("[DashboardPush] Disabled via config — skipping");
            return;
        }

        var url = _cfg["DashboardPush:Url"];
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogWarning("[DashboardPush] DashboardPush:Url not configured — skipping");
            return;
        }

        var connection = new HubConnectionBuilder()
            .WithUrl($"{url.TrimEnd('/')}/hubs/marketData")
            .WithAutomaticReconnect()
            .Build();

        Action<string, decimal> onPrice = (symbol, price) =>
        {
            _outbox.Writer.TryWrite(c => _ = c.InvokeAsync("PushPrice", symbol, price));
        };

        Action<string, KlineInterval, BinanceFuturesUsdtKline> onClosedKline = (symbol, tf, k) =>
        {
            _outbox.Writer.TryWrite(c => _ = c.InvokeAsync(
                "PushKlineClosed",
                symbol,
                tf.ToString(),
                ToUnixMs(k.OpenTime),
                k.OpenPrice,
                k.HighPrice,
                k.LowPrice,
                k.ClosePrice,
                k.Volume));
        };

        _ws.OnPrice += onPrice;
        _ws.OnClosedKline += onClosedKline;

        try
        {
            // Connect with retry — Web may start after Engine, or restart independently
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await connection.StartAsync(ct);
                    _logger.LogInformation("[DashboardPush] Connected to {url}", url);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DashboardPush] Connect failed, retrying in 5s");
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }

            await foreach (var send in _outbox.Reader.ReadAllAsync(ct))
            {
                if (connection.State != HubConnectionState.Connected)
                    continue; // drop silently — chart falls back to file snapshot

                try { send(connection); }
                catch (Exception ex) { _logger.LogWarning(ex, "[DashboardPush] Push failed (non-fatal)"); }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown — normal
        }
        finally
        {
            _ws.OnPrice -= onPrice;
            _ws.OnClosedKline -= onClosedKline;
            try { await connection.DisposeAsync(); } catch { }
        }
    }
}
