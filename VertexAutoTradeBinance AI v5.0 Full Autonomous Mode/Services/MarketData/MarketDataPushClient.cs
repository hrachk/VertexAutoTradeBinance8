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

    private readonly Channel<Func<HubConnection, Task>> _outbox =
        Channel.CreateBounded<Func<HubConnection, Task>>(new BoundedChannelOptions(2000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private long _ticksQueued;
    private long _ticksSent;
    private long _klinesSent;
    private long _sendFailures;

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

        connection.Closed += async (error) =>
        {
            _logger.LogWarning(error, "[DashboardPush] Connection CLOSED (state={state}) — will auto-reconnect", connection.State);
            await Task.CompletedTask;
        };
        connection.Reconnecting += (error) =>
        {
            _logger.LogWarning(error, "[DashboardPush] Reconnecting...");
            return Task.CompletedTask;
        };
        connection.Reconnected += (id) =>
        {
            _logger.LogInformation("[DashboardPush] Reconnected, connectionId={id}", id);
            return Task.CompletedTask;
        };

        Action<string, decimal> onPrice = (symbol, price) =>
        {
            Interlocked.Increment(ref _ticksQueued);
            var wrote = _outbox.Writer.TryWrite(async c =>
            {
                await c.InvokeAsync("PushPrice", symbol, price);
                Interlocked.Increment(ref _ticksSent);
            });
            if (!wrote)
                _logger.LogWarning("[DashboardPush] Outbox FULL — tick dropped for {symbol}", symbol);
        };

        Action<string, KlineInterval, BinanceFuturesUsdtKline> onClosedKline = (symbol, tf, k) =>
        {
            _outbox.Writer.TryWrite(async c =>
            {
                await c.InvokeAsync(
                    "PushKlineClosed",
                    symbol,
                    tf.ToString(),
                    ToUnixMs(k.OpenTime),
                    k.OpenPrice,
                    k.HighPrice,
                    k.LowPrice,
                    k.ClosePrice,
                    k.Volume);
                Interlocked.Increment(ref _klinesSent);
            });
        };

        _ws.OnPrice += onPrice;
        _ws.OnClosedKline += onClosedKline;

        using var heartbeat = new Timer(_ =>
        {
            _logger.LogInformation(
                "[DashboardPush] heartbeat: state={state} queued={queued} ticksSent={ticksSent} klinesSent={klinesSent} failures={failures}",
                connection.State, Interlocked.Read(ref _ticksQueued), Interlocked.Read(ref _ticksSent),
                Interlocked.Read(ref _klinesSent), Interlocked.Read(ref _sendFailures));
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

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
                {
                    _logger.LogWarning("[DashboardPush] Dropping message — connection state is {state}", connection.State);
                    continue; // drop silently — chart falls back to file snapshot
                }

                try
                {
                    await send(connection);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _sendFailures);
                    _logger.LogWarning(ex, "[DashboardPush] Push failed (non-fatal)");
                }
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
