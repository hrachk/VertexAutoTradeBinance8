using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models;
using Binance.Net.Objects.Models.Futures.Socket;
using CryptoExchange.Net.Objects.Sockets;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Web.Models;

namespace VertexAutoTradeBinance8.Web.Services;

public sealed class BinancePositionsWsService : IAsyncDisposable
{
    private readonly ILogger<BinancePositionsWsService> _logger;
    private readonly PositionsLiveService _live;
    private readonly BinanceClientFactory _factory;

    private BinanceRestClient? _rest;
    private BinanceSocketClient? _socket;

    private UpdateSubscription? _userDataSub;
    private UpdateSubscription? _markPriceSub;

    private string? _listenKey;
    private Timer? _listenKeyTimer;

    private static readonly TimeSpan ListenKeyRefreshInterval = TimeSpan.FromMinutes(30);

    private readonly HashSet<string> _markSymbols = new();
    private readonly object _markLock = new();

    public BinancePositionsWsService(
        ILogger<BinancePositionsWsService> logger,
        PositionsLiveService live,
        BinanceClientFactory factory)
    {
        _logger = logger;
        _live = live;
        _factory = factory;
    }

    // =========================================================
    // START
    // =========================================================

    private int _started = 0;

    public async Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        try
        {
            _rest = _factory.TryCreateRestClient();
            if (_rest == null)
            {
                _logger.LogWarning("[WS] Binance credentials missing — WS disabled");
                Interlocked.Exchange(ref _started, 0);
                return;
            }

            // === 1. SNAPSHOT (REST) ===
            await LoadInitialPositionsAsync();

            // === 2. SOCKET CLIENT ===
            _socket = new BinanceSocketClient();

            // === 3. LISTEN KEY ===
            _listenKey = await CreateListenKeyAsync();
            if (string.IsNullOrEmpty(_listenKey))
            {
                Interlocked.Exchange(ref _started, 0);
                await RestartWithDelay();
                return;
            }

            StartListenKeyRefresh();

            // === 4. USER DATA WS ===
            var userRes = await _socket.UsdFuturesApi.Account
                .SubscribeToUserDataUpdatesAsync(
                    _listenKey,
                    onAccountUpdate: OnAccountUpdate,
                    onOrderUpdate: _ => { },
                    onListenKeyExpired: ev =>
                    {
                        _logger.LogWarning("[WS] listenKey expired - restarting");
                        Task.Run(() => RestartAsync(3000));
                    });

            if (!userRes.Success)
            {
                _logger.LogError("[WS] UserData WS subscribe failed: {err}", userRes.Error);
                Interlocked.Exchange(ref _started, 0);
                await RestartWithDelay();
                return;
            }

            _userDataSub = userRes.Data;

            // === 5. MARK PRICE WS ===
            await SubscribeMarkPriceAsync();

            _logger.LogInformation("[WS] Binance positions WS started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WS] StartAsync fatal");
            Interlocked.Exchange(ref _started, 0);
            await RestartWithDelay();
        }
    }

    // =========================================================
    // MARK PRICE SUBSCRIBE (SAFE)
    // =========================================================
    private async Task SubscribeMarkPriceAsync()
    {
        var symbols = _live.GetActiveSymbols();

        lock (_markLock)
        {
            var newSymbols = symbols.Except(_markSymbols).ToList();
            if (newSymbols.Count == 0)
                return;

            foreach (var s in newSymbols)
                _markSymbols.Add(s);
        }

        // Переподписываемся полностью (Binance.Net не умеет add-symbol)
        if (_markPriceSub != null)
        {
            await _socket!.UnsubscribeAsync(_markPriceSub);
            _markPriceSub = null;
        }

        var res = await _socket!.UsdFuturesApi.ExchangeData
            .SubscribeToMarkPriceUpdatesAsync(
                symbols: _markSymbols.ToList(),
                updateInterval: 1000,
                data =>
                {
                    _live.UpdateMark(data.Data.Symbol, data.Data.MarkPrice);
                });

        if (!res.Success)
        {
            _logger.LogError("[WS] MarkPrice WS subscribe failed: {err}", res.Error);
            await RestartWithDelay();
            return;
        }

        _markPriceSub = res.Data;
        _logger.LogInformation("[WS] MarkPrice WS resubscribed symbols={cnt}", _markSymbols.Count);
    }


    // =========================================================
    // ACCOUNT UPDATE
    // =========================================================
    private void OnAccountUpdate(DataEvent<BinanceFuturesStreamAccountUpdate> ev)
    {
        foreach (var p in ev.Data.UpdateData.Positions)
        {
            if (p.Quantity == 0)
            {
                _live.Remove(p.Symbol);
                continue;
            }

            var side = p.Quantity > 0 ? "LONG" : "SHORT";

            var vm = new PositionVm
            {
                Symbol = p.Symbol,
                Side = side,

                Entry = p.EntryPrice,
                Mark = p.EntryPrice, // стартовое
                SizeUsdt = Math.Abs(p.Quantity * p.EntryPrice),

                // ❗ WS НЕ ДАЁТ ЭТИ ДАННЫЕ
                Margin = 0,          // будет заполнено REST / позже
                LiqPrice = 0,
                Leverage = 0,

                Pnl = p.UnrealizedPnl,
                Roi = 0,

                LastUpdateUtc = DateTime.UtcNow
            };

            _live.Upsert(vm);
        }

        // после появления новых позиций — убеждаемся, что Mark WS есть
        _ = SubscribeMarkPriceAsync();
    }

    // =========================================================
    // INITIAL SNAPSHOT (REST)
    // =========================================================
    private async Task LoadInitialPositionsAsync()
    {
        var res = await _rest!.UsdFuturesApi.Account.GetPositionInformationAsync();

        if (!res.Success)
        {
            _logger.LogError("[REST] Failed to load initial positions");
            return;
        }

        foreach (var p in res.Data)
        {
            if (p.Quantity == 0)
                continue;

            var side = p.Quantity > 0 ? "LONG" : "SHORT";

            var margin = p.MarginType == FuturesMarginType.Isolated
                ? p.IsolatedMargin
                : Math.Abs(p.Quantity * p.EntryPrice) / Math.Max(p.Leverage, 1);

            var vm = new PositionVm
            {
                Symbol = p.Symbol,
                Side = side,

                Entry = p.EntryPrice,
                Mark = p.MarkPrice,
                SizeUsdt = Math.Abs(p.Quantity * p.EntryPrice),

                Margin = margin,
                LiqPrice = p.LiquidationPrice,
                Leverage = p.Leverage,

                Pnl = p.UnrealizedPnl,
                Roi = 0,

                LastUpdateUtc = DateTime.UtcNow
            };

            _live.Upsert(vm);
        }

        _logger.LogInformation("[REST] Initial positions snapshot loaded");
    }

    // =========================================================
    // LISTEN KEY
    // =========================================================
    private async Task<string?> CreateListenKeyAsync()
    {
        var res = await _rest!.UsdFuturesApi.Account.StartUserStreamAsync();
        if (!res.Success || string.IsNullOrEmpty(res.Data))
        {
            _logger.LogError("[WS] Failed to create listenKey: {err}", res.Error);
            return null;
        }

        return res.Data;
    }

    private async Task RefreshListenKeyAsync()
    {
        if (_listenKey == null)
            return;

        var res = await _rest!.UsdFuturesApi.Account.KeepAliveUserStreamAsync(_listenKey);
        if (!res.Success)
            _logger.LogWarning("[WS] listenKey refresh failed");
    }

    private void StartListenKeyRefresh()
    {
        _listenKeyTimer = new Timer(_ =>
        {
            _ = Task.Run(RefreshListenKeyAsync);
        },
        null,
        ListenKeyRefreshInterval,
        ListenKeyRefreshInterval);
    }

    // =========================================================
    // RESTART / STOP
    // =========================================================
    private int _restarting = 0;

    private Task RestartWithDelay() => RestartAsync(5000);

    private async Task RestartAsync(int delayMs)
    {
        if (Interlocked.Exchange(ref _restarting, 1) == 1)
            return;

        try
        {
            await StopAsync();
            await Task.Delay(delayMs);
            await StartAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _restarting, 0);
        }
    }

    public async Task StopAsync()
    {
        _listenKeyTimer?.Dispose();
        _listenKeyTimer = null;

        if (_socket != null)
        {
            if (_userDataSub != null)
                await _socket.UnsubscribeAsync(_userDataSub);

            if (_markPriceSub != null)
                await _socket.UnsubscribeAsync(_markPriceSub);

            _socket.Dispose();
            _socket = null;
        }

        if (_listenKey != null)
        {
            try { await _rest!.UsdFuturesApi.Account.StopUserStreamAsync(_listenKey); }
            catch { }
        }

        _listenKey = null;
        _userDataSub = null;
        _markPriceSub = null;

        Interlocked.Exchange(ref _started, 0);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _rest?.Dispose();
    }
}
