using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models;
using Binance.Net.Objects.Models.Futures.Socket;
using CryptoExchange.Net.Objects.Sockets;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services.Interface;

namespace VertexAutoTradeBinance8.Services.Ws
{
    public sealed class BinanceUserDataSubscriber : IAsyncDisposable
    {
        private readonly ILogger<BinanceUserDataSubscriber> _logger;
        private readonly BinanceClientFactory _factory;
        private readonly IAccountStateService _state;
        private readonly MarketDataFacade _market;

        private BinanceSocketClient? _socket;
        private UpdateSubscription? _sub;
        private string? _listenKey;

        // =====================================================
        // listenKey keepalive: Binance требует продление каждые ~30 мин
        // (срок жизни = 60 мин, обновляем каждые 30 мин для надёжности)
        // =====================================================
        private Timer? _keepAliveTimer;
        private const int KeepAliveIntervalMinutes = 30;

        // =====================================================
        // Авто-рестарт при обрыве соединения
        // =====================================================
        private CancellationToken _startCt;
        private bool _isRestarting;

        public BinanceUserDataSubscriber(
            ILogger<BinanceUserDataSubscriber> logger,
            BinanceClientFactory factory,
            IAccountStateService state,
            MarketDataFacade market)
        {
            _logger = logger;
            _factory = factory;
            _state = state;
            _market = market;
        }

        // =============================================================
        // START
        // =============================================================
        public async Task StartAsync(CancellationToken ct = default)
        {
            _startCt = ct;

            using var rest = _factory.CreateRestClient();
            if (rest == null)
            {
                _logger.LogWarning("[USERDATA] REST disabled");
                return;
            }

            _logger.LogInformation("[USERDATA] REST ping starting...");
            var ping = await rest.UsdFuturesApi.Account.GetAccountInfoV3Async(ct: ct);
            _logger.LogInformation("[USERDATA] REST ping result: {ok}", ping.Success);

            // 1️⃣ Получаем listenKey (Futures USD-M)
            var lk = await rest.UsdFuturesApi.Account.StartUserStreamAsync(ct: ct);
            if (!lk.Success)
            {
                _logger.LogError("[USERDATA] listenKey error: {err}", lk.Error);
                return;
            }

            _listenKey = lk.Data;
            _logger.LogInformation("[USERDATA] listenKey obtained");

            // =====================================================
            // 🔥 BOOTSTRAP POSITIONS (INITIAL SNAPSHOT)
            // =====================================================
            await BootstrapPositionsAsync(rest, ct);

            // 2️⃣ Подписка на WS (дельты)
            _socket = _factory.CreateSocketClient();

            var res = await _socket.UsdFuturesApi.Account.SubscribeToUserDataUpdatesAsync(
                _listenKey,
                onLeverageUpdate:    ev => { },     // Config update — игнор
                onMarginUpdate:      ev => { },     // Margin update — игнор
                onAccountUpdate:     HandleAccountUpdate,
                onOrderUpdate:       HandleOrderUpdate,
                onListenKeyExpired:  HandleListenKeyExpired,
                ct: ct
            );

            if (!res.Success)
            {
                _logger.LogError("[USERDATA] WS subscribe failed: {err}", res.Error);
                return;
            }

            _sub = res.Data;

            // =====================================================
            // Обработка обрыва соединения — авто-рестарт
            // =====================================================
            _sub.ConnectionLost += OnConnectionLost;
            _sub.ConnectionRestored += OnConnectionRestored;

            // =====================================================
            // 3️⃣ Запускаем keepalive таймер (каждые 30 мин)
            // =====================================================
            StartKeepAliveTimer();

            _logger.LogInformation("[USERDATA] WS subscribed ✓ keepalive={min}min", KeepAliveIntervalMinutes);
        }

        // =============================================================
        // BOOTSTRAP POSITIONS
        // =============================================================
        private async Task BootstrapPositionsAsync(BinanceRestClient rest, CancellationToken ct)
        {
            var pos = await rest.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
            if (!pos.Success || pos.Data == null) return;

            foreach (var p in pos.Data)
            {
                if (p.PositionSide == PositionSide.Both) continue;
                if (p.Quantity == 0) continue;

                var state = new LivePositionState
                {
                    Symbol           = p.Symbol,
                    Side             = p.PositionSide,
                    Qty              = p.Quantity,
                    EntryPrice       = p.EntryPrice,
                    MarkPrice        = p.MarkPrice,
                    UnrealizedPnl    = p.UnrealizedPnl,
                    Notional         = Math.Abs(p.Quantity) * p.MarkPrice,
                    LiquidationPrice = p.LiquidationPrice,
                    IsolatedMargin   = p.IsolatedMargin,
                    Leverage         = p.Leverage
                };

                _state.UpsertPosition(state);
            }

            _logger.LogInformation("[USERDATA] Bootstrap positions loaded");
        }

        // =============================================================
        // KEEPALIVE TIMER
        // Binance: listenKey expires after 60 min without keepalive
        // PUT /fapi/v1/listenKey  — продлевает ещё на 60 мин
        // =============================================================
        private void StartKeepAliveTimer()
        {
            _keepAliveTimer?.Dispose();
            _keepAliveTimer = new Timer(
                async _ => await SendKeepAliveAsync(),
                null,
                TimeSpan.FromMinutes(KeepAliveIntervalMinutes),
                TimeSpan.FromMinutes(KeepAliveIntervalMinutes));
        }

        private async Task SendKeepAliveAsync()
        {
            if (string.IsNullOrEmpty(_listenKey)) return;

            try
            {
                using var rest = _factory.CreateRestClient();
                var result = await rest.UsdFuturesApi.Account.KeepAliveUserStreamAsync(_listenKey);

                if (result.Success)
                    _logger.LogDebug("[USERDATA] listenKey keepalive OK");
                else
                    _logger.LogWarning("[USERDATA] listenKey keepalive FAILED: {err} → restarting", result.Error);
                    // Если keepalive провалился — перезапускаем
                    await RestartAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[USERDATA] keepalive exception → restarting");
                await RestartAsync();
            }
        }

        // =============================================================
        // CONNECTION EVENTS
        // =============================================================
        private void OnConnectionLost()
        {
            _logger.LogWarning("[USERDATA] WS connection LOST");
        }

        private void OnConnectionRestored(TimeSpan downtime)
        {
            _logger.LogInformation("[USERDATA] WS connection RESTORED after {dt}s — re-bootstrapping positions", downtime.TotalSeconds);
            // Синхронизируем позиции после восстановления
            _ = Task.Run(async () =>
            {
                try
                {
                    using var rest = _factory.CreateRestClient();
                    await BootstrapPositionsAsync(rest, _startCt);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[USERDATA] Re-bootstrap after reconnect failed");
                }
            });
        }

        // =============================================================
        // ACCOUNT UPDATE
        // =============================================================
        private void HandleAccountUpdate(DataEvent<BinanceFuturesStreamAccountUpdate> ev)
        {
            var d = ev.Data;

            // -------- BALANCE --------
            try
            {
                var wallet = d.UpdateData.Balances.Sum(x => x.WalletBalance);
                var unreal = d.UpdateData.Positions.Sum(x => x.UnrealizedPnl);

                var bal = new AccountBalanceState
                {
                    WalletBalanceUsd    = wallet,
                    UnrealizedPnlUsd    = unreal,
                    EquityUsd           = wallet + unreal,
                    AvailableBalanceUsd = wallet,
                    UsedMarginUsd       = 0
                };

                _state.UpdateBalance(bal);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[USERDATA] balance parse failed");
            }

            // -------- POSITIONS --------
            foreach (var p in d.UpdateData.Positions)
            {
                var side = p.PositionSide;

                if (side == PositionSide.Both)
                {
                    if (p.Quantity > 0)      side = PositionSide.Long;
                    else if (p.Quantity < 0) side = PositionSide.Short;
                    else continue;
                }

                if (p.Quantity == 0)
                {
                    _state.RemovePosition(p.Symbol, side);
                    continue;
                }

                var qty  = Math.Abs(p.Quantity);
                var mark = p.EntryPrice;

                var pos = new LivePositionState
                {
                    Symbol        = p.Symbol,
                    Side          = side,
                    Qty           = qty,
                    EntryPrice    = p.EntryPrice,
                    UnrealizedPnl = p.UnrealizedPnl,
                    MarkPrice     = mark,
                    Notional      = qty * mark
                };

                _state.UpsertPosition(pos);
            }
        }

        // =============================================================
        // ORDER UPDATE
        // =============================================================
        private void HandleOrderUpdate(DataEvent<BinanceFuturesStreamOrderUpdate> ev)
        {
            _logger.LogDebug(
                "[USERDATA][ORDER] {sym} {side} {status}",
                ev.Data.UpdateData.Symbol,
                ev.Data.UpdateData.Side,
                ev.Data.UpdateData.Status);
        }

        // =============================================================
        // LISTEN KEY EXPIRED — автоматический рестарт
        // Binance шлёт событие listenKeyExpired когда ключ истёк
        // =============================================================
        private void HandleListenKeyExpired(DataEvent<BinanceStreamEvent> ev)
        {
            _logger.LogWarning("[USERDATA] listenKey EXPIRED → full restart");
            _ = Task.Run(() => RestartAsync());
        }

        // =============================================================
        // RESTART
        // =============================================================
        private async Task RestartAsync()
        {
            if (_isRestarting) return;
            _isRestarting = true;

            try
            {
                _logger.LogWarning("[USERDATA] Restarting...");
                await DisposeAsync();
                await Task.Delay(3000); // пауза перед переподключением
                await StartAsync(_startCt);
                _logger.LogInformation("[USERDATA] Restart complete ✓");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[USERDATA] Restart failed");
            }
            finally
            {
                _isRestarting = false;
            }
        }

        // =============================================================
        // DISPOSE
        // =============================================================
        public async ValueTask DisposeAsync()
        {
            _keepAliveTimer?.Dispose();
            _keepAliveTimer = null;

            try
            {
                if (_sub != null)
                {
                    _sub.ConnectionLost     -= OnConnectionLost;
                    _sub.ConnectionRestored -= OnConnectionRestored;
                    await _sub.CloseAsync();
                }
                _socket?.Dispose();
            }
            catch { }

            _sub      = null;
            _socket   = null;
            _listenKey = null;
        }
    }
}
