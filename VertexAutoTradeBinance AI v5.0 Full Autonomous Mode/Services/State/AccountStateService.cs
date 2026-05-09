using Binance.Net.Enums;
using System.Collections.Concurrent;
using VertexAutoTradeBinance8.Models;
using VertexAutoTradeBinance8.Services.Interface;

namespace VertexAutoTradeBinance8.Services.State
{
    /// <summary>
    /// Central Account State (канон):
    /// - единственный источник правды для Web/UI
    /// - обновляется Engine (WS user-data / bootstrap)
    /// - UI никогда не дергает Binance
    /// </summary>
    public sealed class AccountStateService : IAccountStateService
    {
        private readonly ILogger<AccountStateService> _logger;

        private readonly ConcurrentDictionary<string, LivePositionState> _pos = new();
        private AccountBalanceState _bal = new();

        public event Action? Updated;


        private decimal _realizedPnlSession = 0m;


        public AccountStateService(ILogger<AccountStateService> logger)
        {
            _logger = logger;
            _logger.LogCritical("[STATE] AccountStateService instance created: " + GetHashCode());
        }

        public decimal GetRealizedPnlSession() => _realizedPnlSession;

        public AccountBalanceState GetBalance() => _bal;

        public IReadOnlyList<LivePositionState> GetPositions()
            => _pos.Values.OrderBy(x => x.Symbol).ThenBy(x => x.Side).ToList();

        public void AddRealizedPnl(decimal pnl)
        {
            if (pnl == 0)
                return;

            _realizedPnlSession += pnl;

            _logger.LogInformation(
                "[STATE] RealizedPnL += {pnl}, session={session}",
                pnl,
                _realizedPnlSession);

            Updated?.Invoke();
        }
       

        public IReadOnlyList<LivePositionState> GetPositions(string? symbolsCsv = null)
        {
            if (string.IsNullOrWhiteSpace(symbolsCsv))
                return _pos.Values
                    .OrderBy(x => x.Symbol)
                    .ThenBy(x => x.Side)
                    .ToList();

            var set = symbolsCsv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.ToUpperInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return _pos.Values
                .Where(x => set.Contains(x.Symbol.ToUpperInvariant()))
                .OrderBy(x => x.Symbol)
                .ThenBy(x => x.Side)
                .ToList();
        }

        public LivePositionState? GetPosition(string symbol, PositionSide side)
        {
            _pos.TryGetValue(LivePositionState.Key(symbol, side), out var v);
            return v;
        }

        public void UpsertPosition(LivePositionState p)
        {
            if (string.IsNullOrWhiteSpace(p.Symbol) || p.Side == PositionSide.Both)
                return;

            p.Symbol = p.Symbol.ToUpperInvariant();
            p.LastUpdateUtc = DateTime.UtcNow;

            _pos[LivePositionState.Key(p.Symbol, p.Side)] = p;

            _logger.LogWarning(
            "[STATE] UPSERT {sym} {side} qty={qty}",
            p.Symbol, p.Side, p.Qty);


            Updated?.Invoke();
        }
 
        public void RemovePosition(string symbol, PositionSide side)
        {
            if (string.IsNullOrWhiteSpace(symbol) || side == PositionSide.Both)
                return;

            _pos.TryRemove(LivePositionState.Key(symbol.ToUpperInvariant(), side), out _);
            Updated?.Invoke();
        }

        public void UpdateBalance(AccountBalanceState b)
        {
            b.LastUpdateUtc = DateTime.UtcNow;

            // атомарно заменяем ссылку
            _bal = b;
            Updated?.Invoke();
        }
    }
}
