using Binance.Net.Enums;
using VertexAutoTradeBinance8.Models;

namespace VertexAutoTradeBinance8.Services.Interface
{
    public interface IAccountStateService
    {
        AccountBalanceState GetBalance();
        IReadOnlyList<LivePositionState> GetPositions(string? symbolsCsv = null);

        LivePositionState? GetPosition(string symbol, PositionSide side);

        void UpsertPosition(LivePositionState p);
        void RemovePosition(string symbol, PositionSide side);

        void UpdateBalance(AccountBalanceState b);

        event Action? Updated;
    }
}
