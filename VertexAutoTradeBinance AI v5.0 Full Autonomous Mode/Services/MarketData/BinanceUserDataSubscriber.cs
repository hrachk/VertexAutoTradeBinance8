using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures.Socket;
using CryptoExchange.Net.Objects.Sockets;
using VertexAutoTradeBinance8.MarketData;
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
            // CORE path: REST must be present, no "disabled" fallback here
            using var rest = _factory.CreateRestClient();

            // ---- AUTH CHECK (once, explicit) ----
            _logger.LogInformation("[USERDATA] Futures REST auth check...");
            var account = await rest.UsdFuturesApi.Account.GetAccountInfoV3Async(ct: ct);
            if (!account.Success)
            {
                _logger.LogCritical(
                    "[USERDATA] FUTURES REST AUTH FAILED → user-data disabled ({err})",
                    account.Error);
                return;
            }
            _logger.LogInformation("[USERDATA] Futures REST auth OK");

            // 1) listenKey
            var lk = await rest.UsdFuturesApi.Account.StartUserStreamAsync(ct: ct);
            if (!lk.Success)
            {
                _logger.LogError("[USERDATA] listenKey error {err}", lk.Error);
                return;
            }

            _listenKey = lk.Data;

            // 2) BOOTSTRAP POSITIONS (initial snapshot)
            var pos = await rest.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
            if (!pos.Success)
            {
                _logger.LogError("[USERDATA] GetPositionInformation failed {err}", pos.Error);
            }
            else if (pos.Data != null)
            {
                foreach (var p in pos.Data)
                {
                    if (p.PositionSide == PositionSide.Both) continue;
                    if (p.Quantity == 0) continue;

                    var qtyAbs = Math.Abs(p.Quantity);

                    var state = new LivePositionState
                    {
                        Symbol = p.Symbol,
                        Side = p.PositionSide,
                        Qty = qtyAbs,
                        EntryPrice = p.EntryPrice,
                        MarkPrice = p.MarkPrice > 0 ? p.MarkPrice : p.EntryPrice,
                        UnrealizedPnl = p.UnrealizedPnl,
                        Notional = qtyAbs * (p.MarkPrice > 0 ? p.MarkPrice : p.EntryPrice),
                        LiquidationPrice = p.LiquidationPrice,
                        IsolatedMargin = p.IsolatedMargin,
                        Leverage = p.Leverage
                    };

                    _state.UpsertPosition(state);
                }
            }

            // 3) WS subscribe (PRIVATE WS, with keys)
            _socket = _factory.CreatePrivateSocketClient();

            var res = await _socket.UsdFuturesApi.Account.SubscribeToUserDataUpdatesAsync(
                _listenKey,
                ev => { }, // Config update — ignore
                ev => { }, // Margin update — later
                HandleAccountUpdate,
                HandleOrderUpdate,
                ct: ct);

            if (!res.Success)
            {
                _logger.LogError("[USERDATA] subscribe failed {err}", res.Error);
                return;
            }

            _sub = res.Data;
            _logger.LogInformation("[USERDATA] WS subscribed");
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
                    WalletBalanceUsd = wallet,
                    UnrealizedPnlUsd = unreal,
                    EquityUsd = wallet + unreal,
                    AvailableBalanceUsd = wallet, // WS doesn't provide available balance cleanly
                    UsedMarginUsd = 0
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

                // Normalize "Both" (rare in hedge context, but keep safe)
                if (side == PositionSide.Both)
                {
                    if (p.Quantity > 0) side = PositionSide.Long;
                    else if (p.Quantity < 0) side = PositionSide.Short;
                    else continue;
                }

                if (p.Quantity == 0)
                {
                    _state.RemovePosition(p.Symbol, side);
                    continue;
                }

                var qtyAbs = Math.Abs(p.Quantity);

                // ❗ MarkPrice НЕ приходит в UserData WS
                // используем EntryPrice как нейтральную базу
                var mark = p.EntryPrice;

                var pos = new LivePositionState
                {
                    Symbol = p.Symbol,
                    Side = side,
                    Qty = qtyAbs,
                    EntryPrice = p.EntryPrice,
                    UnrealizedPnl = p.UnrealizedPnl,
                    MarkPrice = mark,
                    Notional = qtyAbs * mark
                };

                _state.UpsertPosition(pos);
            }
        }

        // =============================================================
        // ORDER UPDATE (log-only for now)
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
        // LISTEN KEY EXPIRED
        // =============================================================
        private async void HandleListenKeyExpired()
        {
            _logger.LogWarning("[USERDATA] listenKey expired → restart");
            await DisposeAsync();
            await StartAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_sub != null)
                    await _sub.CloseAsync();

                _socket?.Dispose();
            }
            catch
            {
                // ignore
            }

            _sub = null;
            _socket = null;
            _listenKey = null;
        }
    }
}
