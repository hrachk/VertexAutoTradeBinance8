using Binance.Net.Enums;

namespace VertexAutoTradeBinance8.Services.Recovery;

public interface IPositionRecoveryService
{
    Task<IReadOnlyList<RecoveredPosition>> RecoverOpenPositionsAsync(CancellationToken ct);

    // Мягкий режим: можно вызвать повторно, безопасно.
    Task ReconcileAndProtectAsync(CancellationToken ct);
}

public sealed record RecoveredPosition(
    string Symbol,
    PositionSide Side,
    decimal Quantity,
    decimal EntryPrice,
    decimal UnrealizedPnl,
    int Leverage
);
