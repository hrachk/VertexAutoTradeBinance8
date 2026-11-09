using Binance.Net.Clients;
using Binance.Net.Enums;
using Microsoft.Extensions.Logging;
using VertexAutoTradeBinance8.Services.Interface;

namespace VertexAutoTradeBinance8.Services.Recovery;

public sealed class PositionRecoveryService : IPositionRecoveryService
{
    private readonly BinanceClientFactory _factory;
    private readonly ILogger<PositionRecoveryService> _logger;
    private readonly PositionSupervisorService _supervisor;

    public PositionRecoveryService(
        BinanceClientFactory factory,
        ILogger<PositionRecoveryService> logger,
        PositionSupervisorService supervisor)
    {
        _factory = factory;
        _logger = logger;
        _supervisor = supervisor;
    }

    public async Task<IReadOnlyList<RecoveredPosition>> RecoverOpenPositionsAsync(CancellationToken ct)
    {
        using var client = _factory.CreateRestClient();

        // 1) Positions (fast)
        var pos = await client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
        if (!pos.Success || pos.Data == null)
        {
            _logger.LogError("[RECOVERY] GetPositionInformation failed: {err}", pos.Error?.Message);
            throw new InvalidOperationException($"Cannot verify Binance positions during recovery: {pos.Error?.Message ?? "empty response"}");
        }

        var open = pos.Data
            .Where(p => p.Quantity != 0)
            .Select(p => new RecoveredPosition(
                Symbol: p.Symbol,
                // Binance returns BOTH in one-way mode. Preserve the
                // logical direction from the signed position amount so
                // recovery attaches long and short correctly.
                Side: p.PositionSide == PositionSide.Both
                    ? (p.Quantity > 0 ? PositionSide.Long : PositionSide.Short)
                    : p.PositionSide,
                Quantity: Math.Abs(p.Quantity),
                EntryPrice: p.EntryPrice,
                UnrealizedPnl: p.UnrealizedPnl,
                Leverage: p.Leverage
            ))
            .ToList();

        _logger.LogWarning("[RECOVERY] Open positions found: {count}", open.Count);
        foreach (var p in open)
        {
            _logger.LogWarning("[RECOVERY] {symbol} {side} qty={qty} entry={entry} upnl={upnl}",
                p.Symbol, p.Side, p.Quantity, p.EntryPrice, p.UnrealizedPnl);
        }

        return open;
    }

    public async Task ReconcileAndProtectAsync(CancellationToken ct)
    {
        if (!_factory.TryGetCredentials(out _, out _, out var source))
        {
            // Public market data and the Web's internal paper mode can still
            // run without private Binance credentials. There is no exchange
            // account we can inspect or alter in this state; live entries
            // remain impossible until credentials are configured.
            _logger.LogWarning("[RECOVERY] private Binance credentials unavailable; skip account recovery (source={source})", source);
            return;
        }

        var open = await RecoverOpenPositionsAsync(ct);
        if (open.Count == 0)
        {
            _logger.LogInformation("[RECOVERY] No open positions → nothing to protect.");
            return;
        }

        // ВАЖНО: Supervisor должен уметь "Attach/Sync" позицию и поставить emergency защиту.
        foreach (var p in open)
        {
            try
            {
                await _supervisor.AttachExistingPositionAsync(
                    symbol: p.Symbol,
                    side: p.Side,
                    qty: p.Quantity,
                    entryPrice: p.EntryPrice,
                    ct: ct);

                _logger.LogInformation("[RECOVERY] Attached to supervisor: {symbol} {side}", p.Symbol, p.Side);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RECOVERY] AttachExistingPosition failed {symbol} {side}", p.Symbol, p.Side);
                throw new InvalidOperationException($"Position recovery failed for {p.Symbol} {p.Side}; refusing to resume new entries.", ex);
            }
        }
    }
}
