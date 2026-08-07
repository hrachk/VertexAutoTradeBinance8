using System.Net.Http.Json;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// Один опрос /api/protection/summary на всё приложение.
///
/// Раньше каждая страница держала свой таймер и свой HttpClient — при
/// нескольких открытых вкладках это множило запросы к Binance. Здесь singleton
/// с одним циклом: компоненты подписываются на Changed и перерисовываются.
/// Опрос идёт, только пока есть хотя бы один подписчик.
/// </summary>
public sealed class ProtectionUiService : IAsyncDisposable
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<ProtectionUiService> _logger;
    private readonly TimeSpan _interval;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _subscribers;

    private ProtectionSummary _snapshot = new();

    public event Action? Changed;

    public ProtectionUiService(
        IHttpClientFactory factory,
        IConfiguration config,
        ILogger<ProtectionUiService> logger)
    {
        _factory = factory;
        _logger = logger;

        var ms = config.GetValue<int?>("Console:ProtectionPollMs") ?? 4000;
        _interval = TimeSpan.FromMilliseconds(Math.Max(1000, ms));
    }

    public ProtectionSummary Snapshot => _snapshot;

    /// <summary>Возвращает токен отписки — вызывать в Dispose компонента.</summary>
    public IDisposable Subscribe(Action handler)
    {
        Changed += handler;

        lock (_gate)
        {
            _subscribers++;
            if (_subscribers == 1)
                StartLoop();
        }

        return new Subscription(this, handler);
    }

    private void StartLoop()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await RefreshAsync(ct);

                try { await Task.Delay(_interval, ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var http = _factory.CreateClient("self");

            var data = await http.GetFromJsonAsync<ProtectionSummary>("/api/protection/summary", ct);
            if (data != null)
                _snapshot = data;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[CONSOLE] protection poll failed");
            _snapshot = new ProtectionSummary { Error = "Движок недоступен." };
        }

        try { Changed?.Invoke(); }
        catch (Exception ex) { _logger.LogDebug(ex, "[CONSOLE] subscriber threw"); }
    }

    private void Unsubscribe(Action handler)
    {
        Changed -= handler;

        lock (_gate)
        {
            _subscribers = Math.Max(0, _subscribers - 1);
            if (_subscribers > 0) return;

            try { _cts?.Cancel(); } catch { }
            _cts = null;
            _loop = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        return ValueTask.CompletedTask;
    }

    private sealed class Subscription : IDisposable
    {
        private readonly ProtectionUiService _owner;
        private readonly Action _handler;
        private bool _done;

        public Subscription(ProtectionUiService owner, Action handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _owner.Unsubscribe(_handler);
        }
    }
}
