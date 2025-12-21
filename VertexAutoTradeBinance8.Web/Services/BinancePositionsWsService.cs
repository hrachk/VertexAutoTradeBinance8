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
    public async Task StartAsync()
    {
        // === REST ===
        _rest = _factory.TryCreateRestClient();
        if (_rest == null)
        {
            _logger.LogWarning("[WS] Binance credentials missing — WS disabled");
            return;
        }

        // === 1. SNAPSHOT (REST) ===
        await LoadInitialPositionsAsync();

        // === 2. SOCKET CLIENT ===
        _socket = new BinanceSocketClient();

        // === 3. LISTEN KEY (ТОЛЬКО ДЛЯ USER DATA) ===
        _listenKey = await CreateListenKeyAsync();
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
                    _ = RestartAsync();
                });

        if (!userRes.Success)
            throw new Exception("UserData WS subscribe failed");

        _userDataSub = userRes.Data;

        // === 5. MARK PRICE WS (PUBLIC STREAM) ===
        var markRes = await _socket.UsdFuturesApi.ExchangeData
     .SubscribeToMarkPriceUpdatesAsync(
         symbols: null,
         updateInterval: 1000,
         data =>
         {
             _live.UpdateMark(data.Data.Symbol, data.Data.MarkPrice);
         });


        if (!markRes.Success)
            throw new Exception("MarkPrice WS subscribe failed");

        _markPriceSub = markRes.Data;

        _logger.LogInformation("[WS] Binance positions WS started");
    }


    // =========================================================
    // ACCOUNT UPDATE
    // =========================================================
    private void OnAccountUpdate(
     DataEvent<BinanceFuturesStreamAccountUpdate> ev)
    {
        var update = ev.Data;

        foreach (var p in update.UpdateData.Positions)
        {
            if (p.Quantity == 0)
            {
                _live.Remove(p.Symbol);   // ← УДАЛЯЕМ ЗАКРЫТУЮ ПОЗИЦИЮ ИЗ UI
                continue;
            }

            var side = p.Quantity > 0 ? "LONG" : "SHORT";

            var vm = new PositionVm
            {
                Symbol = p.Symbol,
                Side = side,
                Entry = p.EntryPrice,
                SizeUsdt = Math.Abs(p.Quantity * p.EntryPrice),
                Pnl = p.UnrealizedPnl,

                // ❗ Mark / ROI / MarginRatio — НЕ ТУТ
                Roi = 0
            };

            _live.Upsert(vm);
        }
    }
     

    private async Task LoadInitialPositionsAsync()
    {
        _logger.LogWarning("[DEBUG] LoadInitialPositionsAsync CALLED");

        var res = await _rest!.UsdFuturesApi.Account.GetPositionInformationAsync();
        _logger.LogWarning("[DEBUG] REST positions count = {cnt}", res.Data.Count());


        if (!res.Success)
        {
            _logger.LogError("[REST] Failed to load initial positions");
            return;
        }

        foreach (var p in res.Data)
        {
            if (p.Quantity == 0)
                continue; // ❗ нет позиции — не показываем

            var side = p.Quantity > 0 ? "LONG" : "SHORT";
            var vm = new PositionVm
            {
                Symbol = p.Symbol,
                Side = side,

                Entry = p.EntryPrice,
                Pnl = p.UnrealizedPnl,

                Margin = p.MarginType == FuturesMarginType.Isolated
    ? p.IsolatedMargin
    : Math.Abs(p.Quantity * p.EntryPrice) / Math.Max(p.Leverage, 1),

                LiqPrice = p.LiquidationPrice,
                SizeUsdt = Math.Abs(p.Quantity * p.EntryPrice),

                Roi = 0 // пересчитается после Mark
            };


            _live.Upsert(vm);
        }

        _logger.LogInformation("[REST] Initial positions snapshot loaded");
    }

    // =========================================================
    // LISTEN KEY
    // =========================================================
    private async Task<string> CreateListenKeyAsync()
    {
        var res = await _rest!.UsdFuturesApi.Account.StartUserStreamAsync();
        if (!res.Success || string.IsNullOrEmpty(res.Data))
            throw new Exception("Failed to create listenKey");

        _logger.LogInformation("[WS] listenKey created");
        return res.Data;
    }

    private async Task RefreshListenKeyAsync()
    {
        if (_listenKey == null)
            return;

        var res = await _rest!.UsdFuturesApi.Account
            .KeepAliveUserStreamAsync(_listenKey);

        if (!res.Success)
            _logger.LogWarning("[WS] listenKey refresh failed");
    }

    private void StartListenKeyRefresh()
    {
        _listenKeyTimer = new Timer(
            async _ =>
            {
                try { await RefreshListenKeyAsync(); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[WS] listenKey refresh error");
                }
            },
            null,
            ListenKeyRefreshInterval,
            ListenKeyRefreshInterval);
    }

    // =========================================================
    // RESTART / STOP
    // =========================================================
    private async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    public async Task StopAsync()
    {
        _listenKeyTimer?.Dispose();
        _listenKeyTimer = null;

        if (_userDataSub != null)
            await _socket!.UnsubscribeAsync(_userDataSub);

        if (_markPriceSub != null)
            await _socket!.UnsubscribeAsync(_markPriceSub);

        if (_listenKey != null)
            await _rest!.UsdFuturesApi.Account.StopUserStreamAsync(_listenKey);

        _listenKey = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _socket?.Dispose();
        _rest?.Dispose();
    }
}
