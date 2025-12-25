using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models;
using Binance.Net.Objects.Models.Futures.Socket;
using CryptoExchange.Net.Objects.Sockets;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Services;
using VertexAutoTradeBinance8.Web.Models;
using VertexAutoTradeBinance8.Web.Pages.Components;

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

            // === 3. LISTEN KEY (ТОЛЬКО ДЛЯ USER DATA) ===
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
                        _ = RestartAsync();
                    });

            if (!userRes.Success)
            {
                _logger.LogError("[WS] UserData WS subscribe failed: {err}", userRes.Error);
                Interlocked.Exchange(ref _started, 0);
                await RestartWithDelay();
                return;
            }

            _userDataSub = userRes.Data;

            // === 5. MARK PRICE WS (PUBLIC STREAM) ===
            // Запускаем ТОЛЬКО если есть активные позиции
            var activeSymbols = _live.GetActiveSymbols();

            if (activeSymbols.Count == 0)
            {
                _logger.LogWarning("[WS] No ACTIVE positions — MarkPrice WS skipped (normal state)");
                _logger.LogInformation("[WS] Binance positions WS started (UserData only)");
                return; // ⛔ ВАЖНО: без рестарта, без ошибки
            }

            var markRes = await _socket.UsdFuturesApi.ExchangeData
                .SubscribeToMarkPriceUpdatesAsync(
                    symbols: activeSymbols,
                    updateInterval: 1000,
                    data =>
                    {
                        _live.UpdateMark(data.Data.Symbol, data.Data.MarkPrice);
                    });

            if (!markRes.Success)
            {
                _logger.LogError("[WS] MarkPrice WS subscribe failed: {err}", markRes.Error);
                Interlocked.Exchange(ref _started, 0);
                await RestartWithDelay();
                return;
            }

            _markPriceSub = markRes.Data;

            _logger.LogInformation(
                "[WS] Binance positions WS started (UserData + MarkPrice) symbols={cnt}",
                activeSymbols.Count);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WS] StartAsync fatal");
            Interlocked.Exchange(ref _started, 0);

            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                await RestartWithDelay();
            });
        }
        // === REST ===

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
    private async Task<string?> CreateListenKeyAsync()
    {
        var res = await _rest!.UsdFuturesApi.Account.StartUserStreamAsync();
        if (!res.Success || string.IsNullOrEmpty(res.Data))
        {
            _logger.LogError("[WS] Failed to create listenKey: {err}", res.Error);
            return null;
        }

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
        _listenKeyTimer = new Timer(_ =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await RefreshListenKeyAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[WS] listenKey refresh error");
                }
            });
        },
             null,
             ListenKeyRefreshInterval,
             ListenKeyRefreshInterval);

    }

    // =========================================================
    // RESTART / STOP
    // =========================================================
    private int _restarting = 0;
     
    private Task RestartWithDelay() => RestartAsync(delayMs: 5000);
    private async Task RestartAsync(int delayMs = 3000)
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

        var socket = _socket;
        if (socket != null && _userDataSub != null)
        {
            await socket.UnsubscribeAsync(_userDataSub);
        }

        if (socket != null && _markPriceSub != null)
        {
            await socket.UnsubscribeAsync(_markPriceSub);
        }

        if (_listenKey != null)
            try
            {
                await _rest!.UsdFuturesApi.Account.StopUserStreamAsync(_listenKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WS] StopUserStream failed");
            }

        _listenKey = null;
        _userDataSub = null;
        _markPriceSub = null;
        _socket?.Dispose();
        _socket = null;
        Interlocked.Exchange(ref _restarting, 0);
        Interlocked.Exchange(ref _started, 0);

    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _socket?.Dispose();
        _rest?.Dispose();
    }
 
}
