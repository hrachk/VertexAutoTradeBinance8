using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures.Socket;
using CryptoExchange.Net.Objects.Sockets;
using Microsoft.Extensions.Logging;
using System.Threading;
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
            using var rest = _factory.TryCreateRestClient();
            if (rest == null)
            {
                _logger.LogWarning("[USERDATA] REST disabled");
                return;
            }

            var lk = await rest.UsdFuturesApi.Account.StartUserStreamAsync(ct);
            if (!lk.Success)
            {
                _logger.LogError("[USERDATA] listenKey error {err}", lk.Error);
                return;
            }

            _listenKey = lk.Data;
            _socket = new BinanceSocketClient();

            var res = await _socket.UsdFuturesApi.Account.SubscribeToUserDataUpdatesAsync(
                _listenKey,
                ev => { }, // Config update — игнор
                ev => { }, // Margin update — позже
                HandleAccountUpdate,
                HandleOrderUpdate,
                ct:ct
            );

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
                    AvailableBalanceUsd = wallet, // Futures WS не даёт AB
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

                if (side == PositionSide.Both)
                {
                    if (p.Quantity > 0)
                        side = PositionSide.Long;
                    else if (p.Quantity < 0)
                        side = PositionSide.Short;
                    else
                        continue;
                }


                if (p.Quantity == 0)
                {
                    _state.RemovePosition(p.Symbol, p.PositionSide);
                    continue;
                }

                // MarkPrice берём из MarketData
               // var mark = _market.GetLastMarkPrice(p.Symbol) ?? p.EntryPrice;

                var pos = new LivePositionState
                {
                    Symbol = p.Symbol,
                    Side = side,
                    Qty = p.Quantity,
                    EntryPrice = p.EntryPrice,
                    UnrealizedPnl = p.UnrealizedPnl,
                    MarkPrice = 0m,
                    Notional = 0m
                };

                _state.UpsertPosition(pos);
            }
        }

        // =============================================================
        // ORDER UPDATE (пока лог)
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
            catch { }

            _sub = null;
            _socket = null;
            _listenKey = null;
        }
    }
}
